using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Page;

/// <summary>
/// B 站请求 Cookie：api.bilibili.com 对无 buvid 设备 Cookie 的请求会被风控直接返回 412。
/// 优先使用 Rain.Page.BilibiliCookies 手动配置（可附 SESSDATA）；留空时调用 spi 接口
/// （免登录）领取 buvid3/buvid4 并在进程内缓存 24 小时，遇到 412 可 Invalidate 后重新领取。
/// </summary>
public sealed class BilibiliCookieProvider(IHttpClientFactory clients, RuntimeConfig config, ILogger<BilibiliCookieProvider> logger)
{
    private static readonly Uri SpiUrl = new("https://api.bilibili.com/x/frontend/finger/spi");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cookie;
    private DateTimeOffset _expires;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        string manual = config.Config.Page.BilibiliCookies.Trim();
        if (manual.Length > 0) return manual;
        if (_cookie is { } cached && DateTimeOffset.UtcNow < _expires) return cached;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cookie is { } fresh && DateTimeOffset.UtcNow < _expires) return fresh;
            string cookie = await FetchAsync(cancellationToken);
            if (cookie.Length > 0)
            {
                _cookie = cookie;
                _expires = DateTimeOffset.UtcNow.AddHours(24);
                logger.LogInformation("已从 B 站 spi 接口领取 buvid Cookie。");
            }
            return cookie;
        }
        finally { _lock.Release(); }
    }

    /// <summary>丢弃缓存（遇到 412 时调用），下一次 GetAsync 重新领取。</summary>
    public void Invalidate()
    {
        _cookie = null;
        _expires = DateTimeOffset.MinValue;
    }

    private async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = clients.CreateClient("page_reader");
            using HttpRequestMessage request = new(HttpMethod.Get, SpiUrl);
            request.Headers.UserAgent.ParseAdd(PageReaderHttp.BrowserUserAgent);
            request.Headers.Referrer = new Uri("https://www.bilibili.com/");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return "";
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement data = document.RootElement.GetProperty("data");
            string buvid3 = Text(data, "b_3");
            if (buvid3.Length == 0) return "";
            string buvid4 = Text(data, "b_4");
            return buvid4.Length > 0 ? $"buvid3={buvid3}; buvid4={buvid4}" : $"buvid3={buvid3}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("B 站 buvid Cookie 领取失败：{ErrorType}", exception.GetType().Name);
            return "";
        }
    }

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.String
        ? item.GetString()?.Trim() ?? "" : "";
}
