using RainBot.Services.Config;

namespace RainBot.Services.Trigger;

/// <summary>
/// 主动暖群判定（由 WarmupScheduler 定时扫描调用）：
/// - 密度唤醒：X 分钟内消息数 ≥ Y 且话题未终结
/// - 沉默唤醒：群聊静默 ≥ X 分钟且无管理员发言
/// - 频控：单群每小时主动发言 ≤ X 次
/// - 降级期：阈值提高（密度 ×2、静默 ×4），冷却内不与被动响应冲突
/// </summary>
public class ActiveTrigger(RuntimeConfig config, GroupStateManager states, ILogger<ActiveTrigger> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly GroupStateManager _states = states;
    private readonly ILogger<ActiveTrigger> _logger = logger;

    /// <summary>评估该群当前是否应暖群，返回原因；不触发返回 null</summary>
    public string? Evaluate(string groupOpenId, DateTimeOffset now)
    {
        var cfg = _config.Config.Trigger;
        GroupState state = _states.GetOrCreate(groupOpenId);
        string? reason = null;

        // 全局/群静默检查
        if (state.Muted == true)
        {
            return null;
        }

        // 被动冷却期内不与暖群冲突
        if (state.LastPassiveTriggerUtc + TimeSpan.FromSeconds(cfg.PassiveCooldownSeconds) > now)
        {
            return null;
        }

        // 降级运行：静默期内不暖群，等待彻底重置
        if (state.Degraded)
        {
            return null;
        }

        // 频控：1 小时内主动发言次数
        int activeCount = state.ActiveWindow.Count(t => now - t <= TimeSpan.FromHours(1));
        if (activeCount >= cfg.ActivePerHour)
        {
            return null;
        }

        // 密度唤醒：窗口内消息数 ≥ 阈值 且 话题未终结（最后消息在存活期内）
        TimeSpan densityWindow = TimeSpan.FromMinutes(cfg.DensityWindowMinutes);
        int windowCount = state.DensityWindow.Count(t => now - t <= densityWindow);
        bool topicAlive = state.LastMessageUtc != DateTimeOffset.MinValue
            && now - state.LastMessageUtc <= TimeSpan.FromMinutes(cfg.TopicAliveMinutes);
        if (windowCount >= cfg.DensityThreshold && topicAlive)
        {
            reason = $"群内 {cfg.DensityWindowMinutes} 分钟内消息 {windowCount} 条";
        }

        // 沉默唤醒：静默 ≥ X 分钟 且 该窗口内无管理员发言
        if (reason == null && state.LastMessageUtc != DateTimeOffset.MinValue)
        {
            TimeSpan silence = now - state.LastMessageUtc;
            TimeSpan silenceThreshold = TimeSpan.FromMinutes(cfg.SilenceMinutes);
            bool adminSpokeRecently = state.LastAdminMessageUtc + silenceThreshold > now;
            if (silence >= silenceThreshold && !adminSpokeRecently)
            {
                reason = $"群聊已静默 {silence.TotalMinutes:0.#} 分钟";
            }
        }

        if (reason != null)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("群 {Group} 触发主动暖群：{Reason}", groupOpenId, reason);
        }
        return reason;
    }

    /// <summary>记录一次主动发言（频控计数）</summary>
    public void MarkActive(string groupOpenId, DateTimeOffset now)
    {
        GroupState state = _states.GetOrCreate(groupOpenId);
        state.ActiveWindow.Add(now);
        // 清理 1 小时前的记录，防止无限增长
        state.ActiveWindow.RemoveAll(t => now - t > TimeSpan.FromHours(1));
    }
}
