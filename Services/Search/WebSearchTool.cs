using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using RainBot.Services.Config;
using RainBot.Services.Tools;

namespace RainBot.Services.Search;

/// <summary>搜索结果条目</summary>
public class SearchResult
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Snippet { get; init; } = "";
}

/// <summary>搜索后端抽象（当前实现 DuckDuckGo，可替换 Bing/SearXNG）</summary>
public interface ISearchProvider
{
    Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default);
}

/// <summary>
/// web_search 工具：搜索 + 同话题本地缓存（同一话题 10 分钟内重复搜索直接命中，
/// 避免重复调用搜索服务）。缓存键为规范化 query。
/// </summary>
public class WebSearchTool(RuntimeConfig config, ISearchProvider provider, ILogger<WebSearchTool> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ISearchProvider _provider = provider;
    private readonly ILogger<WebSearchTool> _logger = logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, string Text)> _cache = new();

    /// <summary>执行搜索（注册为 web_search 工具）</summary>
    public async Task<string> SearchAsync(string query, string? topic = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "搜索关键词不能为空。";
        }
        string key = Normalize(query);
        int cacheMinutes = _config.Config.Trigger.SearchCacheMinutes;
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(cacheMinutes))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("搜索缓存命中：{Query}", query);
            return cached.Text;
        }

        try
        {
            List<SearchResult> results = await _provider.SearchAsync(query, 3, ct);
            if (results.Count == 0)
            {
                return "没有搜索到相关内容。";
            }
            string text = string.Join("\n", results.Select((r, i) => $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
            _cache[key] = (DateTimeOffset.UtcNow, text);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "搜索失败：{Query}", query);
            return $"搜索失败：{ex.Message}";
        }
    }

    private static string Normalize(string query) => query.Trim().ToLowerInvariant();
}

/// <summary>
/// DuckDuckGo HTML 端点实现（免费、无需 API Key）。
/// </summary>
public class DuckDuckGoSearchProvider(IHttpClientFactory httpClientFactory, ILogger<DuckDuckGoSearchProvider> logger) : ISearchProvider
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<DuckDuckGoSearchProvider> _logger = logger;

    private const string Endpoint = "https://html.duckduckgo.com/html/";

    public async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        using HttpClient client = _httpClientFactory.CreateClient("duckduckgo");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        using FormUrlEncodedContent form = new(new Dictionary<string, string> { ["q"] = query });
        using HttpResponseMessage response = await client.PostAsync(Endpoint, form, ct);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(ct);

        List<SearchResult> results = [];
        MatchCollection anchors = Regex.Matches(html,
            "class=\"result__a\"[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        MatchCollection snippets = Regex.Matches(html,
            "class=\"result__snippet\"[^>]*>(?<snip>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < anchors.Count && results.Count < count; i++)
        {
            Match anchor = anchors[i];
            string url = WebUtility.HtmlDecode(anchor.Groups["url"].Value);
            string title = StripTags(WebUtility.HtmlDecode(anchor.Groups["title"].Value));
            string snippet = i < snippets.Count ? StripTags(WebUtility.HtmlDecode(snippets[i].Groups["snip"].Value)) : "";
            results.Add(new SearchResult { Title = title, Url = url, Snippet = snippet });
        }
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("DuckDuckGo 返回 {Count} 条结果（query={Query}）", results.Count, query);
        return results;
    }

    private static string StripTags(string text)
    {
        string noTags = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(noTags).Trim();
    }
}
