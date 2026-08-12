using RainBot.Models;
using RainBot.Services.Config;

namespace RainBot.Services.Trigger;

/// <summary>
/// 被动触发判定：@ 机器人 / 回复机器人 → 即时响应，但受每群强冷却限制。
/// 冷却期内消息仍会进入统计与队列，只是不唤醒。
/// </summary>
public class PassiveTrigger(RuntimeConfig config, GroupStateManager states, ILogger<PassiveTrigger> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly GroupStateManager _states = states;
    private readonly ILogger<PassiveTrigger> _logger = logger;

    /// <summary>
    /// 判断该消息是否应触发被动工作流。
    /// </summary>
    public bool ShouldTrigger(IncomingMessage message, DateTimeOffset now)
    {
        if (!message.IsAtRobot)
        {
            return false;
        }

        GroupState state = _states.GetOrCreate(message.GroupOpenId);

        // 管理员 @ 无视冷却（管理员指令需要即时响应）
        if (message.IsAdmin)
        {
            return true;
        }

        int cooldownSeconds = _config.Config.Trigger.PassiveCooldownSeconds;
        if (state.LastPassiveTriggerUtc + TimeSpan.FromSeconds(cooldownSeconds) > now)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("群 {Group} 处于冷却期（{Seconds}s），@ 消息暂不唤醒", message.GroupOpenId, cooldownSeconds);
            return false;
        }
        return true;
    }

    /// <summary>标记该群已发生被动触发（进入冷却）</summary>
    public void MarkTriggered(string groupOpenId, DateTimeOffset now)
    {
        GroupState state = _states.GetOrCreate(groupOpenId);
        state.LastPassiveTriggerUtc = now;
    }
}
