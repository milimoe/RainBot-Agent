using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Services.Config;
using RainBot.Services.Page;
using RainBot.Services.Storage;
using Xunit;

namespace RainBot.Tests;

public class BilibiliLoginTests
{
    private const string Video = "https://www.bilibili.com/video/BV16Faz6tE4F/";
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string SessionCookie = "abc%2Cdef%2Aghi==";
    private const string ApiJson = """{"code":0,"data":{"title":"已解析","owner":{"name":"UP"}}}""";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Generate() => Json(JsonSerializer.Serialize(new { code = 0, data = new { qrcode_key = Key, url = "https://passport.bilibili.com/h5-app/passport/login/scan?qrcode_key=" + Key } }));
    private static HttpResponseMessage Confirm(bool withCookie = true)
    {
        var response = Json("""{"code":0,"data":{"code":0,"refresh_token":"secret-refresh"}}""");
        if (withCookie)
            foreach (string value in new[] { "SESSDATA=" + SessionCookie, "bili_jct=csrf", "DedeUserID=123", "DedeUserID__ckMd5=checksum" })
                response.Headers.TryAddWithoutValidation("Set-Cookie", value + "; Domain=.bilibili.com; Path=/; Expires=Wed, 01 Jan 2031 00:00:00 GMT; Secure; HttpOnly");
        return response;
    }
    private static BilibiliLoginService Login(ServiceProvider provider, IHttpClientFactory factory, IDataProtectionProvider protection) =>
        new(factory, provider.GetRequiredService<Database>(), protection, provider.GetRequiredService<RuntimeConfig>(), NullLogger<BilibiliLoginService>.Instance);
    private static HttpResponseMessage Default(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/x/passport-login/web/qrcode/generate" => Generate(),
        "/x/passport-login/web/qrcode/poll" => Confirm(),
        "/x/web-interface/nav" => Json("""{"code":0,"data":{"isLogin":true}}"""),
        "/x/frontend/finger/spi" => Json("""{"code":0,"data":{"b_3":"device3","b_4":"device4"}}"""),
        _ => Json(ApiJson)
    };

    [Fact]
    public async Task 二维码兼容当前account域名与旧passport域名()
    {
        using var provider = await TestHost.BuildReadyAsync();
        foreach (string host in new[] { "account.bilibili.com", "passport.bilibili.com" })
        {
            var login = Login(provider, new FakeHttpClientFactory(_ => Json(JsonSerializer.Serialize(new
            {
                code = 0,
                data = new { qrcode_key = Key, url = $"https://{host}/h5-app/passport/login/scan?qrcode_key={Key}" }
            }))), new EphemeralDataProtectionProvider());
            var qr = await login.StartAsync(default);
            Assert.Equal(host, new Uri(qr.Url).Host);
            Assert.Equal("pending", qr.State);
        }
    }

    [Theory]
    [InlineData("https://account.bilibili.com.evil.example/login")]
    [InlineData("https://evil.example/login")]
    [InlineData("http://account.bilibili.com/login")]
    [InlineData("https://user:pass@account.bilibili.com/login")]
    [InlineData("https://account.bilibili.com:8443/login")]
    public void 二维码仍拒绝非官方域名明文协议与额外端口(string url) =>
        Assert.False(BilibiliLoginService.IsQrUrl(new Uri(url)));

    [Theory]
    [InlineData(412)]
    [InlineData(503)]
    public async Task 上游HTTP错误保留实际状态码(int status)
    {
        using var provider = await TestHost.BuildReadyAsync();
        var login = Login(provider, new FakeHttpClientFactory(_ => new((HttpStatusCode)status)), new EphemeralDataProtectionProvider());
        var error = await Assert.ThrowsAsync<BilibiliLoginException>(() => login.StartAsync(default));
        Assert.Equal("upstream_http_error", error.Code);
        Assert.Equal(status, error.UpstreamCode);
    }

