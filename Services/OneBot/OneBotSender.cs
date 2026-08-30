using RainBot.Models;
using RainBot.Services.Bots;

namespace RainBot.Services.OneBot;

/// <summary>
/// OneBot11 平台发送器：把平台无关请求渲染为 OneBot 消息段并调用 send_group_msg。
/// 与官方平台的差异：
/// - @ 用 at 消息段（官方是文本标签 &lt;@!openid&gt;）；
/// - 引用回复用 reply 消息段（官方是 msg_id 字段）；
/// - 无原生 Markdown 消息类型 → Markdown 自动降级为纯文本。
/// </summary>
public class OneBotSender(OneBotManager manager, ILogger<OneBotSender> logger) : IBotSender
{
    private readonly OneBotManager _manager = manager;
    private readonly ILogger<OneBotSender> _logger = logger;

    public BotPlatform Platform => BotPlatform.OneBot11;

    public async Task<SendResult> SendAsync(BotSendRequest request)
    {
        List<object> message = BuildMessage(request);

        string action;
        object parameters;
        if (request.IsPrivate)
        {
            // 私聊：send_private_msg，对端是用户号（调用方已去掉 p 标记）
            action = "send_private_msg";
            parameters = new
            {
                user_id = IdAsNumber(request.RawGroupId),
                message,
                auto_escape = false
            };
        }
        else
        {
            action = "send_group_msg";
            parameters = new
            {
                group_id = IdAsNumber(request.RawGroupId),
                message,
                auto_escape = false
            };
        }

        OneBotApiResponse? response = await _manager.SendApiAsync(request.BotId, action, parameters);
        if (response is null)
        {
            _logger.LogWarning("OneBot 发送失败（{BotId}）：无可用通道或无响应（{Action}）", request.BotId, action);
            // 无通道/无响应多为临时状态（断线恢复、实现重启），值得退避重试
            return SendResult.Fail("无可用通道或无响应", retryable: true);
        }
        if (!response.IsSuccess)
        {
            _logger.LogWarning("OneBot 发送失败（{BotId}）：retcode={Retcode} {Wording}", request.BotId, response.Retcode, response.Wording);
            // OneBot 无标准频控码，失败默认可重试（次数有限，不会造成风暴）
            return SendResult.Fail($"retcode={response.Retcode} {response.Wording}", retryable: true);
        }
        return SendResult.Ok();
    }

    private static List<object> BuildMessage(BotSendRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            // OSM 梗图等：OneBot 用 image 段，file 填可访问的 URL
            return [OneBotMessage.Image(request.ImageUrl)];
        }
        // 私聊里 @ 无意义，只保留引用回复（若有）
        return OneBotMessage.Build(request.Content, request.IsPrivate ? null : request.AtUserId, request.ReplyMsgId);
    }

    /// <summary>group_id / user_id 多数实现要求是数字；解析失败则原样传字符串</summary>
    private static object IdAsNumber(string rawId)
        => long.TryParse(rawId, out long id) ? id : rawId;
}
