using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Search;

/// <summary>
/// SearXNG 自建实例（JSON 接口）：需在实例 settings.yml 里开启 json 格式
/// （search.formats 含 json），并把 Search.SearxngBaseUrl 指到实例地址。
/// 适合自建搜索的用户：可聚合多个引擎、无 API Key、不受单站点反爬限制。
/// </summary>
public class SearxngSearchProvider(RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger<SearxngSearchProvider> logger) : ISearchProvider
{
    private readonly RuntimeConfig _config = config;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<SearxngSearchProvider> _logger = logger;

    public string Name => "searxng";

    public async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        string baseUrl = _config.Config.Search.SearxngBaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            throw new InvalidOperationException("未配置 Search.SearxngBaseUrl（如 https://searx.example.com）");
        }
        string url = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json&language=zh-CN";
        string json = await SearchHttp.GetAsync(_config, _httpClientFactory, _logger, Name, url, ct);

        List<SearchResult> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                SearchHttp.LogEmptyParse(_logger, Name, json, "响应 JSON 里没有 results 数组（实例可能未开启 json 格式）");
                return results;
            }
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (results.Count >= count)
                {
                    break;
                }
                string title = item.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" : "";
                string link = item.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                string snippet = item.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                if (title.Length == 0 || link.Length == 0)
                {
                    continue;
                }
                results.Add(new SearchResult { Title = title, Url = link, Snippet = snippet });
            }
        }
        catch (JsonException ex)
        {
            SearchHttp.LogEmptyParse(_logger, Name, json, $"响应不是合法 JSON：{ex.Message}");
            return results;
        }
        if (results.Count == 0)
        {
            SearchHttp.LogEmptyParse(_logger, Name, json, "JSON 合法但 results 为空");
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("searxng 返回 {Count} 条结果（query={Query}）", results.Count, query);
        }
        return results;
    }
}
