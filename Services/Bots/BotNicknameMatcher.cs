using System.Text.RegularExpressions;

namespace RainBot.Services.Bots;

/// <summary>
/// 机器人昵称匹配：群里喊了昵称但没真 @ 时，把消息按 @ 处理。
///
/// 配置串用空白（含全角空格）、半角/全角逗号、半角/全角分号（顺带支持顿号）分隔多个昵称。
/// 「独立完整出现」指昵称两侧不能再紧接字母、数字或汉字等词内字符：
/// 「小雨，帮我查一下」命中「小雨」，「雨 早上好」命中「雨」，
/// 而「雨伞」「下雨了」「小雨伞」都不算命中，避免把昵称当子串误判。
/// </summary>
public static class BotNicknameMatcher
{
    /// <summary>配置串分隔符：空白 + , ，; ；、</summary>
    private static readonly Regex SeparatorRegex = new(@"[\s,;，；、]+", RegexOptions.Compiled);

    /// <summary>词内字符：Unicode 字母（含 CJK 汉字）与数字；昵称两侧遇到它们即不算独立词</summary>
    private const string WordChar = @"\p{L}\p{N}";

    /// <summary>把配置串拆成昵称列表（去空、去重、保序）</summary>
    public static List<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return [.. SeparatorRegex.Split(raw)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>消息文本里是否独立完整出现任一昵称（英文大小写不敏感）</summary>
    public static bool Matches(string? content, string? raw)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }
        List<string> nicknames = Parse(raw);
        if (nicknames.Count == 0)
        {
            return false;
        }

        // 长昵称优先，避免同前缀昵称（小雨 / 雨）顺序影响结果；
        // 每个候选都经转义，昵称里的 . * ( ) 等字符按字面匹配。
        string pattern = "(?<![" + WordChar + "])(?:"
            + string.Join("|", nicknames.OrderByDescending(n => n.Length).Select(Regex.Escape))
            + ")(?![" + WordChar + "])";
        return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
