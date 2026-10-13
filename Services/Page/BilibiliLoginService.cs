using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Page;

/// <summary>Web 扫码会话与加密保存的账号凭据。返回 WebUI 的对象不含 Cookie 或刷新令牌。</summary>
public sealed class BilibiliLoginService(IHttpClientFactory clients, Database db, IDataProtectionProvider protection,
    RuntimeConfig config, ILogger<BilibiliLoginService> logger)
{
    internal const string StorageKey = "Bilibili.Login.Protected";
    private readonly IDataProtector _protector = protection.CreateProtector("RainBot.Bilibili.Login.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Credential? _credential;
    private bool _loaded;
    private Session? _session;
    private long _version;
    public long Version => Interlocked.Read(ref _version);

    private sealed record Credential(string Cookie, string RefreshToken, DateTimeOffset SavedAt, bool Invalid = false);
    private sealed class Session(string id, string key, string url, DateTimeOffset expires)
    {
        public string Id { get; } = id;
        public string Key { get; } = key;
        public string Url { get; } = url;
        public DateTimeOffset Expires { get; } = expires;
        public DateTimeOffset NextPoll { get; set; }
        public string State { get; set; } = "pending";
    }
    public sealed record QrSession(string Id, string Url, DateTimeOffset ExpiresAt, string State);
    public sealed record LoginStatus(string Source, string State, string Message, DateTimeOffset? SavedAt = null);

    public async Task<string> GetCookieAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await LoadAsync(); return _credential is { Invalid: false } c ? c.Cookie : ""; }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        string? stored = await db.GetSettingAsync(StorageKey);
        if (stored != null)
        {
            try { _credential = JsonSerializer.Deserialize<Credential>(_protector.Unprotect(stored)); }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            { logger.LogWarning("B 站凭据无法解密，需要重新扫码登录。"); }
        }
        _loaded = true;
    }

    public async Task<QrSession> StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var response = await SendAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", "", ct);
            using var json = await ParseAsync(response, ct);
            JsonElement data = Data(json.RootElement);
            string key = Text(data, "qrcode_key"), url = Text(data, "url");
            if (key.Length != 32 || !key.All(char.IsAsciiHexDigit)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !IsQrUrl(uri))
                throw new BilibiliLoginException("qr_format_invalid", "B 站二维码响应格式异常（登录域名或密钥不符合预期）。");
            _session = new(Guid.NewGuid().ToString("N"), key, url, DateTimeOffset.UtcNow.AddSeconds(180));
            return PublicSession(_session);
        }
        finally { _gate.Release(); }
    }

    public async Task<QrSession> PollAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Session session = _session is { } s && s.Id == id ? s : throw new InvalidOperationException("登录会话不存在，请重新扫码。");
            if (session.State is "confirmed" or "expired" or "failed") return PublicSession(session);
            if (DateTimeOffset.UtcNow >= session.Expires) { session.State = "expired"; return PublicSession(session); }
            if (DateTimeOffset.UtcNow < session.NextPoll) return PublicSession(session);
            session.NextPoll = DateTimeOffset.UtcNow.AddSeconds(2);
            using var response = await SendAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + session.Key, "", ct);
            using var json = await ParseAsync(response, ct);
            JsonElement data = Data(json.RootElement);
            int code = data.GetProperty("code").GetInt32();
            session.State = code switch { 86101 => "pending", 86090 => "scanned", 86038 => "expired", 0 => "verifying", _ => "failed" };
            if (code == 0)
            {
                string cookie = ExtractCookies(response);
                var parsed = BilibiliCookieProvider.Parse(cookie);
                if (!parsed.ContainsKey("SESSDATA") || !parsed.ContainsKey("bili_jct") || !parsed.ContainsKey("DedeUserID"))
                { session.State = "failed"; throw new InvalidOperationException("扫码确认成功，但响应未包含完整登录凭据，请重新登录。"); }
                LoginStatus checkedStatus = await CheckAsync(cookie, "login", null, ct);
                if (checkedStatus.State != "valid")
                { session.State = "failed"; throw new InvalidOperationException(checkedStatus.Message); }
                Credential credential = new(cookie, Text(data, "refresh_token"), DateTimeOffset.UtcNow);
                await db.UpsertSettingAsync(StorageKey, _protector.Protect(JsonSerializer.Serialize(credential)));
                _credential = credential;
                _loaded = true;
                Interlocked.Increment(ref _version);
                session.State = "confirmed";
                logger.LogInformation("B 站扫码登录凭据已验证并加密保存。");
            }
            return PublicSession(session);
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (_session?.Id == id) _session = null; }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await db.DeleteSettingAsync(StorageKey);
            _credential = null; _loaded = true; _session = null;
            Interlocked.Increment(ref _version);
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginStatus> StatusAsync(bool verify, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadAsync();
            string manual = config.Config.Page.BilibiliCookies.Trim();
            string source = manual.Length > 0 ? "manual" : _credential != null ? "login" : "anonymous";
            if (manual.Length == 0 && _credential is { Invalid: true }) return new(source, "expired", "登录凭据已失效，请重新扫码。", _credential.SavedAt);
            string cookie = manual.Length > 0 ? manual : _credential?.Cookie ?? "";
            if (!BilibiliCookieProvider.Parse(cookie).ContainsKey("SESSDATA"))
                return new(source, "anonymous", "未配置登录凭据，使用匿名设备 Cookie。");
            if (!verify) return new(source, "saved", "已保存凭据，可检查登录状态。", _credential?.SavedAt);
            LoginStatus status = await CheckAsync(cookie, source, _credential?.SavedAt, ct);
            if (source == "login" && status.State == "expired" && _credential != null)
            {
                Credential invalid = _credential with { Invalid = true };
                await db.UpsertSettingAsync(StorageKey, _protector.Protect(JsonSerializer.Serialize(invalid)));
                _credential = invalid;
                Interlocked.Increment(ref _version);
            }
            return status;
        }
        finally { _gate.Release(); }
    }

    private async Task<LoginStatus> CheckAsync(string cookie, string source, DateTimeOffset? savedAt, CancellationToken ct)
    {
        using var response = await SendAsync("https://api.bilibili.com/x/web-interface/nav", cookie, ct);
        if ((int)response.StatusCode == 412) return new(source, "risk", "登录状态检查被风控拦截，暂时无法确认凭据有效性。", savedAt);
        using var json = await ParseAsync(response, ct);
        JsonElement root = json.RootElement;
        int code = root.GetProperty("code").GetInt32();
        if (code == -101) return new(source, "expired", "登录凭据已失效，请重新扫码。", savedAt);
        if (code is -352 or -412) return new(source, "risk", "登录状态检查被风控拦截。", savedAt);
        if (code == 0 && root.TryGetProperty("data", out var data) && data.TryGetProperty("isLogin", out var login))
            return login.ValueKind == JsonValueKind.True
                ? new(source, "valid", "登录凭据有效。", savedAt)
                : new(source, "expired", "账号未登录，请重新扫码。", savedAt);
        return new(source, "unknown", "B 站未返回可确认的登录状态。", savedAt);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string cookie, CancellationToken ct)
    {
        using var client = clients.CreateClient("bilibili_login");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(PageReaderHttp.BrowserUserAgent);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        try { return await client.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            // 不记录异常正文、完整 URL 或 Cookie；轮询 URL 含临时扫码密钥。
            logger.LogWarning("B 站登录网络请求失败：{Stage} / {Error} / {ErrorType}", request.RequestUri!.AbsolutePath,
                ex.HttpRequestError, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            throw;
        }
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new BilibiliLoginException("upstream_http_error", $"B 站登录接口返回 HTTP {(int)response.StatusCode}。", (int)response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new BilibiliLoginException("upstream_not_json", "B 站登录接口未返回 JSON，可能是风控页面或网络拦截。");
        string content = await response.Content.ReadAsStringAsync(ct);
        if (content.Length > 262144) throw new BilibiliLoginException("upstream_response_too_large", "B 站登录响应超过大小限制。");
        try { return JsonDocument.Parse(content); }
        catch (JsonException) { throw new BilibiliLoginException("upstream_invalid_json", "B 站登录接口返回的 JSON 格式无效。"); }
    }

    private static JsonElement Data(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int status))
            throw new BilibiliLoginException("upstream_format_invalid", "B 站登录接口响应缺少有效状态码。");
        if (status != 0) throw new BilibiliLoginException("upstream_api_error", $"B 站登录接口返回业务码 {status}。", status);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new BilibiliLoginException("upstream_format_invalid", "B 站登录接口响应缺少数据。");
        return data;
    }

    // 二维码目标与请求接口域名不同：当前接口仍在 passport，二维码已使用 account。
    // 只接受两个明确的官方 HTTPS 主机，不放宽为任意子域或后缀匹配。
    internal static bool IsQrUrl(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.Host is "passport.bilibili.com" or "account.bilibili.com";

    internal static string ExtractCookies(HttpResponseMessage response)
    {
        CookieContainer jar = new();
        Uri origin = new("https://passport.bilibili.com/");
        if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            foreach (string header in headers)
                try { jar.SetCookies(origin, header); } catch (CookieException) { /* 忽略无效或不匹配的 Cookie */ }
        return string.Join("; ", jar.GetCookies(new Uri("https://api.bilibili.com/")).Cast<Cookie>()
            .Where(c => c.Name is "SESSDATA" or "bili_jct" or "DedeUserID" or "DedeUserID__ckMd5" or "buvid3" or "buvid4" or "buvid_fp")
            .Select(c => $"{c.Name}={c.Value}"));
    }
    private static string Text(JsonElement data, string name) => data.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static QrSession PublicSession(Session s) => new(s.Id, s.Url, s.Expires, s.State);
}

/// <summary>仅携带受控的诊断文案和数值码，可安全返回 WebUI。</summary>
public sealed class BilibiliLoginException(string code, string message, int? upstreamCode = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int? UpstreamCode { get; } = upstreamCode;
}