    [Fact]
    public async Task 业务错误只返回数值码不泄露上游消息()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var login = Login(provider, new FakeHttpClientFactory(_ => Json("""{"code":-352,"message":"SESSDATA=secret"}""")), new EphemeralDataProtectionProvider());
        var error = await Assert.ThrowsAsync<BilibiliLoginException>(() => login.StartAsync(default));
        Assert.Equal(-352, error.UpstreamCode);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task 扫码凭据加密保存重启恢复且不返回到UI()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var factory = new FakeHttpClientFactory(Default);
        var protector = new EphemeralDataProtectionProvider();
        var login = Login(provider, factory, protector);
        var qr = await login.StartAsync(default);
        Assert.NotEqual(Key, qr.Id);
        Assert.Equal("confirmed", (await login.PollAsync(qr.Id, default)).State);
        string stored = (await provider.GetRequiredService<Database>().GetSettingAsync(BilibiliLoginService.StorageKey))!;
        Assert.DoesNotContain(SessionCookie, stored);
        Assert.DoesNotContain("secret-refresh", stored);
        string status = JsonSerializer.Serialize(await login.StatusAsync(true, default));
        Assert.DoesNotContain(SessionCookie, status);
        Assert.DoesNotContain("secret-refresh", status);
        Assert.Equal("valid", (await login.StatusAsync(true, default)).State);
        var restored = Login(provider, factory, protector);
        Assert.Contains("SESSDATA=" + SessionCookie, await restored.GetCookieAsync(default));
        await restored.ClearAsync(default);
        Assert.Empty(await restored.GetCookieAsync(default));
        Assert.Null(await provider.GetRequiredService<Database>().GetSettingAsync(BilibiliLoginService.StorageKey));
    }

    [Fact]
    public async Task 扫码确认缺少SetCookie时不能当作已登录()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var login = Login(provider, new FakeHttpClientFactory(r => r.RequestUri!.AbsolutePath.EndsWith("poll") ? Confirm(false) : Default(r)), new EphemeralDataProtectionProvider());
        var qr = await login.StartAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => login.PollAsync(qr.Id, default));
        Assert.Empty(await login.GetCookieAsync(default));
        Assert.Null(await provider.GetRequiredService<Database>().GetSettingAsync(BilibiliLoginService.StorageKey));
    }

    [Fact]
    public async Task 取消会话与过期状态停止轮询且服务器限制频率()
    {
        using var provider = await TestHost.BuildReadyAsync();
        int polls = 0;
        var login = Login(provider, new FakeHttpClientFactory(r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("poll")) return Default(r);
            polls++;
            return Json("""{"code":0,"data":{"code":86101}}""");
        }), new EphemeralDataProtectionProvider());
        var qr = await login.StartAsync(default);
        Assert.Equal("pending", (await login.PollAsync(qr.Id, default)).State);
        await login.PollAsync(qr.Id, default);
        Assert.Equal(1, polls);
        await login.CancelAsync(qr.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => login.PollAsync(qr.Id, default));
        var expired = Login(provider, new FakeHttpClientFactory(r => r.RequestUri!.AbsolutePath.EndsWith("poll") ? Json("""{"code":0,"data":{"code":86038}}""") : Default(r)), new EphemeralDataProtectionProvider());
        qr = await expired.StartAsync(default);
        Assert.Equal("expired", (await expired.PollAsync(qr.Id, default)).State);
        Assert.Equal("expired", (await expired.PollAsync(qr.Id, default)).State);
    }

    [Fact]
    public async Task 登录过期停止使用自动凭据而风控不判定过期()
    {
        using var provider = await TestHost.BuildReadyAsync();
        int code = 0;
        var login = Login(provider, new FakeHttpClientFactory(r => r.RequestUri!.AbsolutePath.EndsWith("nav") && code != 0 ? Json($"{{\"code\":{code}}}") : Default(r)), new EphemeralDataProtectionProvider());
        var qr = await login.StartAsync(default);
        await login.PollAsync(qr.Id, default);
        code = -352;
        Assert.Equal("risk", (await login.StatusAsync(true, default)).State);
        Assert.NotEmpty(await login.GetCookieAsync(default));
        code = -101;
        Assert.Equal("expired", (await login.StatusAsync(true, default)).State);
        Assert.Empty(await login.GetCookieAsync(default));
        Assert.Equal("expired", (await login.StatusAsync(false, default)).State);
    }

    [Fact]
    public async Task 设备Cookie补齐保留用户身份且短链与外域不收到账号Cookie()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.Page.BilibiliCookies = "SESSDATA=" + SessionCookie + "; buvid3=manual-device";
        List<(string Host, string Cookie)> requests = [];
        var factory = new FakeHttpClientFactory(r =>
        {
            requests.Add((r.RequestUri!.Host, r.Headers.TryGetValues("Cookie", out var v) ? string.Join("; ", v) : ""));
            if (r.RequestUri.Host == "b23.tv") return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/") } };
            if (r.RequestUri.Host == "example.com") return new(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") };
            return Default(r);
        });
        var cookies = new BilibiliCookieProvider(factory, config, NullLogger<BilibiliCookieProvider>.Instance);
        string cookie = await cookies.GetAsync(default);
        Assert.Contains("buvid3=manual-device", cookie);
        Assert.Contains("buvid4=device4", cookie);
        Assert.Contains("SESSDATA=" + SessionCookie, cookie);
        await new PageReaderHttp(factory, cookies).ReadAsync(new Uri("https://b23.tv/test"), 10000, default);
        Assert.DoesNotContain("SESSDATA", requests.Single(r => r.Host == "b23.tv").Cookie);
        Assert.Empty(requests.Single(r => r.Host == "example.com").Cookie);
        await new PageReaderHttp(factory, cookies).ReadAsync(new Uri("https://api.bilibili.com/x/web-interface/view"), 10000, default, json: true);
        Assert.Contains("SESSDATA=" + SessionCookie, requests.Last().Cookie);
    }

    [Fact]
    public async Task 手动凭据风控仅请求一次并对其他BV熔断()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.Page.BilibiliCookies = "SESSDATA=secret; buvid3=one; buvid4=two";
        int calls = 0;
        var factory = new FakeHttpClientFactory(_ => { calls++; return new(HttpStatusCode.PreconditionFailed); });
        var cookies = new BilibiliCookieProvider(factory, config, NullLogger<BilibiliCookieProvider>.Instance);
        var reader = new BilibiliReader(cookies);
        var http = new PageReaderHttp(factory, cookies);
        Assert.Equal("risk", (await reader.ReadAsync(new Uri(Video), http, 10000, default)).Status);
        Assert.Equal("risk", (await reader.ReadAsync(new Uri("https://www.bilibili.com/video/BV1xx411c7mD/"), http, 10000, default)).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 扫码登录后立即重新读取失败缓存()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var factory = new FakeHttpClientFactory(r => r.RequestUri!.AbsolutePath == "/x/web-interface/view"
            && !r.Headers.TryGetValues("Cookie", out var values) ? Json("""{"code":-352}""")
            : r.RequestUri.AbsolutePath == "/x/web-interface/view" && !string.Join(";", r.Headers.GetValues("Cookie")).Contains("SESSDATA")
                ? Json("""{"code":-352}""") : Default(r));
        var login = Login(provider, factory, new EphemeralDataProtectionProvider());
        var cookies = new BilibiliCookieProvider(factory, provider.GetRequiredService<RuntimeConfig>(), NullLogger<BilibiliCookieProvider>.Instance, login);
        using var pages = new PageService(new PageReaderHttp(factory, cookies), new SiteReaderRegistry([new BilibiliReader(cookies)], new OpenGraphReader()), provider.GetRequiredService<RuntimeConfig>(), NullLogger<PageService>.Instance);
        Assert.Equal("risk", (await pages.ReadAsync(Video)).Status);
        var qr = await login.StartAsync(default);
        await login.PollAsync(qr.Id, default);
        Assert.Equal("ok", (await pages.ReadAsync(Video)).Status);
    }

    [Fact]
    public async Task 手动凭据查询与配置列表脱敏且未知域Cookie不导入()
    {
        using var provider = await TestHost.BuildReadyAsync();
        var config = provider.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Page.BilibiliCookies", "SESSDATA=manual-secret");
        Assert.DoesNotContain("manual-secret", (await config.GetAsync("Page.BilibiliCookies"))!);
        Assert.DoesNotContain("manual-secret", (await config.ListAllAsync()).Single(i => i.Key == "Page.BilibiliCookies").Value);
        using var response = Confirm();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "SESSDATA=evil; Domain=evil.example; Path=/");
        Assert.Contains(SessionCookie, BilibiliLoginService.ExtractCookies(response));
        Assert.DoesNotContain("evil", BilibiliLoginService.ExtractCookies(response));
    }
}
