using System.Text.Json.Serialization;

namespace RainBot.Models;

/// <summary>
/// WebSocket 网关帧（参考 QQBot-WebSocket 项目协议层）
/// </summary>
public class Payload
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("op")]
    public int Op { get; set; } = 0;

    [JsonPropertyName("d")]
    public object Data { get; set; } = new();

    [JsonPropertyName("s")]
    public int SequenceNumber { get; set; } = 0;

    [JsonPropertyName("t")]
    public string EventType { get; set; } = "";
}

public class Author
{
    [JsonPropertyName("user_openid")]
    public string UserOpenId { get; set; } = "";

    [JsonPropertyName("member_openid")]
    public string MemberOpenId { get; set; } = "";
}

public class Attachment
{
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("height")]
    public int Height { get; set; } = 0;

    [JsonPropertyName("width")]
    public int Width { get; set; } = 0;

    [JsonPropertyName("size")]
    public int Size { get; set; } = 0;

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public interface IBotMessage
{
    public string Id { get; }
    public bool IsGroup { get; }
    public string Detail { get; set; }
    public string Timestamp { get; }
    public string OpenId { get; }
    public string AuthorOpenId { get; }
}

/// <summary>群 @ 机器人消息（GROUP_AT_MESSAGE_CREATE）</summary>
public class GroupAtMessage : IBotMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("author")]
    public Author Author { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("group_openid")]
    public string GroupOpenId { get; set; } = "";

    [JsonPropertyName("attachments")]
    public Attachment[] Attachments { get; set; } = [];

    [JsonIgnore]
    public string OpenId => GroupOpenId;

    [JsonIgnore]
    public string Detail
    {
        get => Content;
        set => Content = value;
    }

    [JsonIgnore]
    public bool IsGroup => true;

    [JsonIgnore]
    public string AuthorOpenId => Author.UserOpenId;
}

/// <summary>群消息（全量模式 GROUP_MESSAGE_CREATE，需开启"接收所有消息"）</summary>
public class GroupMessage : IBotMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("author")]
    public Author Author { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("group_openid")]
    public string GroupOpenId { get; set; } = "";

    [JsonPropertyName("attachments")]
    public Attachment[] Attachments { get; set; } = [];

    [JsonPropertyName("msg_seq")]
    public long MsgSeq { get; set; } = 0;

    [JsonIgnore]
    public string OpenId => GroupOpenId;

    [JsonIgnore]
    public string Detail
    {
        get => Content;
        set => Content = value;
    }

    [JsonIgnore]
    public bool IsGroup => true;

    [JsonIgnore]
    public string AuthorOpenId => Author.UserOpenId;
}

/// <summary>C2C 私聊消息（C2C_MESSAGE_CREATE，MVP 阶段仅记录）</summary>
public class C2CMessage : IBotMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("author")]
    public Author Author { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("attachments")]
    public Attachment[] Attachments { get; set; } = [];

    [JsonIgnore]
    public string OpenId => Author.UserOpenId;

    [JsonIgnore]
    public string Detail
    {
        get => Content;
        set => Content = value;
    }

    [JsonIgnore]
    public bool IsGroup => false;

    [JsonIgnore]
    public string AuthorOpenId => Author.UserOpenId;
}

public class AccessTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public string ExpiresIn { get; set; } = "";
}

public class MediaResponse
{
    [JsonPropertyName("file_uuid")]
    public string FileUuid { get; set; } = "";

    [JsonPropertyName("file_info")]
    public string FileInfo { get; set; } = "";

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}

public class UploadMediaResult
{
    /// <summary>文件 UUID，成功时有值</summary>
    public string? FileUuid { get; set; }

    /// <summary>文件信息，成功时有值</summary>
    public string? FileInfo { get; set; }

    /// <summary>有效期（秒）</summary>
    public int Ttl { get; set; }

    /// <summary>错误信息，成功时为 null</summary>
    public string? Error { get; set; }

    /// <summary>是否上传成功</summary>
    [JsonIgnore]
    public bool IsSuccess => string.IsNullOrEmpty(Error);
}

public class WebSocketHelloData
{
    [JsonPropertyName("heartbeat_interval")]
    public int HeartbeatInterval { get; set; }
}

public class WebSocketIdentifyData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("intents")]
    public long Intents { get; set; }

    [JsonPropertyName("shard")]
    public List<int>? Shard { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, string> Properties { get; set; } = [];
}

public class WebSocketResumeData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("seq")]
    public int Seq { get; set; }
}

/// <summary>
/// 回复内容（MVP 只用文本，Markdown 保留供扩展）
/// </summary>
public class BotReply
{
    /// <summary>纯文本内容</summary>
    public string? Text { get; set; }

    public static implicit operator BotReply(string text) => new() { Text = text };
}
