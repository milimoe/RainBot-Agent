using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RainBot.Services.Config;

namespace RainBot.Services.Search;

/// <summary>
/// DuckDuckGo HTML 端点实现（免费、无需 API Key）。
/// 注意：duckduckgo.com 在部分网络（如中国大陆）不可达，默认后端为 bing。
/// </summary>
public class DuckDuckGoSearchProvider(RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger<DuckDuckGoSearchProvider> logger) : ISearchProvider
{
    private readonly RuntimeConfig _config = config;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<DuckDuckGoSearchProvider> _logger = logger;

    public string Name => "duckduckgo";

    private const string Endpoint = "https://html.duckduckgo.com/html/";

    public async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        using HttpClient client = _httpClientFactory.CreateClient("search");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

        using FormUrlEncodedContent form = new(new Dictionary<string, string> { ["q"] = query });
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int seconds = Math.Clamp(_config.Config.Search.TimeoutSeconds, 3, 60);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        string html;
        long startMs = Environment.TickCount64;
        try
        {
            using HttpResponseMessage response = await client.PostAsync(Endpoint, form, timeout.Token);
            html = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("搜索后端 duckduckgo 返回 HTTP {Status}（{Length} 字节）：可能被限流或反爬拦截",
                    (int)response.StatusCode, html.Length);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("搜索后端 duckduckgo 超时（{Seconds}s）：duckduckgo.com 在部分网络不可达（如中国大陆），建议 Search.Provider 改为 bing", seconds);
            throw new InvalidOperationException($"请求超时（{seconds}s），后端 duckduckgo 不可达");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "搜索后端 duckduckgo 连接失败：DNS 污染 / 被墙 / 网络不可达（建议 Search.Provider 改为 bing）");
            throw new InvalidOperationException($"连接失败：{ex.Message}");
        }

        List<SearchResult> results = [];
        MatchCollection anchors = Regex.Matches(html,
            "class=\"result__a\"[^>]*href=\"(?<url>[^\"]+)\"",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        MatchCollection titles = Regex.Matches(html,
            "class=\"result__a\"[^>]*>(?<title>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        MatchCollection snippets = Regex.Matches(html,
            "class=\"result__snippet\"[^>]*>(?<snip>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < anchors.Count && results.Count < count; i++)
        {
            string url = WebUtility.HtmlDecode(anchors[i].Groups["url"].Value);
            string title = i < titles.Count ? SearchHttp.StripTags(titles[i].Groups["title"].Value) : "";
            string snippet = i < snippets.Count ? SearchHttp.StripTags(snippets[i].Groups["snip"].Value) : "";
            results.Add(new SearchResult { Title = title, Url = url, Snippet = snippet });
        }
        if (results.Count == 0)
        {
            // 也兜住「返回反爬/异常页」：响应是 200 但拿不到任何结果块
            SearchHttp.LogEmptyParse(_logger, Name, html, "未匹配 class=\"result__a\" 结果块（页面结构变化或返回了反爬页）");
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            long elapsed = Environment.TickCount64 - startMs;
            _logger.LogDebug("duckduckgo 返回 {Count} 条结果（query={Query}，{Elapsed}ms）", results.Count, query, elapsed);
        }
        return results;
    }
}
