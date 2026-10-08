using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RainBot.Services.Config;

namespace RainBot.Services.Search;

/// <summary>
/// Bing 搜索（HTML 抓取，默认后端）：cn.bing.com 国内可直连、结果稳定，无需 API Key。
/// 结果链接是 bing.com/ck/a?...&amp;u=a1&lt;base64url&gt; 跳转包装，需还原真实 URL。
/// </summary>
public class BingSearchProvider(RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger<BingSearchProvider> logger) : ISearchProvider
{
    private readonly RuntimeConfig _config = config;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<BingSearchProvider> _logger = logger;

    public string Name => "bing";

    /// <summary>国内可直连的端点；www.bing.com 会按出口 IP 给日文/英文结果</summary>
    private const string Endpoint = "https://cn.bing.com/search";

    private static readonly Regex BlockRegex = new("<li class=\"b_algo\"", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new("<h2[^>]*>\\s*<a[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SnippetRegex = new("<p class=\"b_lineclamp[^\"]*\"[^>]*>(?<snip>.*?)</p>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FallbackSnippetRegex = new("<p[^>]*>(?<snip>.*?)</p>", RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        string url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&count={Math.Clamp(count, 1, 10)}&mkt=zh-CN&setlang=zh-CN";
        string html = await SearchHttp.GetAsync(_config, _httpClientFactory, _logger, Name, url, ct);

        string[] blocks = BlockRegex.Split(html);
        List<SearchResult> results = [];
        for (int i = 1; i < blocks.Length && results.Count < count; i++)
        {
            Match title = TitleRegex.Match(blocks[i]);
            if (!title.Success)
            {
                continue;
            }
            Match snippet = SnippetRegex.Match(blocks[i]);
            if (!snippet.Success)
            {
                snippet = FallbackSnippetRegex.Match(blocks[i]);
            }
            results.Add(new SearchResult
            {
                Title = SearchHttp.StripTags(WebUtility.HtmlDecode(title.Groups["title"].Value)),
                Url = ResolveUrl(WebUtility.HtmlDecode(title.Groups["url"].Value)),
                Snippet = snippet.Success ? SearchHttp.StripTags(WebUtility.HtmlDecode(snippet.Groups["snip"].Value)) : ""
            });
        }
        if (results.Count == 0)
        {
            SearchHttp.LogEmptyParse(_logger, Name, html, "未能匹配 <li class=\"b_algo\"> 结果块");
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("bing 返回 {Count} 条结果（query={Query}）", results.Count, query);
        }
        return results;
    }

    /// <summary>还原 bing 跳转包装（ck/a?...&amp;u=a1&lt;base64url&gt;）为真实 URL；已是直链则原样返回</summary>
    private static string ResolveUrl(string href)
    {
        int marker = href.IndexOf("&u=a1", StringComparison.Ordinal);
        if (marker < 0)
        {
            return href;
        }
        string encoded = href[(marker + 5)..];
        int amp = encoded.IndexOf('&');
        if (amp >= 0)
        {
            encoded = encoded[..amp];
        }
        try
        {
            string padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? decoded : href;
        }
        catch (FormatException)
        {
            return href; // 非 base64 包装（部分结果直接给直链）
        }
    }
}
