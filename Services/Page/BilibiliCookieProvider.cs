using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Page;

/// <summary>
/// B 站请求 Cookie：手动配置优先，其次扫码凭据；缺失的设备 Cookie 从 spi 补齐。
/// 匿名设备身份缓存 24 小时；有账号或手动身份时不因 412 自动轮换。
/// </summary>
public sealed class BilibiliCookieProvider(IHttpClientFactory clients, RuntimeConfig config, ILogger<BilibiliCookieProvider> logger,
    BilibiliLoginService? login = null)
{
    private static readonly Uri SpiUrl = new("https://api.bilibili.com/x/frontend/finger/spi");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cookie;
    private DateTimeOffset _expires;
    private long _riskUntil;
    private string _riskIdentity = "";
    public string CacheIdentity => (login?.Version ?? 0) + ":" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(config.Config.Page.BilibiliCookies)));
    public bool IsCoolingDown => _riskIdentity == CacheIdentity && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < Interlocked.Read(ref _riskUntil);
    public void MarkRisk()
    {
        _riskIdentity = CacheIdentity;
        Interlocked.Exchange(ref _riskUntil, DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeMilliseconds());
    }

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        string manual = config.Config.Page.BilibiliCookies.Trim();
        string account = manual.Length > 0 ? manual : login != null ? await login.GetCookieAsync(cancellationToken) : "";
        var supplied = Parse(account);
        if (supplied.ContainsKey("buvid3") && supplied.ContainsKey("buvid4")) return Merge(account, "");
        // 已有设备身份不主动轮换；缺失的设备键从匿名会话补齐。
        if (_cookie is { } cached && DateTimeOffset.UtcNow < _expires) return Merge(account, cached);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cookie is { } fresh && DateTimeOffset.UtcNow < _expires) return Merge(account, fresh);
            string cookie = await FetchAsync(cancellationToken);
            if (cookie.Length > 0)
            {
                _cookie = cookie;
                _expires = DateTimeOffset.UtcNow.AddHours(24);
                logger.LogInformation("已从 B 站 spi 接口领取 buvid Cookie。");
            }
            return Merge(account, cookie);
        }
        finally { _lock.Release(); }
    }

    /// <summary>丢弃缓存（遇到 412 时调用），下一次 GetAsync 重新领取。</summary>
    public void Invalidate()
    {
        _cookie = null;
        _expires = DateTimeOffset.MinValue;
    }

    internal static Dictionary<string, string> Parse(string cookie)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string part in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0) continue;
            string key = part[..separator].Trim(), value = part[(separator + 1)..].Trim();
            if (key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && value.Length > 0
                && value.All(c => c >= 0x21 && c <= 0x7e && c != ';')) values[key] = value;
        }
        return values;
    }
    private static string Merge(string supplied, string anonymous)
    {
        var values = Parse(supplied);
        foreach (var pair in Parse(anonymous)) values.TryAdd(pair.Key, pair.Value);
        return string.Join("; ", values.Select(p => $"{p.Key}={p.Value}"));
    }
    internal static string DeviceOnly(string cookie) => string.Join("; ", Parse(cookie)
        .Where(p => p.Key is "buvid3" or "buvid4" or "buvid_fp").Select(p => $"{p.Key}={p.Value}"));
    public async Task<bool> CanRenewAnonymousAsync(CancellationToken ct)
    {
        string account = config.Config.Page.BilibiliCookies.Trim();
        if (account.Length == 0 && login != null) account = await login.GetCookieAsync(ct);
        return account.Length == 0;
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
