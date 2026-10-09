using RainBot.Services.Config;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;

namespace RainBot.Services.Context;

/// <summary>
/// 192k 水位管理（PRD 3.3）：
/// - 压缩前防御：BlockComposer 已按预算删 E 头部、缩 D 锚点（组装阶段完成）。
/// - 强制压缩：蒸馏长历史（3-5 条摘要存库）→ 清空 E 仅留最近 N 条 → 降级运行。
/// - 降级恢复：群聊静默足够长后彻底重置上下文，重建缓存。
/// 返回是否需要重建上下文的标志（蒸馏后 E 已清空，调用方重新组装一次）。
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
            _historyStore.ClearAll(groupOpenId);
            await _states.ResetContextAsync(groupOpenId);
            _logger.LogInformation("群 {Group} 已静默 {Minutes:0} 分钟，上下文彻底重置，重建缓存", groupOpenId, silence.TotalMinutes);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 水位治理：返回处理动作。Distilled 时调用方应重新组装上下文。
    /// </summary>
    public async Task<WatermarkAction> EnforceAsync(ComposeResult compose)
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
                _historyStore.ClearAll(compose.GroupOpenId);
                await _states.ResetContextAsync(compose.GroupOpenId);
                _logger.LogInformation("群 {Group} 已静默 {Minutes:0} 分钟，上下文彻底重置，重建缓存", compose.GroupOpenId, silence.TotalMinutes);
                return WatermarkAction.Reset;
            }
            return WatermarkAction.DegradedSkipped;
        }

        // 强制压缩：突破水位线
        if (compose.EstimatedTokens > cfg.WatermarkTokens)
        {
            await DistillAndDegradeAsync(compose);
            return WatermarkAction.Distilled;
        }

        // 防御性蒸馏：历史已占用巨大预算（接近水位），提前蒸馏避免失控
        if (compose.HistoryTokens > cfg.HistoryAssembleCapTokens * 0.6)
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

    /// <summary>已截取蒸馏快照并降级，需重建上下文；摘要后台生成</summary>
    Distilled,

    /// <summary>已彻底重置上下文（降级恢复）</summary>
    Reset,

    /// <summary>降级中，本次跳过（静默未满，不触发工作流）</summary>
    DegradedSkipped
}
