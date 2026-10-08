using System.Text.Json;
using Microsoft.Extensions.Options;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;

namespace RainBot.Services.QQ;

/// <summary>
/// 协议层事件分发：解析群消息事件 → msg_id 去重 → 写入处理队列。
///
/// 触发语义：
/// - GROUP_AT_MESSAGE_CREATE（@ 事件）：官方保证仅 @ 机器人/回复机器人时推送 → 直接视为触发，
///   同时自动学习机器人自身 openid（供历史推送格式的全量消息精确判定用）。
/// - GROUP_MESSAGE_CREATE（全量消息）：全部入队统计/历史，是否 @ 机器人由 BotIdentityResolver
///   用机器人 openid 精确判定（openid 未知时不误判）。
///
/// 去重语义（关键）：开启「接收所有消息」后，@ 消息会同时推送两条事件且 msg_id 相同
/// （[群消息（全量模式）](https://bot.qq.com/wiki/develop/api-v2/autogen/event/group_message_create.html)）。
/// - 同类事件重复推送 → 忽略（官方注明相同 msg_id 可能重复推送）；
/// - @ 事件先到 → 全量事件直接跳过（避免同一消息重复统计/入库）；
/// - 全量事件先到 → @ 事件仍入队，但带 SkipSideEffects 标记：只补执行
///   风控 → 指令 → 被动触发 → 工作流，保证 @ 指令/回复绝不丢失。
/// </summary>
public class MessageDispatcher
{
    private readonly MessageQueue _queue;
    private readonly RuntimeConfig _config;
    private readonly BotIdentityResolver _botIdentity;
    private readonly BotInstanceStore _store;
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly ConcurrentDedupe _dedupeAt;   // @ 事件同类重复推送
    private readonly ConcurrentDedupe _dedupeFull; // 全量事件同类重复推送
    private readonly ConcurrentDedupe _dedupeC2c;  // C2C 私聊事件同类重复推送
    private readonly ConcurrentDedupe _atSeen;     // 已按 @ 处理过的 msg_id（全量事件随后到达时跳过）
    private readonly ConcurrentDedupe _fullSeen;   // 已按全量入队过的 msg_id（@ 事件随后到达时跳过副作用）

    public MessageDispatcher(MessageQueue queue, RuntimeConfig config, BotIdentityResolver botIdentity, BotInstanceStore store, ILogger<MessageDispatcher> logger)
    {
        _queue = queue;
        _config = config;
        _botIdentity = botIdentity;
        _store = store;
        _logger = logger;
        TimeSpan window = TimeSpan.FromSeconds(config.Config.Safety.DedupeWindowSeconds);
        _dedupeAt = new ConcurrentDedupe(window);
        _dedupeFull = new ConcurrentDedupe(window);
        _dedupeC2c = new ConcurrentDedupe(window);
        _atSeen = new ConcurrentDedupe(window);
        _fullSeen = new ConcurrentDedupe(window);
    }

