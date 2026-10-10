using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Services.Config;
using RainBot.Services.Page;
using RainBot.Services.Tools;
using Xunit;

namespace RainBot.Tests;

public class PageReaderTests
{
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("https://example.com:8080/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://weibo.com/post")]
    [InlineData("https://m.weibo.cn/post")]
    [InlineData("https://t.cn/short")]
    [InlineData("https://www.xiaohongshu.com/explore/1")]
    [InlineData("https://xhslink.com/short")]
    [InlineData("https://www.zhihu.com/question/1")]
    [InlineData("https://zhihu.com./question/1")]
    public void 拒绝危险或排除地址(string url) => Assert.Throws<InvalidOperationException>(() => PageUrlPolicy.Validate(url));

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void 检查实际连接IP(string ip, bool allowed) => Assert.Equal(allowed, PageUrlPolicy.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void 混合DNS结果不能绕过内网拦截()
    {
        Assert.Throws<InvalidOperationException>(() => PageUrlPolicy.ValidateResolvedAddresses([]));
        Assert.Throws<InvalidOperationException>(() => PageUrlPolicy.ValidateResolvedAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]));
        PageUrlPolicy.ValidateResolvedAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111")]);
    }

    [Fact]
    public void 保留查询参数并去除片段()
    {
        Assert.Equal("https://example.com/page?id=3&signature=abc", PageUrlPolicy.Validate("https://example.com/page?id=3&signature=abc#section").AbsoluteUri);
        Assert.Equal("weibo.com.example.org", PageUrlPolicy.Validate("https://weibo.com.example.org/").Host);
    }

    [Fact]
    public void 元信息解析忽略脚本并处理属性顺序与实体()
    {
        PageResult result = new OpenGraphReader().Read(new Uri("https://example.com/"), """
            <script>const x = '<meta property="og:title" content="假标题">';</script>
            <!-- <meta property="og:title" content="注释"> -->
            <META content='标题 &amp; 内容' property='og:title'>
            <meta content=" 一段 &quot;摘要&quot; " NAME="description">
            <title>备用标题</title>
            """);
        Assert.Equal("ok", result.Status);
        Assert.Equal("标题 & 内容", result.Title);
        Assert.Equal("一段 \"摘要\"", result.Summary);
        Assert.Equal("unavailable", new OpenGraphReader().Read(new Uri("https://example.com"), "<body>请登录</body>").Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("https://www.zhihu.com/question/1")]
    public async Task 跳转到内网或排除站点时不发送第二次请求(string target)
    {
        int count = 0;
        PageReaderHttp http = new(new FakeHttpClientFactory(_ =>
        {
            count++;
            HttpResponseMessage response = new(HttpStatusCode.Found);
            response.Headers.Location = new Uri(target);
            return response;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.ReadAsync(new Uri("https://example.com/"), 4096, default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task 相对跳转可读取且跳转循环有上限()
    {
        int count = 0;
        PageReaderHttp http = new(new FakeHttpClientFactory(request =>
        {
            count++;
            if (request.RequestUri!.AbsolutePath == "/final") return Html("<title>最终页面</title>");
            HttpResponseMessage response = new(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/final", UriKind.Relative);
            return response;
        }));
        var result = await http.ReadAsync(new Uri("https://example.com/start"), 4096, default);
        Assert.Equal("https://example.com/final", result.Url.AbsoluteUri);
        Assert.Equal(2, count);

        count = 0;
        http = new(new FakeHttpClientFactory(_ =>
        {
            count++;
            HttpResponseMessage response = new(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/loop", UriKind.Relative);
            return response;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.ReadAsync(new Uri("https://example.com/"), 4096, default));
        Assert.Equal(6, count);
    }

    [Fact]
    public async Task 限制响应类型与流式大小()
    {
        PageReaderHttp image = new(new FakeHttpClientFactory(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("image", Encoding.UTF8, "image/png") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => image.ReadAsync(new Uri("https://example.com/"), 4096, default));
        PageReaderHttp oversized = new(new FakeHttpClientFactory(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekableStream(new byte[5000])) };
            response.Content.Headers.ContentType = new("text/html");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oversized.ReadAsync(new Uri("https://example.com/"), 1000, default));
    }

    [Fact]
    public async Task 缓存成功与失败且失败不影响其他链接()
    {
        using ServiceProvider provider = TestHost.Build().Provider;
        int calls = 0;
        using PageService service = Service(provider, new FakeHttpClientFactory(request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath == "/fail" ? new(HttpStatusCode.Forbidden) : Html("<title>正常页面</title>");
        }));
        Assert.Equal("unavailable", (await service.ReadAsync("https://example.com/fail")).Status);
        await service.ReadAsync("https://example.com/fail");
        Assert.Equal("ok", (await service.ReadAsync("https://example.com/ok")).Status);
        await service.ReadAsync("https://example.com/ok#fragment");
        Assert.Equal(2, calls);
        Assert.Equal("blocked", (await service.ReadAsync("https://weibo.com/")).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 超时真正取消读取且外部取消继续传播()
    {
        using ServiceProvider provider = TestHost.Build().Provider;
        provider.GetRequiredService<RuntimeConfig>().Config.Page.RequestTimeoutSeconds = 1;
        using SlowFactory factory = new();
        using PageService service = Service(provider, factory);
        Assert.Equal("timeout", (await service.ReadAsync("https://example.com/slow")).Status);
        Assert.True(factory.Handler.Cancelled);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync("https://example.com/another", cancelled.Token));
    }

    [Fact]
    public async Task 工具注册遵守启动开关并返回完整JSON()
    {
        using ServiceProvider provider = TestHost.Build().Provider;
        RuntimeConfig config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.Page.MaxChars = 800;
        using PageService service = Service(provider, new FakeHttpClientFactory(_ => Html("<title>标题</title><meta name='description' content='" + new string('"', 2000) + "'>")));
        ToolRegistry registry = new(NullLogger<ToolRegistry>.Instance);
        config.Config.Page.ToolEnabled = false;
        new PageTools(service, config).Register(registry);
        Assert.DoesNotContain(registry.GetToolDefs(), tool => tool.Function.Name == "open_page");
        config.Config.Page.ToolEnabled = true;
        new PageTools(service, config).Register(registry);
        Assert.Contains(registry.GetToolDefs(), tool => tool.Function.Name == "open_page");
        ToolExecutionContext context = new() { GroupOpenId = "test", IsAdmin = false, AllowProfileUpdate = false };
        string output = await registry.ExecuteAsync("open_page", "{\"url\":\"https://example.com/\"}", context);
        Assert.True(output.Length <= 1400);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal("ok", json.RootElement.GetProperty("Status").GetString());
        Assert.Contains("请提供", await registry.ExecuteAsync("open_page", "{\"url\":123}", context));
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task 页面配置可列出热改与恢复默认()
    {
        using ServiceProvider provider = await TestHost.BuildReadyAsync();
        RuntimeConfig config = provider.GetRequiredService<RuntimeConfig>();
        Assert.Contains(await config.ListAllAsync(), entry => entry.Key == "Page.MaxChars");
        Assert.Null(await config.SetAsync("Page.MaxChars", "650"));
        Assert.Equal(650, config.Config.Page.MaxChars);
        Assert.Null(await config.ResetAsync("Page.MaxChars"));
        Assert.Equal(400, config.Config.Page.MaxChars);
        Assert.NotNull(await config.SetAsync("Page.ToolEnabled", "false"));
    }

    private static PageService Service(ServiceProvider provider, IHttpClientFactory clients) =>
        new(new PageReaderHttp(clients), new SiteReaderRegistry([], new OpenGraphReader()),
            provider.GetRequiredService<RuntimeConfig>(), NullLogger<PageService>.Instance);

    private sealed class SlowFactory : IHttpClientFactory, IDisposable
    {
        public SlowHandler Handler { get; } = new();
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        public void Dispose() => Handler.Dispose();
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return Html("<title>不会抵达</title>");
        }
    }
}
