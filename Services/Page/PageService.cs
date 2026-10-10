using Microsoft.Extensions.Caching.Memory;
using RainBot.Services.Config;

namespace RainBot.Services.Page;

/// <summary>供工具和后续自动预取共用的入口；缓存有界，失败仅影响当前 URL。</summary>
public sealed class PageService(PageReaderHttp http, SiteReaderRegistry readers, RuntimeConfig config,
    ILogger<PageService> logger) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512 });
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    public async Task<PageResult> ReadAsync(string input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri url;
        try { url = PageUrlPolicy.Validate(input); }
        catch (InvalidOperationException ex) { return new("blocked", "", Error: ex.Message); }
        if (BilibiliReader.TryGetBvid(url) is { } bvid) url = BilibiliReader.VideoUrl(bvid);
        if (_cache.TryGetValue(url.AbsoluteUri, out PageResult? cached)) return cached!;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Config.Page.RequestTimeoutSeconds, 1, 60)));
        bool entered = false;
        PageResult result;
        try
        {
            await _concurrency.WaitAsync(timeout.Token);
            entered = true;
            result = await readers.ReadAsync(url, http,
                Math.Clamp(config.Config.Page.MaxResponseKb, 1, 2048) * 1024, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { result = new("timeout", url.AbsoluteUri, Error: "读取超时，未取得页面内容。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // 不记录地址参数、响应正文或可能含地址的异常详情。
            logger.LogDebug("页面读取失败：{Host} / {ErrorType}", url.IdnHost, ex.GetType().Name);
            result = new("unavailable", url.AbsoluteUri, Error: ex is InvalidOperationException ? ex.Message : "无法读取页面，不能据此推断内容。");
        }
        finally { if (entered) _concurrency.Release(); }
        _cache.Set(url.AbsoluteUri, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = result.Status == "ok"
                ? TimeSpan.FromMinutes(Math.Clamp(config.Config.Page.CacheMinutes, 1, 1440)) : TimeSpan.FromSeconds(60)
        });
        if (result.Status == "ok" && result.Url != url.AbsoluteUri)
            _cache.Set(result.Url, result, new MemoryCacheEntryOptions { Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Clamp(config.Config.Page.CacheMinutes, 1, 1440)) });
        return result;
    }

    public void Dispose() { _cache.Dispose(); _concurrency.Dispose(); }
}
