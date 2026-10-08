using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Services.Config;
using RainBot.Services.Search;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 联网搜索：后端可配置（bing 默认 / duckduckgo / searxng）、结果解析、失败与空结果的诊断行为。
/// 背景：duckduckgo.com 在部分网络（中国大陆）DNS 污染不可达，且「拿到响应但解析不出结果」原先无日志，
/// 排查不到原因——这里覆盖新后端的解析与失败可见性。
/// </summary>
public class WebSearchTests
{
    /// <summary>Bing 结果页片段（含 ck/a 跳转包装与 b_lineclamp 摘要）</summary>
    private const string BingHtml = """
    <html><body><ol id="b_results">
    <li class="b_algo" data-id iid=SERP.1><h2 class=""><a target="_blank" href="https://www.bing.com/ck/a?!&amp;&amp;p=abc&amp;u=a1aHR0cHM6Ly93ZWF0aGVyLmV4YW1wbGUuY29tLw&amp;ntb=1" h="ID=SERP,1">今日天气<strong>预报</strong></a></h2><div class="b_caption"><p class="b_lineclamp2" data-x="1">今天多云转晴，最高 26℃。</p></div></li>
    <li class="b_algo" data-id iid=SERP.2><h2 class=""><a href="https://direct.example.com/page" h="ID=SERP,2">直链结果</a></h2><div class="b_caption"><p class="b_lineclamp3">直链摘要内容</p></div></li>
    </ol></body></html>
    """;

    private static BingSearchProvider BingOf(ServiceProvider sp) => new(
        sp.GetRequiredService<RuntimeConfig>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<BingSearchProvider>>());

    private static TavilySearchProvider TavilyOf(ServiceProvider sp) => new(
        sp.GetRequiredService<RuntimeConfig>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<TavilySearchProvider>>());

    /// <summary>按真实装配方式构造路由（含额度计数所需的 Database）</summary>
    private static ConfiguredSearchProvider RouterOf(ServiceProvider sp)
    {
        var config = sp.GetRequiredService<RuntimeConfig>();
        return new ConfiguredSearchProvider(
            config,
            BingOf(sp),
            new DuckDuckGoSearchProvider(config, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<DuckDuckGoSearchProvider>>()),
            new SearxngSearchProvider(config, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<SearxngSearchProvider>>()),
            TavilyOf(sp),
            sp.GetRequiredService<Services.Storage.Database>(),
            sp.GetRequiredService<ILogger<ConfiguredSearchProvider>>());
    }

