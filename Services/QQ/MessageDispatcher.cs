using System.Text.Json;
using Microsoft.Extensions.Options;
using RainBot.Models;
using RainBot.Services.Config;

namespace RainBot.Services.QQ;

/// <summary>
/// 协议层事件分发：解析群消息事件 → msg_id 去重 → 写入处理队列。
/// 
/// 触发语义：
/// - GROUP_AT_MESSAGE_CREATE（@ 事件）：官方保证仅 @ 机器人/回复机器人时推送 → 直接视为触发，
///   同时自动学习机器人自身 openid（供全量消息精确判定用）。
/// - GROUP_MESSAGE_CREATE（全量消息）：全部入队统计/历史，是否 @ 机器人由 BotIdentityResolver
///   用机器人 openid 精确判定（openid 未知时不误判）。
/// </summary>
public class MessageDispatcher
{
    private readonly MessageQueue _queue;
    private readonly RuntimeConfig _config;
    private readonly BotIdentityResolver _botIdentity;
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly ConcurrentDedupe _dedupe;

    public MessageDispatcher(MessageQueue queue, RuntimeConfig config, BotIdentityResolver botIdentity, ILogger<MessageDispatcher> logger)
    {
        _queue = queue;
        _config = config;
        _botIdentity = botIdentity;
        _logger = logger;
        _dedupe = new ConcurrentDedupe(TimeSpan.FromSeconds(config.Config.Safety.DedupeWindowSeconds));
    }

    /// <summary>处理网关分发事件（READY/RESUMED/消息等）</summary>
    public async Task HandleDispatchAsync(string eventType, JsonElement data)
    {
        switch (eventType)
        {
            case "GROUP_AT_MESSAGE_CREATE":
                await HandleGroupAtAsync(data);
                break;
            case "GROUP_MESSAGE_CREATE":
                await HandleGroupFullAsync(data);
                break;
            case "C2C_MESSAGE_CREATE":
                // MVP 聚焦群聊，私聊仅记录
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("收到 C2C 消息（暂不处理）");
                break;
            default:
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("未处理事件: {Event}", eventType);
                break;
        }
    }

    private async Task HandleGroupAtAsync(JsonElement data)
    {
        GroupAtMessage? group = JsonSerializer.Deserialize<GroupAtMessage>(data.GetRawText());
        if (group == null || string.IsNullOrEmpty(group.GroupOpenId))
        {
            return;
        }
        if (_dedupe.IsDuplicate(group.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("重复消息已忽略（msg_id={MsgId}）", group.Id);
            return;
        }
        _dedupe.Mark(group.Id);

        // @ 事件中的 <@!xxx> 标签即机器人自身 openid，自动学习供全量消息判定使用
        await _botIdentity.ResolveFromAtContentAsync(group.Content);

        await _queue.EnqueueAsync(new IncomingMessage
        {
            MsgId = group.Id,
            GroupOpenId = group.GroupOpenId,
            SenderOpenId = group.Author.UserOpenId,
            Content = group.Content,
            IsAtRobot = true,
            IsAdmin = await _config.IsAdminAsync(group.Author.UserOpenId),
            IsFullMessage = false
        });
    }

    private async Task HandleGroupFullAsync(JsonElement data)
    {
        GroupMessage? group = JsonSerializer.Deserialize<GroupMessage>(data.GetRawText());
        if (group == null || string.IsNullOrEmpty(group.GroupOpenId))
        {
            return;
        }
        if (_dedupe.IsDuplicate(group.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("重复消息已忽略（msg_id={MsgId}）", group.Id);
            return;
        }
        _dedupe.Mark(group.Id);

        // 全量消息：精确判断是否 @ 机器人（依赖机器人 openid；未知时不误判）
        bool isAt = await _botIdentity.IsAtBotAsync(group.Content);

        await _queue.EnqueueAsync(new IncomingMessage
        {
            MsgId = group.Id,
            MsgSeq = group.MsgSeq,
            GroupOpenId = group.GroupOpenId,
            SenderOpenId = group.Author.UserOpenId,
            Content = group.Content,
            IsAtRobot = isAt,
            IsAdmin = await _config.IsAdminAsync(group.Author.UserOpenId),
            IsFullMessage = true
        });
    }
}

/// <summary>
/// 消息 ID 去重（滑动时间窗口，定期清理过期条目）
/// </summary>
public class ConcurrentDedupe
{
    private readonly Dictionary<string, DateTimeOffset> _seen = [];
    private readonly Lock _lock = new();
    private readonly TimeSpan _window;
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;

    public ConcurrentDedupe(TimeSpan window)
    {
        _window = window;
    }

    public bool IsDuplicate(string key)
    {
        lock (_lock)
        {
            return _seen.TryGetValue(key, out DateTimeOffset ts) && DateTimeOffset.UtcNow - ts < _window;
        }
    }

    public void Mark(string key)
    {
        lock (_lock)
        {
            _seen[key] = DateTimeOffset.UtcNow;
            // 定期清理过期条目，避免无限增长
            if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromMinutes(10))
            {
                _lastCleanup = DateTimeOffset.UtcNow;
                foreach (string k in _seen.Where(kv => DateTimeOffset.UtcNow - kv.Value > _window).Select(kv => kv.Key).ToList())
                {
                    _seen.Remove(k);
                }
            }
        }
    }
}
