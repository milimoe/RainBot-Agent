using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RainBot.Models;
using RainBot.Services.Config;

namespace RainBot.Services.QQ;

/// <summary>
/// QQ 官方 API 发送服务（复用参考项目实现，修复沙箱 Host 统一管理）。
/// 凭据来自 BotConfigService：WebUI 修改 AppID/Secret 后即时生效（Token 缓存会被清空）。
/// </summary>
public class QQBotService(BotConfigService botConfigService, ILogger<QQBotService> logger, IHttpClientFactory httpClientFactory, IMemoryCache memoryCache)
{
    private BotConfig BotConfig => botConfigService.Current; // 每次访问取最新凭据
    private ILogger<QQBotService> Logger { get; } = logger;
    private HttpClient HttpClient { get; } = httpClientFactory.CreateClient();
    private IMemoryCache MemoryCache { get; } = memoryCache;

    private const string AccessTokenCacheKey = BotConfigService.AccessTokenCacheKey;

    /// <summary>
    /// 解析本实例使用的凭据：传入实例配置时以其为准，否则回落到全局配置（兼容旧单实例用法）。
    /// </summary>
    private BotConfig ResolveCredentials(QqOfficialConfig? instanceCredentials)
    {
        BotConfig fallback = BotConfig;
        if (instanceCredentials == null || string.IsNullOrWhiteSpace(instanceCredentials.AppId))
        {
            return fallback;
        }
        return new BotConfig
        {
            AppId = instanceCredentials.AppId,
            Secret = instanceCredentials.Secret,
            UseSandbox = instanceCredentials.UseSandbox
        };
    }

    /// <summary>Access Token 缓存键按 AppId 隔离（多实例共用一份缓存不串味）</summary>
    private static string TokenCacheKey(BotConfig credentials) => $"{AccessTokenCacheKey}:{credentials.AppId}";

    public async Task<string> GetAccessTokenAsync(bool useCache = true, QqOfficialConfig? credentials = null)
        => await GetAccessTokenAsync(ResolveCredentials(credentials), useCache);

    private async Task<string> GetAccessTokenAsync(BotConfig credentials, bool useCache)
    {
        string cacheKey = TokenCacheKey(credentials);
        if (useCache && MemoryCache.TryGetValue(cacheKey, out string? accessToken) && !string.IsNullOrEmpty(accessToken))
        {
            return accessToken;
        }
        return await RefreshTokenAsync(credentials);
    }

    public Task<string> RefreshTokenAsync(QqOfficialConfig? credentials = null) => RefreshTokenAsync(ResolveCredentials(credentials));

