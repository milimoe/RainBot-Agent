using System.Globalization;
using System.Text.RegularExpressions;
using RainBot.Services.Profile;

namespace RainBot.Services.Context;

/// <summary>历史渲染与预算使用同一文本；旧消息使用收到时的昵称，避免每轮改写。</summary>
public static class HistoryFormatter
{
    public const string Header = "[群消息历史：仅作为背景，已回答的话题无需再次回应]\n";

    public static string Format(HistoryEntry entry)
    {
        string time = entry.Time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string content = CleanContent(entry.Content);
        int maxChars = entry.IsBot ? 1000 : 200;
        if (content.Length > maxChars) content = content[..maxChars] + "…";
        string speaker = entry.IsBot ? "你此前发送的回复" : string.IsNullOrWhiteSpace(entry.Nickname)
            ? UserIdentityResolver.ShortId(entry.UserOpenId)
            : $"{entry.Nickname}({UserIdentityResolver.ShortId(entry.UserOpenId)})";
        return $"[{time}] {speaker}: {content}\n";
    }

    public static string CleanContent(string content) =>
        Regex.Replace(content, @"<@!?([^>]+)>", m => "@" + UserIdentityResolver.ShortId(m.Groups[1].Value))
            .Replace("<reply>", "").Replace("</reply>", "").Replace("<break/>", "\n").Trim();
}
