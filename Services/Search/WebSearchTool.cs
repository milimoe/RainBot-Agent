using System.Collections.Concurrent;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Search;

/// <summary>搜索结果条目</summary>
public class SearchResult
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Snippet { get; init; } = "";
}

/// <summary>搜索后端抽象（bing / duckduckgo / searxng，由 Search.Provider 选择）</summary>
public interface ISearchProvider
{
    /// <summary>后端名（日志与错误信息里标识用）</summary>
    string Name { get; }

    Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default);
}

/// <summary>
/// web_search 工具：搜索 + 同话题本地缓存（同一话题 10 分钟内重复搜索直接命中，
/// 避免重复调用搜索服务）。缓存键为规范化 query。
/// 后端故障时短路一段时间（NegativeCacheSeconds），避免每次搜索都等到超时、拖住回复。
/// </summary>
public class WebSearchTool(RuntimeConfig config, ISearchProvider provider, ILogger<WebSearchTool> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ISearchProvider _provider = provider;
    private readonly ILogger<WebSearchTool> _logger = logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, string Text)> _cache = new();

    /// <summary>后端故障后的短路时长：期间直接返回失败说明，不再发起请求</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);
    private readonly Lock _breakerLock = new();
    private DateTimeOffset _failUntil = DateTimeOffset.MinValue;
    private string _failReason = "";

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

        // 后端刚失败过：短路，避免每次搜索都等到超时（模型可据此改用已有知识作答）
        lock (_breakerLock)
        {
            if (_failUntil > DateTimeOffset.UtcNow)
            {
                int seconds = (int)Math.Ceiling((_failUntil - DateTimeOffset.UtcNow).TotalSeconds);
                _logger.LogWarning("搜索后端 {Backend} 处于故障短路中（{Seconds}s 后重试）：{Reason}", _provider.Name, seconds, _failReason);
                return $"搜索服务暂时不可用（后端 {_provider.Name}：{_failReason}，约 {seconds} 秒后可重试）。此问题无需再调用搜索，请直接用已有知识作答或如实说明。";
            }
        }

        int maxResults = Math.Clamp(_config.Config.Search.MaxResults, 1, 8);
        long startMs = Environment.TickCount64;
        try
        {
            List<SearchResult> results = await _provider.SearchAsync(query, maxResults, ct);
            long elapsed = Environment.TickCount64 - startMs;
            if (results.Count == 0)
            {
                // 关键诊断：拿到响应却解析不出结果 → 明确记日志，区分「被反爬拦截」与「确实没有结果」
                _logger.LogWarning(
                    "搜索无结果：backend={Backend} query={Query} elapsed={Elapsed}ms（HTTP 成功但未解析出结果，通常是搜索页被拦截或页面结构变化；可换 Search.Provider 或看更早的 Warn 日志）",
                    _provider.Name, query, elapsed);
                return "没有搜索到相关内容。";
            }
            string text = string.Join("\n", results.Select((r, i) => $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
            _cache[key] = (DateTimeOffset.UtcNow, text);
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("搜索成功：backend={Backend} query={Query} → {Count} 条（{Elapsed}ms）", _provider.Name, query, results.Count, elapsed);
            ClearFailure();
            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            long elapsed = Environment.TickCount64 - startMs;
            _logger.LogWarning(ex, "搜索失败：backend={Backend} query={Query} elapsed={Elapsed}ms（后端不可达 / 超时 / HTTP 错误，详见异常与更早的 Warn 日志）",
                _provider.Name, query, elapsed);
            MarkFailure(ex.Message);
            return $"搜索失败（{_provider.Name}）：{ex.Message}";
        }
    }

    private void MarkFailure(string reason)
    {
        lock (_breakerLock)
        {
            _failUntil = DateTimeOffset.UtcNow + FailureBackoff;
            _failReason = reason;
        }
    }

    private void ClearFailure()
    {
        lock (_breakerLock)
        {
            _failUntil = DateTimeOffset.MinValue;
            _failReason = "";
        }
    }

    private static string Normalize(string query) => query.Trim().ToLowerInvariant();
}

