using RainBot.Models;
using RainBot.Services.Config;

namespace RainBot.Services.Trigger;

/// <summary>
/// 被动触发判定：@ 机器人 / 回复机器人 → 即时响应，但受每群强冷却限制。
/// 冷却期内消息仍会进入统计与队列，只是不唤醒。
/// 另提供随机插嘴判定：普通群消息（未 @）按概率触发人设回复，像群友一样搭话，
/// 受概率 + 独立冷却 + 被动冷却互斥 + 静默群限制（@ 响应永远优先，不被插嘴挤占）。
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

    /// <summary>
    /// 随机插嘴判定：普通群消息（未 @、非私聊、非敏感词）按概率触发人设回复。
    /// 防刷屏四道闸：概率（密度相关）→ 插嘴独立冷却（硬上限）→ 被动冷却/机器人刚发言互斥（不连说两句）→ 静默群不出声。
    /// </summary>
    public bool ShouldRandomChat(IncomingMessage message, DateTimeOffset now, bool sensitiveContent = false)
    {
        int probability = _config.Config.Trigger.RandomChatProbability;
        if (probability <= 0 || message.IsAtRobot || message.IsPrivate || sensitiveContent)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(message.Content) && message.ImageUrls.Count == 0
            && string.IsNullOrWhiteSpace(message.QuotedContent))
        {
            return false; // 空消息没有可接的话头；纯图片/纯引用消息可搭话（走多模态识图）
        }
        if (Random.Shared.Next(100) >= probability)
        {
            return false;
        }

        GroupState state = _states.GetOrCreate(message.GroupOpenId);
        var cfg = _config.Config.Trigger;

        // 管理员静默的群只保留 @ 响应，插嘴闭嘴
        if (state.Muted == true)
        {
            return false;
        }
        // 插嘴独立冷却：不管群多热闹，硬性限制插嘴频率
        if (state.LastRandomChatUtc + TimeSpan.FromSeconds(cfg.RandomChatCooldownSeconds) > now)
        {
            return false;
        }
        // 被动冷却互斥：刚 @ 回复过不插嘴；机器人刚发过言（指令回复/随机互动/暖群）也不接自己的话
        if (state.LastPassiveTriggerUtc + TimeSpan.FromSeconds(cfg.PassiveCooldownSeconds) > now
            || state.LastBotSpeakUtc + TimeSpan.FromSeconds(cfg.PassiveCooldownSeconds) > now)
        {
            return false;
        }
        return true;
    }

    /// <summary>标记该群已发生随机插嘴（进入插嘴冷却）</summary>
    public void MarkRandomChat(string groupOpenId, DateTimeOffset now)
    {
        GroupState state = _states.GetOrCreate(groupOpenId);
        state.LastRandomChatUtc = now;
    }
}
