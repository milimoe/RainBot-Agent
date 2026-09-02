using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Context;
using RainBot.Services.Topic;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;

namespace RainBot.Services.Scheduler;

/// <summary>
/// 主动暖群调度器：定时扫描所有群，评估密度唤醒 / 沉默唤醒 / 频控，
/// 触发暖群工作流；同时执行降级恢复（静默足够后重置上下文）。
/// </summary>
public class WarmupScheduler(
    GroupStateManager states,
    ActiveTrigger activeTrigger,
    WatermarkManager watermarkManager,
    HistoryStore historyStore,
    TopicAnalyzer topicAnalyzer,
    WorkflowRunner workflowRunner,
    ILogger<WarmupScheduler> logger) : BackgroundService
{
    private readonly GroupStateManager _states = states;
    private readonly ActiveTrigger _activeTrigger = activeTrigger;
    private readonly WatermarkManager _watermarkManager = watermarkManager;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly TopicAnalyzer _topicAnalyzer = topicAnalyzer;
    private readonly WorkflowRunner _workflowRunner = workflowRunner;
    private readonly ILogger<WarmupScheduler> _logger = logger;

    /// <summary>扫描周期</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动先等待一个周期，等消息队列与数据库就绪
        await Task.Delay(ScanInterval, stoppingToken);
        using PeriodicTimer timer = new(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "暖群扫描异常");
            }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (GroupState state in _states.AllStates())
        {
            // 全新群（尚无消息）不打扰
            if (state.LastMessageUtc == DateTimeOffset.MinValue)
            {
                continue;
            }
            // 私聊不主动暖场（只有对方说话才回）
            if (state.IsPrivate || BotKeys.IsPrivateKey(state.GroupOpenId))
            {
                continue;
            }

            // 降级恢复：静默足够后彻底重置上下文
            if (await _watermarkManager.TryRecoverDegradedAsync(state.GroupOpenId))
            {
                continue;
            }

            // 暖群判定（密度/沉默/频控/静默/降级）
            string? reason = _activeTrigger.Evaluate(state.GroupOpenId, now);
            if (reason == null)
            {
                continue;
            }

            // 话题分析 → 暖群提示（进 Block F）
            List<HistoryEntry> recent = _historyStore.GetRecent(state.GroupOpenId, int.MaxValue, out _);
            string hint = _topicAnalyzer.Analyze(recent);

            TriggerContext ctx = new()
            {
                BotId = BotKeys.GetBotId(state.GroupOpenId),
                GroupOpenId = state.GroupOpenId,
                Type = TriggerType.Warmup,
                Reason = reason,
                WarmupHint = hint,
                AllowProfileUpdate = true // 仅暖群工作流允许沉淀画像
            };

            _logger.LogInformation("群 {Group} 触发主动暖群：{Reason}", state.GroupOpenId, reason);
            bool sent = await _workflowRunner.RunAsync(ctx, null, ct);
            // 无论是否真正发言都记录尝试时间（ActiveTrigger 用它做失败重试冷却，防空转）
            state.LastWarmupAttemptUtc = now;
            if (sent)
            {
                _activeTrigger.MarkActive(state.GroupOpenId, now);
            }
            else
            {
                _logger.LogInformation("群 {Group} 暖群未产出内容，静默跳过（等待重试冷却后再试）", state.GroupOpenId);
            }
        }
    }
}
