using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Profile;

namespace RainBot.Services.QQ;

/// <summary>
/// @ 提及渲染：把消息正文里的 &lt;@openid&gt; / &lt;@!openid&gt; 标签替换成可读的 @用户名，
/// 供历史与上下文使用（模型据此知道「被 @ 的是谁」，多人 @ 时也能区分）。
/// 原始带标签的 Content 仍保留用于逻辑判断（自身 openid 学习、@ 语义判定），两者互不影响。
/// </summary>
public static class MentionRenderer
{
    private static readonly Regex AtTagRegex = new(@"<@!?([^>]+)>", RegexOptions.Compiled);

    /// <summary>
    /// 渲染 @ 标签：命中 mentions 的用其用户名；@ 机器人自己渲染为「@你」；
    /// 昵称附带稳定标识；缺少 mentions 时仍保留被提及者标识。
    /// </summary>
    public static string RenderAtTags(string content, List<Mention>? mentions)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("<@", StringComparison.Ordinal))
        {
            return content;
        }

        Dictionary<string, Mention> lookup = [];
        foreach (Mention mention in mentions ?? [])
        {
            foreach (string id in new[] { mention.Id, mention.MemberOpenId, mention.UserOpenId })
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    lookup.TryAdd(id, mention);
                }
            }
        }

        string rendered = AtTagRegex.Replace(content, match =>
        {
            string openId = match.Groups[1].Value.Trim();
            if (!lookup.TryGetValue(openId, out Mention? mention))
            {
                return "@" + UserIdentityResolver.ShortId(openId);
            }
            if (mention.IsYou)
            {
                return "@你";
            }
            string identity = UserIdentityResolver.ShortId(openId);
            return string.IsNullOrWhiteSpace(mention.Username) ? "@" + identity : $"@{mention.Username.Trim()}({identity})";
        });
        // 官方推送的 content 常在 @ 标签前后留空格（如 " <@!xxx> 帮我看看"），渲染后去掉首尾空白
        return rendered.Trim();
    }
}