    /// <summary>处理网关分发事件（READY/RESUMED/消息等）。botId 为该连接所属机器人实例。</summary>
    public async Task HandleDispatchAsync(string botId, string eventType, JsonElement data)
    {
        // 调试：打印每个网关事件（截断长数据，便于排查接收问题）
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string raw = data.GetRawText();
            _logger.LogDebug("[{BotId}] 网关事件：{EventType}，data={Data}", botId, eventType, raw.Length <= 800 ? raw : raw[..800] + "…");
        }
        switch (eventType)
        {
            case "GROUP_AT_MESSAGE_CREATE":
                await HandleGroupAtAsync(botId, data);
                break;
            case "GROUP_MESSAGE_CREATE":
                await HandleGroupFullAsync(botId, data);
                break;
            case "C2C_MESSAGE_CREATE":
                await HandleC2CAsync(botId, data);
                break;
            default:
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("未处理事件: {Event}", eventType);
                break;
        }
    }

    private async Task HandleGroupAtAsync(string botId, JsonElement data)
    {
        GroupAtMessage? group = JsonSerializer.Deserialize<GroupAtMessage>(data.GetRawText());
        if (group == null || string.IsNullOrEmpty(group.GroupOpenId))
        {
            return;
        }
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[@事件] id={Id} group={Group} sender={Sender} content={Content}",
                group.Id, group.GroupOpenId, ResolveSenderOpenId(group.Author), group.Content);
        }
        if (_dedupeAt.IsDuplicate(group.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("@ 事件重复推送已忽略（msg_id={MsgId}）", group.Id);
            return;
        }
        _dedupeAt.Mark(group.Id);
        _atSeen.Mark(group.Id); // 全量事件随后到达时直接跳过

        // 全量事件是否已先入队（同 msg_id）？是则跳过统计/历史等副作用，只补执行 @ 语义
        bool skipSideEffects = _fullSeen.IsDuplicate(group.Id);
        if (skipSideEffects && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("@ 事件晚于全量事件到达（msg_id={MsgId}），补执行指令/触发判定", group.Id);
        }

        // @ 事件中的 <@!xxx> 标签即机器人自身 openid，自动学习供全量消息判定使用
        await _botIdentity.ResolveFromAtContentAsync(group.Content);
        string? botOpenId = await _botIdentity.GetBotOpenIdAsync();
        if (!string.IsNullOrEmpty(botOpenId))
        {
            await _store.LearnSelfIdentityAsync(botId, botOpenId);
        }

        string senderOpenId = ResolveSenderOpenId(group.Author);
        QuoteParser.Result quote = QuoteParser.Parse(group.Scene, group.MsgElements);
        await _queue.EnqueueAsync(new IncomingMessage
        {
            BotId = botId,
            MsgId = group.Id,
            GroupOpenId = BotKeys.Group(botId, group.GroupOpenId),
            SenderOpenId = senderOpenId,
            Username = group.Author.Username,
            Content = group.Content,
            ImageUrls = MergeImages(ExtractImageUrls(group.Attachments), quote.QuotedImageUrls),
            QuotedContent = quote.QuotedText,
            MsgIdx = quote.MsgIdx,
            RefMsgIdx = quote.RefMsgIdx,
            IsAtRobot = true,
            IsAdmin = await _store.IsAdminAsync(botId, senderOpenId),
            IsFullMessage = false,
            SkipSideEffects = skipSideEffects
        });
    }

    /// <summary>
    /// 取消息附件中的图片 URL（官方 attachments 可能含图片/视频/文件，只取 image/*）。
    /// 这些 URL 仅在本轮请求内联给多模态模型使用，不落历史。
    /// </summary>
    private static List<string> ExtractImageUrls(Attachment[] attachments)
        => [.. attachments
            .Where(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.Url))
            .Select(a => a.Url)];

    /// <summary>合并本条附件图片与引用消息里的图片（去重，保持顺序）</summary>
    private static List<string> MergeImages(List<string> own, List<string> quoted)
        => own.Concat(quoted).Distinct(StringComparer.Ordinal).ToList();

    private async Task HandleGroupFullAsync(string botId, JsonElement data)
    {
        GroupMessage? group = JsonSerializer.Deserialize<GroupMessage>(data.GetRawText());
        if (group == null || string.IsNullOrEmpty(group.GroupOpenId))
        {
            return;
        }
        // 该消息已按 @ 事件处理过 → 跳过（避免重复统计/历史/回复）
        if (_atSeen.IsDuplicate(group.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("[全量事件] id={Id} 已被 @ 事件处理过，跳过", group.Id);
            return;
        }
        if (_dedupeFull.IsDuplicate(group.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("[全量事件] id={Id} 重复推送已忽略", group.Id);
            return;
        }
        _dedupeFull.Mark(group.Id);
        _fullSeen.Mark(group.Id);

        // 全量消息：mentions（is_you/openid 匹配，权威）→ content 标签兜底，精确判断是否 @ 机器人
        bool isAt = await _botIdentity.IsAtBotAsync(group.Content, group.Mentions);
        string senderOpenId = ResolveSenderOpenId(group.Author);
        QuoteParser.Result quote = QuoteParser.Parse(group.Scene, group.MsgElements);
        // 机器人自己的消息（全量模式回显，author.bot=true）：暖群判定"最后一条是自己"的依据
        bool isFromBot = group.Author.IsBot;
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[全量事件] id={Id} group={Group} sender={Sender} isAt={IsAt} isBot={IsBot} content={Content}",
                group.Id, group.GroupOpenId, senderOpenId, isAt, isFromBot, group.Content);
        }

        await _queue.EnqueueAsync(new IncomingMessage
        {
            BotId = botId,
            MsgId = group.Id,
            MsgSeq = group.MsgSeq,
            GroupOpenId = BotKeys.Group(botId, group.GroupOpenId),
            SenderOpenId = senderOpenId,
            Username = group.Author.Username,
            Content = group.Content,
            ImageUrls = MergeImages(ExtractImageUrls(group.Attachments), quote.QuotedImageUrls),
            QuotedContent = quote.QuotedText,
            MsgIdx = quote.MsgIdx,
            RefMsgIdx = quote.RefMsgIdx,
            IsAtRobot = isAt && !isFromBot,
            IsAdmin = await _store.IsAdminAsync(botId, senderOpenId),
            IsFullMessage = true,
            IsFromBot = isFromBot
        });
    }

    /// <summary>
    /// C2C 私聊消息（C2C_MESSAGE_CREATE，Intents 1&lt;&lt;25）：
    /// 单事件推送（无 @/全量双事件问题），会话键 = {实例Id}:p{用户OpenID}，与 OneBot 私聊共用隔离规则。
    /// 私聊里每条消息都是对机器人说的（IsAtRobot=true），直接进入被动触发链路。
    /// 官方 C2C 支持被动回复（msg_id 引用原消息），故 IsFullMessage=false；msg_seq 由发送队列递增。
    /// 去重键加 "p" 前缀防与群消息 msg_id 序列重叠（参考 OneBot 处理），
    /// IncomingMessage.MsgId 保留原始值供被动回复引用。
    /// </summary>
    private async Task HandleC2CAsync(string botId, JsonElement data)
    {
        C2CMessage? c2c = JsonSerializer.Deserialize<C2CMessage>(data.GetRawText());
        if (c2c == null || string.IsNullOrEmpty(c2c.Author.UserOpenId))
        {
            return;
        }
        if (_dedupeC2c.IsDuplicate("p" + c2c.Id))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("[C2C] 重复推送已忽略（msg_id={MsgId}）", c2c.Id);
            return;
        }
        _dedupeC2c.Mark("p" + c2c.Id);

        string senderOpenId = c2c.Author.UserOpenId;
        QuoteParser.Result quote = QuoteParser.Parse(c2c.Scene, c2c.MsgElements);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[C2C] id={Id} sender={Sender} content={Content}", c2c.Id, senderOpenId, c2c.Content);
        }

        await _queue.EnqueueAsync(new IncomingMessage
        {
            BotId = botId,
            MsgId = c2c.Id,
            GroupOpenId = BotKeys.Private(botId, senderOpenId),
            SenderOpenId = senderOpenId,
            Username = c2c.Author.Username,
            Content = c2c.Content,
            ImageUrls = MergeImages(ExtractImageUrls(c2c.Attachments), quote.QuotedImageUrls),
            QuotedContent = quote.QuotedText,
            MsgIdx = quote.MsgIdx,
            RefMsgIdx = quote.RefMsgIdx,
            IsAtRobot = true,
            IsAdmin = await _store.IsAdminAsync(botId, senderOpenId),
            IsFullMessage = false,
            IsPrivate = true
        });
    }

    /// <summary>
    /// 群消息事件的发送者 OpenID：官方文档中群事件 author 携带 member_openid
    /// （用户在群内的 openid，[群@机器人消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/group_at_message_create.html)、
    /// [群消息全量模式](https://bot.qq.com/wiki/develop/api-v2/autogen/event/group_message_create.html)），
    /// 部分环境仅返回 user_openid，故 member_openid 优先、user_openid 兜底。
    /// </summary>
    private static string ResolveSenderOpenId(Author author)
        => string.IsNullOrEmpty(author.MemberOpenId) ? author.UserOpenId : author.MemberOpenId;
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
