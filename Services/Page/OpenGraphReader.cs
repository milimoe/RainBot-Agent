using System.Net;
using System.Text.RegularExpressions;

namespace RainBot.Services.Page;

/// <summary>只读取标题和声明的摘要，不执行脚本，不把导航栏当作文章正文。</summary>
public sealed partial class OpenGraphReader : ISiteReader
{
    public bool CanRead(Uri url) => true;

    public PageResult Read(Uri url, string content)
    {
        content = InertContent().Replace(content, " ");
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTags().Matches(content))
        {
            Dictionary<string, string> attrs = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match attr in Attributes().Matches(tag.Value))
                attrs[attr.Groups[1].Value] = attr.Groups[2].Success ? attr.Groups[2].Value
                    : attr.Groups[3].Success ? attr.Groups[3].Value : attr.Groups[4].Value;
            string name = attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name") ?? "";
            if (name.Length > 0 && attrs.TryGetValue("content", out string? value)) metadata.TryAdd(name, value);
        }
        string title = Clean(metadata.GetValueOrDefault("og:title") ?? metadata.GetValueOrDefault("twitter:title")
            ?? TitleTag().Match(content).Groups[1].Value, 180);
        string summary = Clean(metadata.GetValueOrDefault("og:description") ?? metadata.GetValueOrDefault("description")
            ?? metadata.GetValueOrDefault("twitter:description") ?? "", 2000);
        return title.Length == 0 && summary.Length == 0
            ? new("unavailable", url.AbsoluteUri, "opengraph", Error: "没有取得页面标题或摘要，可能需要登录或执行脚本。")
            : new("ok", url.AbsoluteUri, "opengraph", title, summary);
    }

    private static string Clean(string text, int maxChars)
    {
        string value = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(text, " ")), " ").Trim();
        return value.Length > maxChars ? value[..maxChars] : value;
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase, 100)] private static partial Regex MetaTags();
    [GeneratedRegex("([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.None, 100)] private static partial Regex Attributes();
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 100)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 100)] private static partial Regex Tags();
    [GeneratedRegex(@"\s+", RegexOptions.None, 100)] private static partial Regex Whitespace();
    [GeneratedRegex(@"<!--.*?-->|<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 100)]
    private static partial Regex InertContent();
}
