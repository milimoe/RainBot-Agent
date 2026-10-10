using System.Text.Json;
using System.Text.RegularExpressions;

namespace RainBot.Services.Page;

public sealed partial class BilibiliReader : ISiteReader
{
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
        string? bvid = TryGetBvid(url);
        if (bvid == null)
        {
            var resolved = await http.ReadAsync(url, maxBytes, cancellationToken, stopAt: candidate => TryGetBvid(candidate) != null);
            bvid = TryGetBvid(resolved.Url);
        }
        if (bvid == null) return new("unavailable", url.AbsoluteUri, "bilibili", Error: "短链未指向支持的 BV 视频页面。");
        var response = await http.ReadAsync(new Uri($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}"), maxBytes, cancellationToken, json: true);
        return Read(VideoUrl(bvid), response.Content);
    }

    public PageResult Read(Uri url, string content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out JsonElement code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int status))
            return new("unavailable", url.AbsoluteUri, "bilibili", Error: "视频接口返回格式异常。");
        if (status != 0)
            return new("unavailable", url.AbsoluteUri, "bilibili", Error: status == -352
                ? "B 站接口风控，未取得视频信息。" : $"B 站接口返回错误码 {status}，未取得视频信息。");
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
        return new("ok", url.AbsoluteUri, "bilibili", title, summary);
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
    [GeneratedRegex(@"^/video/(BV[0-9A-Za-z]{10})(?:/|$)", RegexOptions.None, 100)] private static partial Regex VideoPath();
}
