using RainBot.Models;
using RainBot.Services.QQ;

namespace RainBot.Services.Bots;

/// <summary>
/// QQ 官方平台发送器：把平台无关请求渲染为官方 API 调用。
/// 多实例下凭据按实例取（每个实例有自己的 AppId/Secret）。
/// </summary>
public class QqOfficialSender(QQBotService qqBotService, BotInstanceStore store, ILogger<QqOfficialSender> logger) : IBotSender
{
    private readonly QQBotService _qqBotService = qqBotService;
    private readonly BotInstanceStore _store = store;
    private readonly ILogger<QqOfficialSender> _logger = logger;

    public BotPlatform Platform => BotPlatform.QqOfficial;

    public async Task<bool> SendAsync(BotSendRequest request)
    {
        QqOfficialConfig? credentials = _store.Get(request.BotId)?.Qq;

        // QQ 官方侧的私聊（C2C）入站尚未接入，这里只做防御：不打无把握的发送
        if (request.IsPrivate)
        {
            _logger.LogWarning("实例 {BotId} 请求私聊发送，但 QQ 官方通道暂不支持私聊（C2C 入站未接入）", request.BotId);
            return false;
        }

        string content = BuildContent(request);

        if (!string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            return await SendImageAsync(request, credentials);
        }

        return request.Markdown
            ? await _qqBotService.SendGroupMarkdownAsync(request.RawGroupId, content, request.ReplyMsgId, request.MsgSeq, credentials)
            : await _qqBotService.SendGroupTextAsync(request.RawGroupId, content, request.ReplyMsgId, request.MsgSeq, credentials);
    }

    /// <summary>@ 语义在官方平台是文本标签 &lt;@!openid&gt;</summary>
    private static string BuildContent(BotSendRequest request)
        => string.IsNullOrWhiteSpace(request.AtUserId) ? request.Content : $"<@!{request.AtUserId}> {request.Content}";

    private async Task<bool> SendImageAsync(BotSendRequest request, QqOfficialConfig? credentials)
    {
        if (string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            return false;
        }
        UploadMediaResult upload = await _qqBotService.UploadGroupMediaAsync(request.RawGroupId, 1, request.ImageUrl, credentials);
        if (string.IsNullOrEmpty(upload.Error) && !string.IsNullOrEmpty(upload.FileInfo))
        {
            return await _qqBotService.SendGroupImageAsync(request.RawGroupId, upload.FileInfo, request.ReplyMsgId, credentials);
        }
        _logger.LogWarning("QQ 官方图片上传失败，降级为文本：{Error}", upload.Error);
        return await _qqBotService.SendGroupTextAsync(request.RawGroupId, BuildContent(request), request.ReplyMsgId, request.MsgSeq, credentials);
    }
}
