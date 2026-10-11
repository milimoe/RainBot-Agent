using System.Net;
using System.Text;
using System.Text.Json;
using RainBot.Services.Preprocessing;

namespace RainBot.Services.Page;

/// <summary>公开 GitHub 资源的文本读取；不带凭据、不执行源码、不下载发布附件。</summary>
public sealed class GitHubReader : ISiteReader
{
    public bool CanRead(Uri url) => GitHubLink.Parse(url.AbsoluteUri) != null;

    public async Task<PageResult> ReadAsync(Uri url, PageReaderHttp http, int maxBytes, CancellationToken cancellationToken)
    {
        var link = GitHubLink.Parse(url.AbsoluteUri);
        if (link == null) return new("unavailable", url.AbsoluteUri, "github", Error: "不支持的 GitHub 链接类型。");
        PageResult result;
        try
        {
            var response = await ReadApiAsync(http, link.ApiUrl, maxBytes, cancellationToken);
            result = Read(link.Url, response.Content);
        }
        catch (PageHttpException exception) when (exception.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound)
        {
            return new("unavailable", link.Url.AbsoluteUri, "github", Error: exception.Status == HttpStatusCode.NotFound
                ? "GitHub 资源不存在、为私有资源或链接中的分支/文件路径不受支持。"
                : "GitHub API 限流或拒绝访问，未取得内容。");
        }
        if (link.Kind != "repository" || result.Status != "ok") return result;
        // README 是可选补充：没有 README 或读取失败时仍保留实际取得的仓库资料。
        try
        {
            var response = await ReadApiAsync(http, new Uri(link.ApiUrl.AbsoluteUri + "/readme"), maxBytes, cancellationToken);
            using var document = JsonDocument.Parse(response.Content);
            result = result with { Summary = result.Summary + "\nREADME（文本片段）：\n" + DecodeFile(document.RootElement, maxBytes) };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException or FormatException or DecoderFallbackException)
        {
            result = result with { Summary = result.Summary + "\nREADME 未取得，仅提供仓库元信息。" };
        }
        return result;
    }

