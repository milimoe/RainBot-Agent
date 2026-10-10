using System.Net;
using System.Text;

namespace RainBot.Services.Page;

public sealed class PageReaderHttp(IHttpClientFactory clients)
{
    public async Task<(Uri Url, string Content)> ReadAsync(Uri url, int maxBytes, CancellationToken cancellationToken,
        bool json = false, Func<Uri, bool>? stopAt = null)
    {
        using HttpClient client = clients.CreateClient("page_reader");
        for (int hop = 0; hop <= 5; hop++)
        {
            url = PageUrlPolicy.Validate(url.AbsoluteUri);
            if (stopAt?.Invoke(url) == true) return (url, "");
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd(json ? "application/json" : "text/html, application/xhtml+xml;q=0.9");
            if (BilibiliReader.IsBilibiliHost(url.Host)) request.Headers.Referrer = new Uri("https://www.bilibili.com/");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop == 5 || response.Headers.Location is not { } location)
                    throw new InvalidOperationException("页面跳转次数过多或跳转地址缺失。");
                url = PageUrlPolicy.Validate(new Uri(url, location).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new PageHttpException(response.StatusCode);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (json ? mediaType != "application/json" : mediaType is not ("text/html" or "application/xhtml+xml"))
                throw new InvalidOperationException(json ? "视频接口未返回 JSON 数据。" : "只读取 HTML 页面，不下载图片、视频或其他文件。");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException("页面超过读取大小上限。");
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(chunk, cancellationToken);
                if (count == 0) break;
                if (buffer.Length + count > maxBytes) throw new InvalidOperationException("页面超过读取大小上限。");
                buffer.Write(chunk, 0, count);
            }
            Encoding encoding = Encoding.UTF8;
            string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            if (!string.IsNullOrEmpty(charset))
            {
                try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
            }
            return (url, encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        throw new InvalidOperationException("页面跳转次数过多。");
    }
}

public sealed class PageHttpException(HttpStatusCode status) : InvalidOperationException($"站点返回 HTTP {(int)status}，未取得页面内容。")
{
    public HttpStatusCode Status { get; } = status;
}
