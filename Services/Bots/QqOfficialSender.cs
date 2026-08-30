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

        // C2C 私聊：官方不支持发送图片，OSM 梗图等自动降级纯文本
        if (request.IsPrivate)
        {
            if (!string.IsNullOrWhiteSpace(request.ImageUrl))
            {
                _logger.LogInformation("实例 {BotId} 私聊不支持图片，已降级为纯文本发送", request.BotId);
            }
            string c2cContent = BuildContent(request);
            return request.Markdown
                ? await _qqBotService.SendC2CMarkdownAsync(request.RawGroupId, c2cContent, request.ReplyMsgId, request.MsgSeq, credentials)
                : await _qqBotService.SendC2CTextAsync(request.RawGroupId, c2cContent, request.ReplyMsgId, request.MsgSeq, credentials);
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
