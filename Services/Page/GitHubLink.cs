using System.Globalization;
using System.Text.RegularExpressions;

namespace RainBot.Services.Page;

/// <summary>只将支持的 github.com 页面映射到固定的公开 REST API，不接受用户指定的 API 地址。</summary>
public sealed partial record GitHubLink(Uri Url, Uri ApiUrl, string Kind, string Repository, string Reference = "", string Path = "")
{
    public static GitHubLink? Parse(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)
            || url.Port is not (80 or 443)
            || !(url.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || url.IdnHost.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))) return null;
        string[] parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || !Owner().IsMatch(parts[0])) return null;
        string repo = parts[1];
        if (parts.Length == 2 && repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (!Repo().IsMatch(repo) || repo is "." or "..") return null;
        string repository = (parts[0] + "/" + repo).ToLowerInvariant();
        string kind, suffix, reference = "", path = "";
        if (parts.Length == 2) { kind = "repository"; suffix = ""; }
        else if (parts.Length == 4 && parts[2] is "issues" or "pull"
            && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0)
        {
            kind = parts[2] == "pull" ? "pull" : "issue";
            suffix = $"/{(kind == "pull" ? "pulls" : "issues")}/{number}";
            parts[3] = number.ToString(CultureInfo.InvariantCulture);
        }
        else if (parts.Length == 4 && parts[2] == "commit" && Sha().IsMatch(parts[3]))
        { kind = "commit"; suffix = "/commits/" + parts[3]; }
        else if (parts.Length == 3 && parts[2] == "releases")
        { kind = "release"; suffix = "/releases/latest"; }
        else if (parts.Length == 4 && parts[2] == "releases" && parts[3] == "latest")
        { kind = "release"; suffix = "/releases/latest"; }
        else if (parts.Length >= 5 && parts[2] == "releases" && parts[3] == "tag")
        {
            kind = "release";
            reference = string.Join('/', parts.Skip(4).Select(Uri.UnescapeDataString));
            suffix = "/releases/tags/" + Uri.EscapeDataString(reference);
        }
        else if (parts.Length >= 5 && parts[2] == "blob")
        {
            // GitHub 页面中含斜杠的 ref 与文件路径有歧义；此处仅接受单段 ref（或编码为 %2F 的 ref）。
            kind = "file";
            reference = Uri.UnescapeDataString(parts[3]);
            path = string.Join('/', parts.Skip(4).Select(Uri.UnescapeDataString));
            if (reference.Length == 0 || path.Length == 0 || path.Split('/').Any(segment => segment is "" or "." or "..")) return null;
            suffix = "/contents/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + "?ref=" + Uri.EscapeDataString(reference);
        }
        else return null;
        string canonical = $"https://github.com/{repository}";
        if (kind == "release" && reference.Length == 0) canonical += "/releases/latest";
        else if (kind != "repository") canonical += "/" + string.Join('/', parts.Skip(2));
        return new(new Uri(canonical), new Uri($"https://api.github.com/repos/{repository}{suffix}"), kind, repository, reference, path);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.None, 100)] private static partial Regex Owner();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.None, 100)] private static partial Regex Repo();
    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$", RegexOptions.None, 100)] private static partial Regex Sha();
}
