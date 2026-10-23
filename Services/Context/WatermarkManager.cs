using System.Collections.Concurrent;
using RainBot.Services.Config;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;

namespace RainBot.Services.Context;

/// <summary>
/// 历史完整渲染量达到软水位时压缩旧前缀，保留近期原文，不进入降级。
/// 正常压缩失败保留历史并冷却重试；最终输入异常超水位才使用原有降级兜底。
/// Compacted / Distilled / Reset 后调用方重新组装一次。
/// </summary>
public class WatermarkManager(
    RuntimeConfig config,
    Distiller distiller,
    HistoryStore historyStore,
    GroupStateManager states,
    ILogger<WatermarkManager> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly Distiller _distiller = distiller;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly GroupStateManager _states = states;
    private readonly ILogger<WatermarkManager> _logger = logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _compressionRetryAfter = new();

    /// <summary>
    /// 降级恢复检查（由 WarmupScheduler 定时扫描调用）：静默足够长后彻底重置上下文。
    /// 返回是否执行了重置。
    /// </summary>
    public async Task<bool> TryRecoverDegradedAsync(string groupOpenId)
    {
        var cfg = _config.Config.Context;
        GroupState state = _states.GetOrCreate(groupOpenId);
        if (!state.Degraded)
        {
            return false;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan silence = state.LastMessageUtc == DateTimeOffset.MinValue
            ? TimeSpan.MaxValue
            : now - state.LastMessageUtc;
        if (silence >= TimeSpan.FromMinutes(cfg.DegradeResetSilenceMinutes))
        {
            await _states.ResetContextAsync(groupOpenId);
            _logger.LogInformation("群 {Group} 已静默 {Minutes:0} 分钟，上下文彻底重置，重建缓存", groupOpenId, silence.TotalMinutes);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 水位治理；调用方已持有会话锁。压缩或重置后重新组装上下文。
    /// </summary>
    public async Task<WatermarkAction> EnforceAsync(ComposeResult compose, CancellationToken ct = default)
    {
        var cfg = _config.Config.Context;
        GroupState state = _states.GetOrCreate(compose.GroupOpenId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 降级恢复：静默足够长后彻底重置上下文、重建缓存
        if (state.Degraded)
        {
            TimeSpan silence = state.LastMessageUtc == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : now - state.LastMessageUtc;
            if (silence >= TimeSpan.FromMinutes(cfg.DegradeResetSilenceMinutes))
            {
                await _states.ResetContextAsync(compose.GroupOpenId, acquireLock: false);
                _logger.LogInformation("群 {Group} 已静默 {Minutes:0} 分钟，上下文彻底重置，重建缓存", compose.GroupOpenId, silence.TotalMinutes);
                return WatermarkAction.Reset;
            }
            return WatermarkAction.DegradedSkipped;
        }

        // 检查裁剪前的完整历史；正常压缩不降级、不清空最近对话。
        int trigger = Math.Max(1, Math.Min(cfg.HistoryCompressAtTokens, compose.HistoryBudgetTokens > 0
            ? compose.HistoryBudgetTokens : cfg.HistoryAssembleCapTokens));
        if (compose.UntrimmedHistoryTokens >= trigger && compose.HistorySnapshot.Count > 1
            && (!_compressionRetryAfter.TryGetValue(compose.GroupOpenId, out var retryAfter) || now >= retryAfter))
        {
            int target = Math.Clamp(cfg.HistoryTargetTokens, 0, trigger - 1);
            var retained = BlockComposer.SelectHistoryTail(compose.HistorySnapshot, target);
            int keep = Math.Max(1, retained.Count);
            int desiredRemove = compose.HistorySnapshot.Count - keep;
            // 长时间未触发回复时可能积累远超软水位的历史；分批处理旧前缀，
            // 给压缩器自身的提示与已有摘要留空间，避免永远因输入超限而失败。
            int batchBudget = Math.Max(1, cfg.WatermarkTokens / 2);
            int batchTokens = TokenEstimator.Estimate(HistoryFormatter.Header);
            int remove = 0;
            while (remove < desiredRemove)
            {
                int next = TokenEstimator.Estimate(HistoryFormatter.Format(compose.HistorySnapshot[remove]));
                if ((long)batchTokens + next > batchBudget) break;
                batchTokens += next;
                remove++;
            }
            keep = compose.HistorySnapshot.Count - remove;
            if (remove > 0)
            {
                _compressionRetryAfter[compose.GroupOpenId] = now.AddMinutes(5);
                string summary = await _distiller.CompressAsync(compose.GroupOpenId,
                    compose.HistorySnapshot.Take(remove).ToList(), ct, consolidate: true);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    _historyStore.CompactSnapshot(compose.GroupOpenId, compose.HistorySnapshot, keep);
                    _compressionRetryAfter.TryRemove(compose.GroupOpenId, out _);
                    _logger.LogInformation("群 {Group} 正常压缩：历史≈{Before} tokens，保留 {Keep} 条及新到消息，不进入降级",
                        compose.GroupOpenId, compose.UntrimmedHistoryTokens, keep);
                    return WatermarkAction.Compacted;
                }
                _logger.LogWarning("群 {Group} 正常压缩失败，原历史保留，五分钟后重试", compose.GroupOpenId);
            }
        }

        // 强制压缩：突破水位线
        if (compose.EstimatedTokens > cfg.WatermarkTokens)
        {
            await DistillAndDegradeAsync(compose);
            return WatermarkAction.Distilled;
        }

        return WatermarkAction.None;
    }

    private async Task DistillAndDegradeAsync(ComposeResult compose)
    {
        var cfg = _config.Config.Context;
        // 1. 抓取快照并交给后台消费者；LLM 不占用回复关键路径。
        List<HistoryEntry> history = [];
        // 从 HistoryStore 取全量历史供蒸馏（超过保留上限的部分本就已丢弃，取现有即可）
        history = _historyStore.GetRecent(compose.GroupOpenId, int.MaxValue, out _);
        _distiller.Enqueue(compose.GroupOpenId, history);

        // 2. 清空 Block E，仅保留最近 N 条
        _historyStore.ClearKeep(compose.GroupOpenId, cfg.DistillKeepMessages);

        // 3. 降级运行：提高触发阈值、延长冷却（ActiveTrigger/PassiveTrigger 按 Degraded 调整）
        await _states.SetDegradedAsync(compose.GroupOpenId, true);
        _logger.LogWarning("群 {Group} 已强制压缩并降级运行（蒸馏 {Count} 条历史，保留 {Keep} 条，等待静默 {Minutes} 分钟后重置）",
            compose.GroupOpenId, history.Count, cfg.DistillKeepMessages, cfg.DegradeResetSilenceMinutes);
    }
}

public enum WatermarkAction
{
    /// <summary>无需处理</summary>
    None,

    /// <summary>正常压缩成功，需重建上下文，不进入降级。</summary>
    Compacted,

    /// <summary>已截取蒸馏快照并降级，需重建上下文；摘要后台生成</summary>
    Distilled,

    /// <summary>已彻底重置上下文（降级恢复）</summary>
    Reset,

    /// <summary>降级中，本次跳过（静默未满，不触发工作流）</summary>
    DegradedSkipped
}

