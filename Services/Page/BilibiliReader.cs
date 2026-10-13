using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RainBot.Services.Preprocessing;

namespace RainBot.Services.Page;

public sealed partial class BilibiliReader(BilibiliCookieProvider? cookies = null) : ISiteReader
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private DateTimeOffset _nextRequest;
    public static bool IsBilibiliHost(string host) => host.TrimEnd('.').Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.TrimEnd('.').EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    public static bool IsShortLink(Uri url) => url.Host.TrimEnd('.').Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
    public static string? TryGetBvid(Uri url)
    {
        if (!IsBilibiliHost(url.Host)) return null;
        Match match = VideoPath().Match(url.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }
    public static Uri VideoUrl(string bvid) => new($"https://www.bilibili.com/video/{bvid}/");
    public bool CanRead(Uri url) => IsShortLink(url) || TryGetBvid(url) != null;

    public async Task<PageResult> ReadAsync(Uri url, PageReaderHttp http, int maxBytes, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken);
        try
        {
            if (cookies?.IsCoolingDown == true) return CoolingDown(url);
            TimeSpan wait = _nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(500);
            return await ReadCoreAsync(url, http, maxBytes, cancellationToken);
        }
        catch (PageHttpException ex) when (ex.Status == HttpStatusCode.PreconditionFailed)
        {
            cookies?.MarkRisk();
            return Risk(url);
        }
        finally { _requests.Release(); }
    }

    private async Task<PageResult> ReadCoreAsync(Uri url, PageReaderHttp http, int maxBytes, CancellationToken cancellationToken)
    {
        if (cookies?.IsCoolingDown == true) return CoolingDown(url);
        string? bvid = TryGetBvid(url);
        if (bvid == null)
        {
            var resolved = await http.ReadAsync(url, maxBytes, cancellationToken, stopAt: candidate => TryGetBvid(candidate) != null);
            bvid = TryGetBvid(resolved.Url);
        }
        if (bvid == null) return new("unavailable", url.AbsoluteUri, "bilibili", Error: "短链未指向支持的 BV 视频页面。");
        Uri api = new($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}");
        Uri page = VideoUrl(bvid);
        try
        {
            PageResult result = Read(page, (await http.ReadAsync(api, maxBytes, cancellationToken, json: true)).Content);
            if (result.Status == "risk") cookies?.MarkRisk();
            return result;
        }
        catch (PageHttpException exception) when (exception.Status == HttpStatusCode.PreconditionFailed)
        {
            // 412 = 风控拦截；缓存 buvid 可能已失效，重新领取后重试一次，仍被拦则转入 risk 兜底。
            if (cookies != null && !await cookies.CanRenewAnonymousAsync(cancellationToken))
            {
                cookies.MarkRisk();
                return Risk(page);
            }
            cookies?.Invalidate();
            await Task.Delay(250, cancellationToken);
            try
            {
                PageResult result = Read(page, (await http.ReadAsync(api, maxBytes, cancellationToken, json: true)).Content);
                if (result.Status == "risk") cookies?.MarkRisk();
                return result;
            }
            catch (PageHttpException retry) when (retry.Status == HttpStatusCode.PreconditionFailed)
            {
                cookies?.MarkRisk();
                return Risk(page);
            }
        }
    }

    private static PageResult Risk(Uri url) => new("risk", url.AbsoluteUri, "bilibili", Error: "B 站风控拦截（HTTP 412），未取得视频信息。");
    private static PageResult CoolingDown(Uri url) => new("risk", url.AbsoluteUri, "bilibili", Error: "B 站请求处于风控冷却期，请稍后重试。");

    public PageResult Read(Uri url, string content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out JsonElement code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int status))
            return new("unavailable", url.AbsoluteUri, "bilibili", Error: "视频接口返回格式异常。");
        if (status != 0)
            return status is -352 or -412
                ? new("risk", url.AbsoluteUri, "bilibili", Error: $"B 站接口风控（{status}），未取得视频信息。")
                : new("unavailable", url.AbsoluteUri, "bilibili", Error: $"B 站接口返回错误码 {status}，未取得视频信息。");
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            return new("unavailable", url.AbsoluteUri, "bilibili", Error: "视频接口没有返回数据。");
        string title = Text(data, "title", 180);
        if (title.Length == 0) return new("unavailable", url.AbsoluteUri, "bilibili", Error: "视频接口缺少标题。");
        string owner = data.TryGetProperty("owner", out JsonElement user) ? Text(user, "name", 100) : "未知";
        long seconds = Number(data, "duration");
        string summary = $"UP 主：{owner}；时长：{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}";
        if (data.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array)
        {
            summary += $"；分 P：{pages.GetArrayLength()}";
            if (pages.GetArrayLength() > 1) summary += "（" + string.Join("、", pages.EnumerateArray().Take(3).Select(page => Text(page, "part", 60))) + "）";
        }
        if (data.TryGetProperty("stat", out JsonElement stat) && stat.ValueKind == JsonValueKind.Object)
            summary += $"；播放：{Number(stat, "view")}；点赞：{Number(stat, "like")}";
        summary += "\n简介：" + Text(data, "desc", 1500);
        List<ParsedContentField> fields = [new("UP", owner), new("时长", seconds >= 3600
            ? $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}" : $"{seconds / 60:D2}:{seconds % 60:D2}")];
        long published = Number(data, "pubdate");
        if (published > 0 && published <= 253402271999)
            fields.Add(new("发布", DateTimeOffset.FromUnixTimeSeconds(published).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm")));
        if (data.TryGetProperty("stat", out var statistics) && statistics.ValueKind == JsonValueKind.Object)
        {
            fields.Add(new("播放", Count(Number(statistics, "view"))));
            fields.Add(new("弹幕", Count(Number(statistics, "danmaku"))));
            fields.Add(new("点赞", Count(Number(statistics, "like"))));
        }
        return new("ok", url.AbsoluteUri, "bilibili", title, summary)
        {
            DisplayCard = new("bilibili", title, Text(data, "desc", 1500), fields, NormalizeCover(Text(data, "pic", 512)))
        };
    }

    private static string Text(JsonElement value, string key, int maxChars)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out JsonElement item) || item.ValueKind != JsonValueKind.String) return "";
        string text = item.GetString() ?? "";
        return text.Length > maxChars ? text[..maxChars] : text;
    }
    private static long Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetInt64(out long number) ? Math.Max(0, number) : 0;

    internal static string NormalizeCover(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(url.UserInfo) || url.Port is not (80 or 443)
            || !url.IdnHost.EndsWith(".hdslb.com", StringComparison.OrdinalIgnoreCase)) return "";
        return new UriBuilder(url) { Scheme = "https", Port = -1, Fragment = "" }.Uri.AbsoluteUri;
    }
    private static string Count(long value) => value >= 100000000 ? (value / 100000000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "亿"
        : value >= 10000 ? (value / 10000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "万"
        : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    [GeneratedRegex(@"^/video/(BV[0-9A-Za-z]{10})(?:/|$)", RegexOptions.None, 100)] private static partial Regex VideoPath();
}
