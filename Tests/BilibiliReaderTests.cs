using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Page;
using Xunit;

namespace RainBot.Tests;

public class BilibiliReaderTests
{
    private const string ApiJson = """{"code":0,"data":{"title":"测试视频","owner":{"name":"测试 UP"},"duration":125,"desc":"真实简介","pages":[{"part":"第一部分"},{"part":"第二部分"}],"stat":{"view":123,"like":45}}}""";
    private const string Video = "https://www.bilibili.com/video/BV16Faz6tE4F/";

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV16Faz6tE4F/?share_source=copy_web", "BV16Faz6tE4F")]
    [InlineData("https://m.bilibili.com/video/BV16Faz6tE4F", "BV16Faz6tE4F")]
    [InlineData("https://bilibili.com.evil.example/video/BV16Faz6tE4F/", null)]
    [InlineData("https://example.com/BV16Faz6tE4F", null)]
    [InlineData("https://www.bilibili.com/video/BV16Faz6tE4Fextra", null)]
    public void 只从合法视频路径提取BV(string url, string? expected) => Assert.Equal(expected, BilibiliReader.TryGetBvid(new Uri(url)));

    [Fact]
    public async Task 完整视频链接直接读取API并共享带参数缓存()
    {
        using ServiceProvider provider = TestHost.Build().Provider;
        int calls = 0;
        using PageService pages = Service(provider, request =>
        {
            calls++;
            Assert.Equal("api.bilibili.com", request.RequestUri!.Host);
            Assert.Equal("?bvid=BV16Faz6tE4F", request.RequestUri.Query);
            Assert.Equal("https://www.bilibili.com/", request.Headers.Referrer!.AbsoluteUri);
            return Json(ApiJson);
        });
        PageResult result = await pages.ReadAsync(Video + "?vd_source=tracking");
        Assert.Equal("ok", result.Status);
        Assert.Equal("bilibili", result.Reader);
        Assert.Equal(Video, result.Url);
        Assert.Equal("测试视频", result.Title);
        Assert.Contains("测试 UP", result.Summary);
        Assert.Contains("00:02:05", result.Summary);
        Assert.Contains("分 P：2", result.Summary);
        Assert.Contains("真实简介", result.Summary);
        await pages.ReadAsync(Video + "?share_source=copy_web");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 短链跳转后直接读取API无需下载视频页()
    {
        using ServiceProvider provider = TestHost.Build().Provider;
        List<string> hosts = [];
        using PageService pages = Service(provider, request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "b23.tv")
            {
                HttpResponseMessage response = new(HttpStatusCode.Found);
                response.Headers.Location = new Uri(Video + "?share_source=copy_web");
                return response;
            }
            return Json(ApiJson);
        });
        Assert.Equal("ok", (await pages.ReadAsync("https://b23.tv/1Fu5W89")).Status);
        Assert.Equal(new[] { "b23.tv", "api.bilibili.com" }, hosts);
        await pages.ReadAsync(Video);
        Assert.Equal(2, hosts.Count);
    }

    [Theory]
    [InlineData("{\"code\":-352}", "风控")]
    [InlineData("{\"code\":-404}", "-404")]
    [InlineData("{\"code\":0,\"data\":{}}", "标题")]
    [InlineData("{\"unexpected\":1}", "格式异常")]
    public void API失败不编造视频资料(string json, string error)
    {
        PageResult result = new BilibiliReader().Read(new Uri(Video), json);
        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Title);
        Assert.Contains(error, result.Error);
    }

    [Fact]
    public void 提取两种文本链接并排除卡片素材与其他站点()
    {
        List<string> links = LinkPrefetcher.Extract("【标题】https://b23.tv/1Fu5W89，看看 " + Video + "?share_source=copy_web " + Video);
        Assert.Equal(new[] { "https://b23.tv/1Fu5W89", Video }, links);
        Assert.Empty(LinkPrefetcher.Extract("[卡片消息] 小程序\npreview: https://b23.tv/cover"));
        Assert.Empty(LinkPrefetcher.Extract("https://www.zhihu.com/question/1 https://qq.ugcimg.cn/cover"));
    }

    [Fact]
    public async Task 指代追问跨人回溯链接而普通话题不抓取()
    {
        using ServiceProvider provider = await TestHost.BuildReadyAsync();
        HistoryStore history = provider.GetRequiredService<HistoryStore>();
        await history.AppendAsync("group", TestHelpers.Msg("group", "alice", Video));
        int calls = 0;
        using PageService pages = Service(provider, _ => { calls++; return Json(ApiJson); });
        LinkPrefetcher prefetch = new(pages, provider.GetRequiredService<RuntimeConfig>(), history);
        var unrelated = TestHelpers.Msg("group", "bob", "今天天气不错");
        Assert.Empty(await prefetch.PrefetchAsync(unrelated, [unrelated], "", DateTimeOffset.UtcNow, default));
        var followup = TestHelpers.Msg("group", "bob", "这个咋样");
        Assert.Single(await prefetch.PrefetchAsync(followup, [followup], "", DateTimeOffset.UtcNow, default));
        Assert.Equal(1, calls);
        provider.GetRequiredService<RuntimeConfig>().Config.Page.LinkLookbackSeconds = 0;
        Assert.Empty(await prefetch.PrefetchAsync(followup, [followup], "", DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task 预取资料仅进入本轮F块并显示失败状态()
    {
        using ServiceProvider provider = await TestHost.BuildReadyAsync();
        TriggerContext ctx = new()
        {
            GroupOpenId = "group", Type = TriggerType.Passive, Reason = "测试",
            LinkResults = [new("ok", Video, "bilibili", "唯一视频标题", "唯一简介"), new("timeout", "https://b23.tv/slow", Error: "读取超时")]
        };
        var composed = await provider.GetRequiredService<BlockComposer>().BuildAsync(ctx);
        Assert.Contains("唯一视频标题", composed.Messages[3].Content);
        Assert.Contains("读取超时", composed.Messages[3].Content);
        Assert.Contains("未观看视频", composed.Messages[3].Content);
        Assert.DoesNotContain("唯一视频标题", composed.Messages[2].Content);
        Assert.True(composed.BlockTokens["F 当前触发"] > 0);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static PageService Service(ServiceProvider provider, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new PageReaderHttp(new FakeHttpClientFactory(responder)), new SiteReaderRegistry([new BilibiliReader()], new OpenGraphReader()),
            provider.GetRequiredService<RuntimeConfig>(), NullLogger<PageService>.Instance);

    [Fact]
    public void 公网DNS依然拒绝内网及代理假IP答案()
    {
        using JsonDocument safe = JsonDocument.Parse("""{"Status":0,"Answer":[{"type":5,"data":"cdn.example"},{"type":1,"data":"8.8.8.8","TTL":20}]}""");
        Assert.Single(PublicPageDnsResolver.Parse(safe.RootElement, out int ttl));
        Assert.Equal(20, ttl);
        using JsonDocument unsafeAnswer = JsonDocument.Parse("""{"Status":0,"Answer":[{"type":1,"data":"198.18.0.10","TTL":20}]}""");
        Assert.Throws<InvalidOperationException>(() => PublicPageDnsResolver.Parse(unsafeAnswer.RootElement, out _));
    }

    [Fact]
    public async Task 真实处理流程在调用模型前注入资料()
    {
        string modelBody = "";
        await using ServiceProvider provider = await TestHost.BuildReadyAsync(request =>
        {
            if (request.RequestUri!.Host == "api.bilibili.com") return Json(ApiJson);
            if (request.RequestUri.AbsolutePath.Contains("chat/completions")) modelBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"choices":[{"message":{"role":"assistant","content":"这个视频挺有意思"}}],"usage":{"prompt_tokens":100,"completion_tokens":10}}""");
        });
        provider.GetRequiredService<RuntimeConfig>().Config.Fun = new FunConfig
        {
            EnableReplyYes = false, EnableReplyNo = false, EnableReverseAt = false,
            EnableOsm = false, EnableRepeat = false, EnableCallBrother = false
        };
        await provider.GetRequiredService<RainBot.Services.QQ.MessageProcessor>().ProcessAsync(TestHelpers.Msg("group", "alice", Video, isAt: true), default);
        using JsonDocument body = JsonDocument.Parse(modelBody);
        string blockF = body.RootElement.GetProperty("messages")[3].GetProperty("content").GetString()!;
        Assert.Contains("测试视频", blockF);
        Assert.Contains("真实简介", blockF);
    }
}
