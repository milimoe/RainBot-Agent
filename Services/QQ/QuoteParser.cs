using System.Text;
using RainBot.Models;

namespace RainBot.Services.QQ;

/// <summary>
/// 引用（回复）消息解析：官方在事件里直接下发被引用内容，无需额外调 API。
/// - message_scene.ext（key=value 数组）给出 msg_idx（本条索引）与 ref_msg_idx（被引用消息索引）；
/// - message_type=103（引用消息）/102（聊天记录）时，被引用的正文与附件在 msg_elements 里
///   （content = 被引用文本，attachments = 被引用图片等，可递归嵌套）。
/// </summary>
public static class QuoteParser
{
    /// <summary>被引用文本的展示上限（防聊天记录式大段内容撑爆上下文）</summary>
    private const int MaxQuotedChars = 600;

    /// <summary>解析结果：索引、被引用文本、被引用图片</summary>
    public sealed record Result(string MsgIdx, string RefMsgIdx, string QuotedText, List<string> QuotedImageUrls)
    {
        public static readonly Result Empty = new("", "", "", []);
    }

    /// <summary>解析一条消息事件里的引用信息（无引用时各项为空）</summary>
    public static Result Parse(MessageScene? scene, MsgElement[]? elements)
    {
        string msgIdx = "";
        string refMsgIdx = "";
        foreach (string pair in scene?.Ext ?? [])
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            string key = pair[..eq].Trim();
            string value = pair[(eq + 1)..].Trim();
            if (key == "msg_idx")
            {
                msgIdx = value;
            }
            else if (key == "ref_msg_idx")
            {
                refMsgIdx = value;
            }
        }

        List<string> images = [];
        string text = Collect(elements, images, depth: 0).Trim();
        if (text.Length > MaxQuotedChars)
        {
            text = text[..MaxQuotedChars] + "…（被引用内容已截断）";
        }
        return new Result(msgIdx, refMsgIdx, text, images);
    }

    /// <summary>递归收集元素正文与图片（深度上限 3，防异常数据无限递归）</summary>
    private static string Collect(MsgElement[]? elements, List<string> images, int depth)
    {
        if (elements == null || elements.Length == 0 || depth > 3)
        {
            return "";
        }
        StringBuilder sb = new();
        foreach (MsgElement element in elements)
        {
            if (!string.IsNullOrWhiteSpace(element.Content))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(element.Content.Trim());
            }
            foreach (Attachment attachment in element.Attachments)
            {
                if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(attachment.Url))
                {
                    images.Add(attachment.Url);
                }
            }
            string nested = Collect(element.MsgElements, images, depth + 1);
            if (nested.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(nested);
            }
        }
        return sb.ToString();
    }
}
