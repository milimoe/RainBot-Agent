using RainBot.Models;
using RainBot.Services.OneBot;

namespace RainBot.Services.Bots;

/// <summary>一次发送请求（平台无关）</summary>
public class BotSendRequest
{
    /// <summary>目标机器人实例 Id</summary>
    public required string BotId { get; init; }

    /// <summary>原始群号/群 openid（不含实例前缀）</summary>
    public required string RawGroupId { get; init; }

    /// <summary>文本内容</summary>
    public required string Content { get; init; }

    /// <summary>是否以 Markdown 发送（OneBot11 无原生支持，自动降级纯文本）</summary>
    public bool Markdown { get; init; }

    /// <summary>被动回复时引用原消息 ID</summary>
    public string? ReplyMsgId { get; init; }

    /// <summary>需要 @ 的用户原始 ID（由各平台渲染为对应语义）</summary>
    public string? AtUserId { get; init; }

    /// <summary>发送序号（防止服务端按内容去重）</summary>
    public long MsgSeq { get; init; }

    /// <summary>图片地址（OSM 梗图等；留空表示纯文本）</summary>
    public string? ImageUrl { get; init; }

    /// <summary>是否私聊会话（OneBot 走 send_private_msg；@ 语义在私聊中忽略）</summary>
    public bool IsPrivate { get; init; }
}

/// <summary>
/// 平台发送器：一个平台一个实现，负责把平台无关的请求渲染成该平台的消息格式。
/// 返回是否发送成功（供发送统计使用）。
/// </summary>
public interface IBotSender
{
    BotPlatform Platform { get; }

    Task<bool> SendAsync(BotSendRequest request);
}

/// <summary>
/// 发送路由器：按实例所属平台显式分发（switch-case，不用反射）。
/// 实例不存在或已禁用时直接丢弃并记录，不抛异常打断发送队列。
/// </summary>
public class BotSenderRouter(
    BotInstanceStore store,
    QqOfficialSender qqSender,
    OneBotSender oneBotSender,
    ILogger<BotSenderRouter> logger)
{
    private readonly BotInstanceStore _store = store;
    private readonly QqOfficialSender _qqSender = qqSender;
    private readonly OneBotSender _oneBotSender = oneBotSender;
    private readonly ILogger<BotSenderRouter> _logger = logger;

    public async Task<bool> SendAsync(BotSendRequest request)
    {
        BotInstance? instance = _store.Get(request.BotId);
        if (instance == null)
        {
            _logger.LogWarning("发送失败：机器人实例 {BotId} 不存在", request.BotId);
            return false;
        }
        if (!instance.Enabled)
        {
            _logger.LogInformation("发送跳过：机器人实例 {BotId} 已禁用", request.BotId);
            return false;
        }

        switch (instance.Platform)
        {
            case BotPlatform.QqOfficial:
                return await _qqSender.SendAsync(request);
            case BotPlatform.OneBot11:
                return await _oneBotSender.SendAsync(request);
            default:
                _logger.LogWarning("发送失败：实例 {BotId} 平台 {Platform} 无对应发送器", request.BotId, instance.Platform);
                return false;
        }
    }
}