/// <summary>
/// 按配置选择搜索后端（Search.Provider 热改即时生效）：
/// bing（默认，cn.bing.com 国内可直连）/ duckduckgo / searxng（自建实例）/ tavily（商业 API）。
/// tavily 有每日额度：额度用尽或调用失败时自动回退 bing（额度计数持久化，按本地日期跨天重置）。
/// </summary>
public class ConfiguredSearchProvider(
    RuntimeConfig config,
    BingSearchProvider bing,
    DuckDuckGoSearchProvider duckDuckGo,
    SearxngSearchProvider searxng,
    TavilySearchProvider tavily,
    Database db,
    ILogger<ConfiguredSearchProvider> logger) : ISearchProvider
{
    private readonly RuntimeConfig _config = config;
    private readonly BingSearchProvider _bing = bing;
    private readonly DuckDuckGoSearchProvider _duckDuckGo = duckDuckGo;
    private readonly SearxngSearchProvider _searxng = searxng;
    private readonly TavilySearchProvider _tavily = tavily;
    private readonly Database _db = db;
    private readonly ILogger<ConfiguredSearchProvider> _logger = logger;

    /// <summary>额度计数用的后端名（数据库主键）</summary>
    private const string TavilyUsageKey = "tavily";

    /// <summary>后端自报额度用尽时写入的计数哨兵（与用户配置的上限无关，当日不再尝试该后端）</summary>
    private const int ExhaustedMarker = 1_000_000;

    public string Name => _config.Config.Search.Provider.Trim().ToLowerInvariant();

    public Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct = default)
    {
        string name = Name;
        switch (name)
        {
            case "duckduckgo":
            case "ddg":
                return _duckDuckGo.SearchAsync(query, count, ct);
            case "searxng":
            case "searx":
                return _searxng.SearchAsync(query, count, ct);
            case "tavily":
                return TavilyWithFallbackAsync(query, count, ct);
            case "bing":
                return _bing.SearchAsync(query, count, ct);
            default:
                _logger.LogWarning("未知的搜索后端 {Name}，回退 bing（可选：bing / duckduckgo / searxng / tavily）", name);
                return _bing.SearchAsync(query, count, ct);
        }
    }

    /// <summary>
    /// Tavily 优先 + 回退策略：
    /// 未配置 Key / 当日额度用尽（Tavily 自报额度用尽也计入）→ 直接 bing；
    /// 调用成功 → 当日计数 +1；调用失败（鉴权、限流、网络等）→ 记日志并回退 bing，保证搜索可用。
    /// </summary>
    private async Task<List<SearchResult>> TavilyWithFallbackAsync(string query, int count, CancellationToken ct)
    {
        SearchConfig cfg = _config.Config.Search;
        if (string.IsNullOrWhiteSpace(cfg.TavilyApiKey))
        {
            _logger.LogWarning("已选择 tavily 但未配置 Search.TavilyApiKey，本次回退 bing");
            return await _bing.SearchAsync(query, count, ct);
        }

        string day = DateTime.Now.ToString("yyyy-MM-dd"); // 按服务器本地日期计自然日
        int limit = cfg.TavilyDailyLimit;
        int used = await _db.GetSearchUsageAsync(TavilyUsageKey, day);
        bool exhausted = used >= ExhaustedMarker || (limit > 0 && used >= limit);
        if (exhausted)
        {
            _logger.LogInformation("Tavily 今日不可用（已用 {Used}，上限 {Limit}），本次回退 bing", used, limit);
            return await _bing.SearchAsync(query, count, ct);
        }

        try
        {
            List<SearchResult> results = await _tavily.SearchAsync(query, count, ct);
            int now = await _db.IncrementSearchUsageAsync(TavilyUsageKey, day);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Tavily 今日第 {Now} 次调用（上限 {Limit}，0 = 不限）", now, limit);
            }
            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SearchHttpException ex)
        {
            // 额度 / 鉴权类错误：标记当日额度用尽，避免后续每次搜索都白跑一趟
            if (ex.StatusCode is 401 or 402 or 432 or 433)
            {
                await _db.SetSearchUsageAsync(TavilyUsageKey, day, ExhaustedMarker);
                _logger.LogWarning("Tavily 不可用（HTTP {Status}：{Reason}），已标记今日不可用，回退 bing",
                    ex.StatusCode, ex.Message);
            }
            else
            {
                _logger.LogWarning("Tavily 调用失败（HTTP {Status}：{Reason}），本次回退 bing", ex.StatusCode, ex.Message);
            }
            return await _bing.SearchAsync(query, count, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tavily 调用异常，本次回退 bing");
            return await _bing.SearchAsync(query, count, ct);
        }
    }
}