    public PageResult Read(Uri url, string content)
    {
        var link = GitHubLink.Parse(url.AbsoluteUri);
        if (link == null) return new("unavailable", url.AbsoluteUri, "github", Error: "不支持的 GitHub 链接类型。");
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Boolean(root, "private") == true)
            return new("unavailable", link.Url.AbsoluteUri, "github", Error: "GitHub 返回内容格式异常或不是公开资源。");
        string title, summary;
        switch (link.Kind)
        {
            case "repository":
                title = Text(root, "full_name");
                summary = $"公开仓库；描述：{Text(root, "description")}\n语言：{Text(root, "language")}；Star：{Number(root, "stargazers_count")}；Fork：{Number(root, "forks_count")}；未关闭 Issue/PR：{Number(root, "open_issues_count")}\n默认分支：{Text(root, "default_branch")}；许可证：{Nested(root, "license", "spdx_id")}；归档：{Boolean(root, "archived")}\n最近更新：{Text(root, "updated_at")}";
                break;
            case "issue":
            case "pull":
                title = Text(root, "title");
                bool pull = link.Kind == "pull" || root.TryGetProperty("pull_request", out _);
                summary = $"{(pull ? "PR" : "Issue")} #{Number(root, "number")}；仓库：{link.Repository}；状态：{Text(root, "state")}；作者：{Nested(root, "user", "login")}\n创建：{Text(root, "created_at")}；更新：{Text(root, "updated_at")}；评论数：{Number(root, "comments")}";
                if (link.Kind == "pull") summary += $"\n草稿：{Boolean(root, "draft")}；已合并：{Boolean(root, "merged")}；分支：{Nested(root, "head", "ref")} → {Nested(root, "base", "ref")}；变更文件：{Number(root, "changed_files")}；新增/删除：{Number(root, "additions")}/{Number(root, "deletions")}";
                summary += "\n仅含主帖/描述，不含评论、评审或完整代码差异。\n正文：\n" + Text(root, "body", 6000);
                break;
            case "commit":
                title = Text(root, "sha");
                summary = $"Commit；仓库：{link.Repository}；作者：{Nested(root, "author", "login")}\n只读取提交说明及文件名，不含完整 diff。\n提交说明：\n{Nested(root, "commit", "message", 4000)}";
                if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    summary += "\n变更文件（最多 20 项）：" + string.Join("、", files.EnumerateArray().Take(20).Select(file => Text(file, "filename", 200)));
                break;
            case "release":
                title = Text(root, "tag_name");
                summary = $"Release：{Text(root, "name")}；仓库：{link.Repository}；预发布：{Boolean(root, "prerelease")}\n作者：{Nested(root, "author", "login")}；发布时间：{Text(root, "published_at")}\n不下载发布附件。\n发布说明：\n{Text(root, "body", 6000)}";
                break;
            case "file":
                title = Text(root, "path");
                try { summary = $"文件；仓库：{link.Repository}；ref：{link.Reference}\n文本片段（未执行代码）：\n" + DecodeFile(root, 512 * 1024); }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException or DecoderFallbackException)
                { return new("unavailable", link.Url.AbsoluteUri, "github", Error: "未取得可读取的 UTF-8 文本文件（可能是二进制、过大文件或不支持的分支路径）。"); }
                break;
            default: throw new InvalidOperationException("不支持的 GitHub 链接类型。");
        }
        if (string.IsNullOrWhiteSpace(title))
            return new("unavailable", link.Url.AbsoluteUri, "github", Error: "GitHub API 缺少资源信息，不能据此推断内容。");
        return new("ok", link.Url.AbsoluteUri, "github", title, summary)
        { DisplayCard = BuildCard(link, root, title) };
    }

    private static ParsedContentCard BuildCard(GitHubLink link, JsonElement root, string title)
    {
        List<ParsedContentField> fields = [];
        string description = "", kind = "GitHub";
        switch (link.Kind)
        {
            case "repository":
                kind = "GitHub 仓库";
                description = Text(root, "description");
                fields.AddRange([new("语言", Text(root, "language")), new("Star", Number(root, "stargazers_count").ToString()),
                    new("Fork", Number(root, "forks_count").ToString()), new("Issue / PR", Number(root, "open_issues_count").ToString()),
                    new("默认分支", Text(root, "default_branch")), new("许可证", Nested(root, "license", "spdx_id")),
                    new("状态", Boolean(root, "archived") == true ? "已归档" : "公开"), new("更新", Date(root, "updated_at"))]);
                break;
            case "issue":
            case "pull":
                kind = link.Kind == "pull" || root.TryGetProperty("pull_request", out _) ? "GitHub PR" : "GitHub Issue";
                title = $"#{Number(root, "number")} {title}";
                fields.AddRange([new("仓库", link.Repository), new("作者", Nested(root, "user", "login")),
                    new("状态", Boolean(root, "merged") == true ? "已合并" : Text(root, "state") == "closed" ? "已关闭" : "进行中"),
                    new("评论", Number(root, "comments").ToString()), new("更新", Date(root, "updated_at"))]);
                if (link.Kind == "pull") fields.AddRange([new("分支", Nested(root, "head", "ref") + " → " + Nested(root, "base", "ref")),
                    new("变更", $"{Number(root, "changed_files")} 个文件 · +{Number(root, "additions")} / -{Number(root, "deletions")}")]);
                break;
            case "commit":
                kind = "GitHub Commit";
                title = Nested(root, "commit", "message").Split('\n')[0];
                string sha = Text(root, "sha");
                fields.AddRange([new("仓库", link.Repository), new("作者", Nested(root, "author", "login")), new("SHA", sha[..Math.Min(12, sha.Length)])]);
                break;
            case "release":
                kind = "GitHub Release";
                fields.AddRange([new("仓库", link.Repository), new("名称", Text(root, "name")), new("作者", Nested(root, "author", "login")),
                    new("版本", Boolean(root, "prerelease") == true ? "预发布" : "正式版"), new("发布", Date(root, "published_at"))]);
                break;
            case "file":
                kind = "GitHub 文件";
                fields.AddRange([new("仓库", link.Repository), new("ref", link.Reference), new("大小", Number(root, "size") + " 字节")]);
                break;
        }
        return new(kind, title, description, fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)).ToList());
    }

    private static string Date(JsonElement root, string key) => DateTimeOffset.TryParse(Text(root, key), out var date)
        ? date.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy/MM/dd HH:mm") : "";

    private static string DecodeFile(JsonElement root, int maxBytes)
    {
        if (Text(root, "type") != "file" || Text(root, "encoding") != "base64" || Number(root, "size") > maxBytes)
            throw new InvalidOperationException("不支持的文件类型或大小。");
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("接口缺少文件正文。");
        byte[] bytes = Convert.FromBase64String(Text(root, "content", 2 * 1024 * 1024));
        if (bytes.Length > maxBytes) throw new InvalidOperationException("文件过大。");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("不是文本文件。");
        return text.Length > 6000 ? text[..6000] + "…（内容已截断）" : text;
    }

    private static string Text(JsonElement root, string key, int max = 500) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? Limit(value.GetString() ?? "", max) : "";
    private static string Limit(string text, int max) => text.Length > max ? text[..max] + "…（内容已截断）" : text;
    private static string Nested(JsonElement root, string key, string child, int max = 500) =>
        root.TryGetProperty(key, out var value) ? Text(value, child, max) : "";
    private static long Number(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static bool? Boolean(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static Task<(Uri Url, string Content)> ReadApiAsync(PageReaderHttp http, Uri url, int maxBytes, CancellationToken cancellationToken) =>
        http.ReadAsync(url, maxBytes, cancellationToken, json: true,
            allowedUrl: candidate => candidate.Scheme == "https" && candidate.IdnHost == "api.github.com" && candidate.Port == 443);
}
