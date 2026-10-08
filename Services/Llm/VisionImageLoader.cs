namespace RainBot.Services.Llm;

/// <summary>
/// 视觉图片加载器：把图片 URL 下载并编码为 base64 data URL，供多模态请求内联（DeepSeek 视觉格式）。
/// 全程只驻内存、不落盘，因此无需清理：单条消息用完即随请求体释放。
/// 下载失败 / 超限 / 非图片一律返回 null，调用方降级为纯文本处理。
/// </summary>
public class VisionImageLoader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisionImageLoader> _logger;

    public VisionImageLoader(IHttpClientFactory httpClientFactory, ILogger<VisionImageLoader> logger)
    {
        _httpClient = httpClientFactory.CreateClient("vision");
        _logger = logger;
        // CDN（如腾讯多媒体域名）对无 UA 请求可能直接拒绝
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RainBot/1.0");
    }

    /// <summary>单图大小上限（DeepSeek 单图 32MiB、请求体 48MiB；这里留足余量并控内存）</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>下载超时（秒）：识图是锦上添花，不能拖住整个回复</summary>
    private const int DownloadTimeoutSeconds = 20;

    /// <summary>
    /// 下载并转为 data URL（如 data:image/jpeg;base64,...）；失败返回 null（降级纯文本）。
    /// </summary>
    public async Task<string?> ToDataUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DownloadTimeoutSeconds));

            using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("图片下载失败（HTTP {Status}），本轮按纯文本处理", (int)response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                _logger.LogWarning("图片超过 {Max}MB 上限（{Size} 字节），本轮按纯文本处理", MaxBytes / 1024 / 1024, response.Content.Headers.ContentLength);
                return null;
            }

            byte[] bytes = await ReadCappedAsync(response, timeout.Token);
            if (bytes.Length == 0)
            {
                _logger.LogWarning("图片下载为空，本轮按纯文本处理");
                return null;
            }
            string? mime = SniffMime(bytes);
            if (mime == null)
            {
                _logger.LogWarning("附件不是受支持的图片格式（JPEG/PNG/GIF/WebP），本轮按纯文本处理");
                return null;
            }
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("图片已内联：{Mime}，{Size} 字节", mime, bytes.Length);
            }
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图片下载/编码失败，本轮按纯文本处理");
            return null;
        }
    }

    /// <summary>流式读取并强制上限（Content-Length 缺失或撒谎时兜底）</summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                return [];
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>按文件头判断真实格式（DeepSeek 亦按内容而非扩展名/声明类型判断）</summary>
    private static string? SniffMime(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return "image/gif";
        }
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }
        return null;
    }
}