    [Fact]
    public async Task Bing_解析结果并还原跳转链接()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BingHtml, Encoding.UTF8, "text/html")
        });

        List<SearchResult> results = await BingOf(sp).SearchAsync("今天天气", 3);

        Assert.Equal(2, results.Count);
        // ck/a 跳转包装 → 还原真实 URL
        Assert.Equal("https://weather.example.com/", results[0].Url);
        Assert.Equal("今日天气预报", results[0].Title);
        Assert.Equal("今天多云转晴，最高 26℃。", results[0].Snippet);
        // 已是直链 → 原样保留
        Assert.Equal("https://direct.example.com/page", results[1].Url);
    }

    [Fact]
    public async Task Bing_请求参数带中文市场与条数()
    {
        string requested = "";
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            requested = req.RequestUri!.AbsoluteUri; // 线上实际发送的转义形式
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });

        await BingOf(sp).SearchAsync("今天天气", 5);

        Assert.Contains("cn.bing.com/search", requested);
        Assert.Contains("count=5", requested);
        Assert.Contains("mkt=zh-CN", requested);
        Assert.Contains(Uri.EscapeDataString("今天天气"), requested);
    }

    [Fact]
    public async Task 后端按配置切换()
    {
        string requested = "";
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            requested = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[{"title":"T","url":"https://t.example.com","content":"C"}]}""", Encoding.UTF8, "application/json")
            };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        var configured = RouterOf(sp);

        // 默认 bing
        await configured.SearchAsync("天气", 3);
        Assert.Contains("cn.bing.com", requested);

        // 切到 searxng：走自建实例的 JSON 接口
        await config.SetAsync("Search.SearxngBaseUrl", "https://searx.example.com");
        await config.SetAsync("Search.Provider", "searxng");
        List<SearchResult> results = await configured.SearchAsync("天气", 3);

        Assert.Contains("searx.example.com/search", requested);
        Assert.Contains("format=json", requested);
        Assert.Equal("T", results[0].Title);
        Assert.Equal("https://t.example.com", results[0].Url);
    }

    // ---------- Tavily（优先调用 + 额度用尽回退 bing） ----------

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private static string TavilyJson(params (string Title, string Url, string Content)[] items)
        => JsonSerializer.Serialize(new
        {
            query = "天气",
            results = items.Select(i => new { title = i.Title, url = i.Url, content = i.Content }).ToArray(),
            response_time = 0.5
        });

    /// <summary>Tavily 优先：命中 api.tavily.com、解析结果、当日计数 +1</summary>
    [Fact]
    public async Task Tavily_配置后优先调用并计数()
    {
        int tavilyCalls = 0;
        List<string> authHeaders = [];
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("tavily", StringComparison.Ordinal))
            {
                tavilyCalls++;
                authHeaders.Add(req.Headers.Authorization?.ToString() ?? "");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TavilyJson(("结果一", "https://a.example.com", "摘要一")), Encoding.UTF8, "application/json")
                };
            }
            throw new InvalidOperationException("不应回退到其它后端：" + req.RequestUri);
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");
        await config.SetAsync("Search.TavilyApiKey", "tvly-test-key");
        var db = sp.GetRequiredService<Services.Storage.Database>();

        List<SearchResult> results = await RouterOf(sp).SearchAsync("天气", 3);

        Assert.Equal(1, tavilyCalls);
        Assert.Equal("Bearer tvly-test-key", authHeaders[0]);
        Assert.Equal("结果一", results[0].Title);
        Assert.Equal("https://a.example.com", results[0].Url);
        Assert.Equal("摘要一", results[0].Snippet);
        Assert.Equal(1, await db.GetSearchUsageAsync("tavily", Today)); // 当日计数 +1
    }

    /// <summary>额度用尽：当日计数达到上限后不再调用 Tavily，直接回退 bing</summary>
    [Fact]
    public async Task Tavily_达到每日上限_回退bing()
    {
        int tavilyCalls = 0;
        string requested = "";
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            requested = req.RequestUri!.Host;
            if (requested.Contains("tavily", StringComparison.Ordinal))
            {
                tavilyCalls++;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = requested.Contains("tavily", StringComparison.Ordinal)
                    ? new StringContent(TavilyJson(("T", "https://t.example.com", "C")), Encoding.UTF8, "application/json")
                    : new StringContent(BingHtml, Encoding.UTF8, "text/html")
            };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");
        await config.SetAsync("Search.TavilyApiKey", "tvly-test-key");
        await config.SetAsync("Search.TavilyDailyLimit", "2");
        var db = sp.GetRequiredService<Services.Storage.Database>();
        await db.SetSearchUsageAsync("tavily", Today, 2); // 今日已用满

        List<SearchResult> results = await RouterOf(sp).SearchAsync("天气", 3);

        Assert.Equal(0, tavilyCalls);                 // 不再调用 Tavily
        Assert.Contains("cn.bing.com", requested);    // 回退 bing
        Assert.NotEmpty(results);
    }

    /// <summary>额度用尽（HTTP 432）：回退 bing，并把当日额度标记为用尽，之后直接走 bing</summary>
    [Fact]
    public async Task Tavily_额度用尽错误_回退bing并标记当日用尽()
    {
        int tavilyCalls = 0;
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("tavily", StringComparison.Ordinal))
            {
                tavilyCalls++;
                return new HttpResponseMessage((HttpStatusCode)432)
                {
                    Content = new StringContent("""{"detail":"plan limit exceeded"}""", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");
        await config.SetAsync("Search.TavilyApiKey", "tvly-test-key");
        await config.SetAsync("Search.TavilyDailyLimit", "50");
        var db = sp.GetRequiredService<Services.Storage.Database>();
        var router = RouterOf(sp);

        List<SearchResult> first = await router.SearchAsync("天气", 3);
        Assert.Equal(1, tavilyCalls);                                        // 首次尝试了 Tavily
        Assert.NotEmpty(first);                                              // 但拿到了 bing 的结果
        Assert.True(await db.GetSearchUsageAsync("tavily", Today) >= 50);    // 当日被标记为不可用（哨兵值 ≥ 上限）

        await router.SearchAsync("另一件事", 3);
        Assert.Equal(1, tavilyCalls);                                        // 之后不再尝试 Tavily
    }

    /// <summary>未配置 Key：不调用 Tavily，直接 bing</summary>
    [Fact]
    public async Task Tavily_未配置Key_回退bing()
    {
        int tavilyCalls = 0;
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("tavily", StringComparison.Ordinal))
            {
                tavilyCalls++;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");

        List<SearchResult> results = await RouterOf(sp).SearchAsync("天气", 3);

        Assert.Equal(0, tavilyCalls);
        Assert.NotEmpty(results);
    }

    /// <summary>不限制额度（0）时后端自报用尽：当日也不再多打 Tavily</summary>
    [Fact]
    public async Task Tavily_不限制额度但后端自报用尽_后续跳过()
    {
        int tavilyCalls = 0;
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("tavily", StringComparison.Ordinal))
            {
                tavilyCalls++;
                return new HttpResponseMessage((HttpStatusCode)433) { Content = new StringContent("paygo limit exceeded") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");
        await config.SetAsync("Search.TavilyApiKey", "tvly-test-key");
        await config.SetAsync("Search.TavilyDailyLimit", "0"); // 不限制
        var router = RouterOf(sp);

        await router.SearchAsync("天气", 3);
        await router.SearchAsync("天气2", 3);

        Assert.Equal(1, tavilyCalls); // 只试探一次
    }

    [Fact]
    public async Task Tavily_网络失败_回退bing()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("tavily", StringComparison.Ordinal))
            {
                throw new HttpRequestException("connection refused");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "tavily");
        await config.SetAsync("Search.TavilyApiKey", "tvly-test-key");

        List<SearchResult> results = await RouterOf(sp).SearchAsync("天气", 3);

        Assert.NotEmpty(results); // 网络故障也保证搜索可用（回退 bing）
    }

    [Fact]
    public async Task Searxng未配置基址_返回明确错误()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Search.Provider", "searxng");
        var provider = new SearxngSearchProvider(
            config, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<SearxngSearchProvider>>());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync("天气", 3));
        Assert.Contains("SearxngBaseUrl", ex.Message);
    }

    [Fact]
    public async Task 解析不出结果_返回空列表不抛异常()
    {
        // 200 但是异常页（反爬/结构变化）→ 返回空列表，并由 SearchHttp 记 Warn 诊断日志
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>anomaly detected</body></html>", Encoding.UTF8, "text/html")
        });

        List<SearchResult> results = await BingOf(sp).SearchAsync("今天天气", 3);
        Assert.Empty(results);
    }

    [Fact]
    public async Task 搜索失败_短路避免重复超时()
    {
        int calls = 0;
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("blocked") };
        });
        var tool = new WebSearchTool(sp.GetRequiredService<RuntimeConfig>(), BingOf(sp), sp.GetRequiredService<ILogger<WebSearchTool>>());

        string first = await tool.SearchAsync("今天天气");
        Assert.Contains("搜索失败", first);
        Assert.Equal(1, calls);

        // 后端刚失败 → 60s 内短路，不再发起请求（也不重复等待超时）
        string second = await tool.SearchAsync("另一件事");
        Assert.Contains("暂时不可用", second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 同话题命中缓存_不重复请求()
    {
        int calls = 0;
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BingHtml, Encoding.UTF8, "text/html") };
        });
        var tool = new WebSearchTool(sp.GetRequiredService<RuntimeConfig>(), BingOf(sp), sp.GetRequiredService<ILogger<WebSearchTool>>());

        string first = await tool.SearchAsync("今天天气");
        string second = await tool.SearchAsync(" 今天天气 ");
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }
}
