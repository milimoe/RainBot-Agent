using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainBot.Services.OneBot;

/// <summary>
/// OneBot11 事件（仅取本项目需要的字段）。
/// 群消息事件形态：
/// { time, self_id, post_type:"message", message_type:"group", message_id, group_id, user_id,
///   message: string | segment[], raw_message, sender:{ user_id, nickname, card, role } }
/// </summary>
public class OneBotEvent
{
    [JsonPropertyName("time")]
    public long Time { get; set; }

    /// <summary>机器人自身 QQ 号（自动学习身份用）</summary>
    [JsonPropertyName("self_id")]
    public long SelfId { get; set; }

    [JsonPropertyName("post_type")]
    public string PostType { get; set; } = "";

    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    [JsonPropertyName("sub_type")]
    public string? SubType { get; set; }

    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("group_id")]
    public long? GroupId { get; set; }

    [JsonPropertyName("user_id")]
    public long? UserId { get; set; }

    /// <summary>消息内容：字符串或消息段数组</summary>
    [JsonPropertyName("message")]
    public JsonElement Message { get; set; }

    [JsonPropertyName("raw_message")]
    public string? RawMessage { get; set; }

    [JsonPropertyName("sender")]
    public OneBotEventSender? Sender { get; set; }

    [JsonPropertyName("meta_event_type")]
    public string? MetaEventType { get; set; }
}

public class OneBotEventSender
{
    [JsonPropertyName("user_id")]
    public long? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("card")]
    public string? Card { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>解析后的消息段（只保留类型与 data）</summary>
public readonly record struct OneBotSegment(string Type, JsonElement Data);

/// <summary>OneBot11 API 请求（正向调用）</summary>
public class OneBotApiRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    /// <summary>回声字段：用于把响应关联回请求（WebSocket 通道必需）</summary>
    [JsonPropertyName("echo")]
    public string? Echo { get; set; }
}

/// <summary>OneBot11 API 响应</summary>
public class OneBotApiResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("retcode")]
    public int Retcode { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("echo")]
    public string? Echo { get; set; }

    [JsonPropertyName("wording")]
    public string? Wording { get; set; }

    /// <summary>是否成功（retcode 0 为成功；部分实现用 1 表示异步已提交，同样视为成功）</summary>
    public bool IsSuccess => Retcode == 0 || Retcode == 1;
}

/// <summary>OneBot11 消息段构造与解析工具</summary>
public static class OneBotMessage
{
    public const string TypeText = "text";
    public const string TypeAt = "at";
    public const string TypeReply = "reply";
    public const string TypeImage = "image";

    /// <summary>把 message 字段（字符串或消息段数组）解析为消息段列表</summary>
    public static List<OneBotSegment> Parse(JsonElement message)
    {
        List<OneBotSegment> segments = [];
        if (message.ValueKind == JsonValueKind.String)
        {
            segments.Add(new OneBotSegment(TypeText, JsonSerializer.SerializeToElement(new { text = message.GetString() ?? "" })));
            return segments;
        }
        if (message.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }
        foreach (JsonElement item in message.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string type = item.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
            JsonElement data = item.TryGetProperty("data", out JsonElement d) ? d : default;
            segments.Add(new OneBotSegment(type, data));
        }
        return segments;
    }

    /// <summary>取消息段中的文本参数（如 at 的 qq、text 的 text）</summary>
    public static string GetString(OneBotSegment segment, string key)
    {
        if (segment.Data.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (!segment.Data.TryGetProperty(key, out JsonElement value))
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => value.ToString()
        };
    }

    /// <summary>拼接消息中的纯文本（丢弃 at/reply/图片等非文本段）</summary>
    public static string ExtractText(JsonElement message)
    {
        System.Text.StringBuilder sb = new();
        foreach (OneBotSegment seg in Parse(message))
        {
            if (seg.Type == TypeText)
            {
                sb.Append(GetString(seg, "text"));
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>是否 @ 了指定 QQ 号（含 @全体成员）</summary>
    public static bool IsAt(JsonElement message, string selfQq)
    {
        if (string.IsNullOrWhiteSpace(selfQq))
        {
            return false;
        }
        foreach (OneBotSegment seg in Parse(message))
        {
            if (seg.Type != TypeAt)
            {
                continue;
            }
            string qq = GetString(seg, "qq");
            if (qq == selfQq || qq.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>取被引用（回复）的原消息 ID</summary>
    public static string? GetReplyId(JsonElement message)
    {
        foreach (OneBotSegment seg in Parse(message))
        {
            if (seg.Type == TypeReply)
            {
                string id = GetString(seg, "id");
                if (id.Length > 0)
                {
                    return id;
                }
            }
        }
        return null;
    }

    // ---------- 构造 ----------

    public static object Text(string text) => new { type = TypeText, data = new { text } };

    public static object At(string qq) => new { type = TypeAt, data = new { qq } };

    public static object Reply(string id) => new { type = TypeReply, data = new { id } };

    public static object Image(string file) => new { type = TypeImage, data = new { file } };

    /// <summary>
    /// 组装发送消息体：可选 [回复引用] + [艾特] + [文本]。
    /// OneBot11 的消息既可以是字符串也可以是段数组，这里统一用段数组以保留 at/reply 语义。
    /// </summary>
    public static List<object> Build(string content, string? atUserId = null, string? replyMessageId = null)
    {
        List<object> message = [];
        if (!string.IsNullOrWhiteSpace(replyMessageId))
        {
            message.Add(Reply(replyMessageId));
        }
        if (!string.IsNullOrWhiteSpace(atUserId))
        {
            message.Add(At(atUserId));
        }
        string text = !string.IsNullOrWhiteSpace(atUserId) ? " " + content.Trim() : content.Trim();
        if (text.Trim().Length > 0 || message.Count == 0)
        {
            message.Add(Text(text));
        }
        return message;
    }
}
