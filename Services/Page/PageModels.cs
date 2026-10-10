namespace RainBot.Services.Page;

/// <summary>外部资料，不代表机器人已经浏览了视频、音频或完整文章。</summary>
public sealed record PageResult(string Status, string Url, string Reader = "", string Title = "", string Summary = "", string Error = "");

public interface ISiteReader
{
    bool CanRead(Uri url);
    PageResult Read(Uri url, string content);
    async Task<PageResult> ReadAsync(Uri url, PageReaderHttp http, int maxBytes, CancellationToken cancellationToken)
    {
        var page = await http.ReadAsync(url, maxBytes, cancellationToken);
        return Read(page.Url, page.Content);
    }
}

/// <summary>优先使用匹配的站点读取器，其余页面读取通用元信息。</summary>
public sealed class SiteReaderRegistry(IEnumerable<ISiteReader> readers, OpenGraphReader fallback)
{
    public PageResult Read(Uri url, string content) =>
        (readers.FirstOrDefault(reader => reader.CanRead(url)) ?? fallback).Read(url, content);
    public Task<PageResult> ReadAsync(Uri url, PageReaderHttp http, int maxBytes, CancellationToken cancellationToken) =>
        (readers.FirstOrDefault(reader => reader.CanRead(url)) ?? fallback).ReadAsync(url, http, maxBytes, cancellationToken);
}
