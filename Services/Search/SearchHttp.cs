using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using RainBot.Services.Config;

namespace RainBot.Services.Search;

/// <summary>
/// 搜索 HTTP 公共层：统一超时（Search.TimeoutSeconds）、状态码诊断与解析失败的诊断日志。
/// 各后端只管发请求、解析结果，失败原因由这里记录清楚。
/// </summary>
internal static class SearchHttp
{
    /// <summary>发 GET 并返回响应体；HTTP 非 2xx 抛异常（日志已记录状态码与响应长度）</summary>
    internal static async Task<string> GetAsync(
        RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger logger, string backend, string url, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return await SendAsync(config, httpClientFactory, logger, backend, request, ct);
    }

    /// <summary>发 POST JSON（如 Tavily）；HTTP 非 2xx 抛 SearchHttpException（带状态码）</summary>
    internal static async Task<string> PostJsonAsync(
        RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger logger, string backend, string url,
        string jsonBody, string? bearerToken, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        return await SendAsync(config, httpClientFactory, logger, backend, request, ct);
    }

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static async Task<string> SendAsync(
        RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger logger, string backend, HttpRequestMessage request, CancellationToken ct)
    {
        using HttpClient client = httpClientFactory.CreateClient("search");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int seconds = Math.Clamp(config.Config.Search.TimeoutSeconds, 3, 60);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        string url = request.RequestUri?.ToString() ?? "";
        long startMs = Environment.TickCount64;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);
            long elapsed = Environment.TickCount64 - startMs;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("搜索后端 {Backend} 返回 HTTP {Status}（{Length} 字节，{Elapsed}ms）：{Hint}",
                    backend, (int)response.StatusCode, body.Length, elapsed, FailureHint(backend, (int)response.StatusCode));
                throw new SearchHttpException((int)response.StatusCode, $"HTTP {(int)response.StatusCode}：{Truncate(body, 200)}");
            }
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("搜索后端 {Backend} 响应 {Status}（{Length} 字节，{Elapsed}ms）", backend, (int)response.StatusCode, body.Length, elapsed);
            }
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("搜索后端 {Backend} 超时（{Seconds}s，{Url}）：网络不通或被墙，可换 Search.Provider 或调大 Search.TimeoutSeconds",
                backend, seconds, url);
            throw new SearchHttpException(0, $"请求超时（{seconds}s），后端 {backend} 不可达");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "搜索后端 {Backend} 连接失败（{Url}）：网络不可达、DNS 污染或被墙", backend, url);
            throw new SearchHttpException(0, $"连接失败：{ex.Message}");
        }
    }

    /// <summary>常见失败码的排查提示（额度类错误直接说明可回退）</summary>
    private static string FailureHint(string backend, int status) => status switch
    {
        401 or 403 => backend == "tavily" ? "API Key 无效或未授权（检查 Search.TavilyApiKey）" : "被拒绝访问，可能有反爬",
        402 or 432 or 433 => "额度/套餐已用尽（Tavily 会回退到 bing）",
        429 => "触发速率限制（Retry-After 后可重试；Tavily 本轮会回退 bing）",
        >= 500 => "搜索服务端故障，稍后重试",
        _ => "可能被限流或反爬拦截"
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>HTTP 成功但没解析出结果时的诊断日志（附响应长度与文本片段，便于判断是拦截页还是结构变化）</summary>
    internal static void LogEmptyParse(ILogger logger, string backend, string body, string detail)
    {
        string sample = StripTags(body);
        sample = Regex.Replace(sample, @"\s+", " ").Trim();
        if (sample.Length > 200)
        {
            sample = sample[..200] + "…";
        }
        logger.LogWarning("搜索后端 {Backend} 响应成功但未解析出结果（{Length} 字节）：{Detail}；响应文本片段：{Sample}",
            backend, body.Length, detail, sample);
    }

    /// <summary>去 HTML 标签并解码实体</summary>
    internal static string StripTags(string text)
        => WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", "")).Trim();
}

/// <summary>搜索后端 HTTP 错误（带状态码：上层据此区分鉴权/额度/限流类错误并决定回退）</summary>
internal sealed class SearchHttpException(int statusCode, string message) : Exception(message)
{
    /// <summary>HTTP 状态码；0 = 连接失败/超时</summary>
    public int StatusCode { get; } = statusCode;
}
