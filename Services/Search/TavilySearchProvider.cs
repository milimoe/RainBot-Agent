using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Search;

/// <summary>
/// Tavily 搜索后端（商业化搜索 API，返回干净的结构化结果，无需反爬对抗）。
/// 端点：POST https://api.tavily.com/search，鉴权：Authorization: Bearer tvly-xxx。
/// 每日调用额度由 Search.TavilyDailyLimit 控制、计数持久化在数据库（按本地日期），
/// 额度用尽或调用失败时由 ConfiguredSearchProvider 回退到 bing。
/// </summary>
public class TavilySearchProvider(RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger<TavilySearchProvider> logger) : ISearchProvider
{
    private readonly RuntimeConfig _config = config;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<TavilySearchProvider> _logger = logger;

    public string Name => "tavily";

    private const string Endpoint = "https://api.tavily.com/search";

    public async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        string apiKey = _config.Config.Search.TavilyApiKey.Trim();
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("未配置 Search.TavilyApiKey");
        }

        string body = JsonSerializer.Serialize(new
        {
            query,
            max_results = Math.Clamp(count, 1, 20),
            search_depth = "basic", // basic = 1 credit/次，advanced 更贵且更慢
            topic = "general"
        });
        string json = await SearchHttp.PostJsonAsync(_config, _httpClientFactory, _logger, Name, Endpoint, body, apiKey, ct);

        List<SearchResult> results = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                SearchHttp.LogEmptyParse(_logger, Name, json, "响应 JSON 里没有 results 数组");
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
                string content = item.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                if (title.Length == 0 || link.Length == 0)
                {
                    continue;
                }
                results.Add(new SearchResult { Title = title, Url = link, Snippet = content });
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
            _logger.LogDebug("tavily 返回 {Count} 条结果（query={Query}）", results.Count, query);
        }
        return results;
    }
}