    private async Task<string> RefreshTokenAsync(BotConfig credentials)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "https://bots.qq.com/app/getAppAccessToken")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { appId = credentials.AppId, clientSecret = credentials.Secret }), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        AccessTokenResponse? tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(responseBody);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken) || !int.TryParse(tokenResponse.ExpiresIn, out int expiresIn))
        {
            throw new Exception("获取 Access Token 失败！");
        }
        MemoryCache.Set(TokenCacheKey(credentials), tokenResponse.AccessToken, TimeSpan.FromSeconds(expiresIn - 60));
        if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("获取到 Access Token（appId={AppId}）", credentials.AppId);
        return tokenResponse.AccessToken;
    }

    /// <summary>
    /// 清空某实例的 Access Token 缓存（WebUI 修改凭据后调用）。
    /// </summary>
    public void InvalidateTokenCache(QqOfficialConfig? credentials = null) => MemoryCache.Remove(TokenCacheKey(ResolveCredentials(credentials)));

    /// <summary>
    /// 发送群文本消息（RainBot 主力发送方式）。
    /// </summary>
    /// <param name="groupOpenId">群 OpenID</param>
    /// <param name="content">文本内容</param>
    /// <param name="msgId">被动回复时传原消息 ID 形成回复引用</param>
    /// <param name="msgSeq">发多条消息时递增，防止服务器去重</param>
    public Task<bool> SendGroupTextAsync(string groupOpenId, string content, string? msgId = null, long? msgSeq = null, QqOfficialConfig? credentials = null)
    {
        Dictionary<string, object> requestBody = new()
        {
            { "content", "\r\n" + content.Trim() },
            { "msg_type", 0 }
        };
        if (!string.IsNullOrEmpty(msgId)) requestBody.Add("msg_id", msgId);
        if (msgSeq.HasValue) requestBody.Add("msg_seq", msgSeq.Value);
        return PostJsonAsync($"{ResolveCredentials(credentials).ApiHost}/v2/groups/{groupOpenId}/messages", requestBody, credentials);
    }

    /// <summary>
    /// 发送群 Markdown 消息（msg_type=2，开启 Rain.MarkdownReply 时使用）。
    /// </summary>
    /// <param name="groupOpenId">群 OpenID</param>
    /// <param name="markdownContent">Markdown 内容（QQ 支持标题/加粗/列表/块引用等子集语法）</param>
    /// <param name="msgId">被动回复时传原消息 ID 形成回复引用</param>
    /// <param name="msgSeq">发多条消息时递增，防止服务器去重</param>
    public Task<bool> SendGroupMarkdownAsync(string groupOpenId, string markdownContent, string? msgId = null, long? msgSeq = null, QqOfficialConfig? credentials = null)
    {
        Dictionary<string, object> requestBody = new()
        {
            { "msg_type", 2 },
            { "markdown", new Dictionary<string, object> { ["content"] = markdownContent.Trim() } }
        };
        if (!string.IsNullOrEmpty(msgId)) requestBody.Add("msg_id", msgId);
        if (msgSeq.HasValue) requestBody.Add("msg_seq", msgSeq.Value);
        return PostJsonAsync($"{ResolveCredentials(credentials).ApiHost}/v2/groups/{groupOpenId}/messages", requestBody, credentials);
    }

    /// <summary>
    /// 发送 C2C 私聊文本消息（POST /v2/users/{openid}/messages）。
    /// </summary>
    /// <param name="userOpenId">用户 OpenID（C2C 单聊对端）</param>
    /// <param name="content">文本内容</param>
    /// <param name="msgId">被动回复时传原消息 ID 形成回复引用</param>
    /// <param name="msgSeq">发多条消息时递增，防止服务器去重</param>
    public Task<bool> SendC2CTextAsync(string userOpenId, string content, string? msgId = null, long? msgSeq = null, QqOfficialConfig? credentials = null)
    {
        Dictionary<string, object> requestBody = new()
        {
            { "content", "\r\n" + content.Trim() },
            { "msg_type", 0 }
        };
        if (!string.IsNullOrEmpty(msgId)) requestBody.Add("msg_id", msgId);
        if (msgSeq.HasValue) requestBody.Add("msg_seq", msgSeq.Value);
        return PostJsonAsync($"{ResolveCredentials(credentials).ApiHost}/v2/users/{userOpenId}/messages", requestBody, credentials);
    }

    /// <summary>
    /// 发送 C2C 私聊 Markdown 消息（msg_type=2，开启 Rain.MarkdownReply 时使用）。
    /// </summary>
    public Task<bool> SendC2CMarkdownAsync(string userOpenId, string markdownContent, string? msgId = null, long? msgSeq = null, QqOfficialConfig? credentials = null)
    {
        Dictionary<string, object> requestBody = new()
        {
            { "msg_type", 2 },
            { "markdown", new Dictionary<string, object> { ["content"] = markdownContent.Trim() } }
        };
        if (!string.IsNullOrEmpty(msgId)) requestBody.Add("msg_id", msgId);
        if (msgSeq.HasValue) requestBody.Add("msg_seq", msgSeq.Value);
        return PostJsonAsync($"{ResolveCredentials(credentials).ApiHost}/v2/users/{userOpenId}/messages", requestBody, credentials);
    }

    /// <summary>上传群媒体（图片等，保留参考项目能力）</summary>
    public async Task<UploadMediaResult> UploadGroupMediaAsync(string groupOpenId, int fileType, string url, QqOfficialConfig? credentials = null)
    {
        BotConfig config = ResolveCredentials(credentials);
        Dictionary<string, object> requestBody = new()
        {
            { "file_type", fileType },
            { "url", url },
            { "srv_send_msg", false }
        };
        string json;
        using (StringContent content = new(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"))
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"{config.ApiHost}/v2/groups/{groupOpenId}/files")
            {
                Content = content
            };
            await AttachAuthAsync(request, credentials);
            HttpResponseMessage response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                string error = $"状态码：{response.StatusCode}，错误信息：{errorBody}，多媒体URL地址：{url}";
                if (Logger.IsEnabled(LogLevel.Error)) Logger.LogError("{Error}", error);
                return new UploadMediaResult { Error = error };
            }
            json = await response.Content.ReadAsStringAsync();
        }
        MediaResponse? mediaResponse = JsonSerializer.Deserialize<MediaResponse>(json);
        if (mediaResponse == null)
        {
            return new UploadMediaResult { Error = "反序列化富媒体消息失败。" };
        }
        return new UploadMediaResult
        {
            FileUuid = mediaResponse.FileUuid,
            FileInfo = mediaResponse.FileInfo,
            Ttl = mediaResponse.Ttl
        };
    }

    /// <summary>发送群图片（msg_type=7，需先上传媒体拿到 file_info）</summary>
    public Task<bool> SendGroupImageAsync(string groupOpenId, string fileInfo, string? msgId = null, QqOfficialConfig? credentials = null)
    {
        Dictionary<string, object> body = new()
        {
            { "msg_type", 7 },
            { "media", new Dictionary<string, object> { ["file_info"] = fileInfo } }
        };
        if (!string.IsNullOrEmpty(msgId)) body.Add("msg_id", msgId);
        return PostJsonAsync($"{ResolveCredentials(credentials).ApiHost}/v2/groups/{groupOpenId}/messages", body, credentials);
    }

    private async Task<bool> PostJsonAsync(string url, Dictionary<string, object> body, QqOfficialConfig? credentials = null)
    {
        string accessToken = await GetAccessTokenAsync(true, credentials);
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);
        HttpResponseMessage response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            if (Logger.IsEnabled(LogLevel.Error)) Logger.LogError("发送失败，状态码：{StatusCode}，错误信息：{ErrorBody}", response.StatusCode, errorBody);
            return false;
        }
        return true;
    }

    private async Task AttachAuthAsync(HttpRequestMessage request, QqOfficialConfig? credentials = null)
    {
        string accessToken = await GetAccessTokenAsync(true, credentials);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", accessToken);
    }
}
