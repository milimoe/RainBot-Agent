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

    /// <summary>群名片/昵称（官方 API 提供，可能为空）</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    /// <summary>是否为机器人自己（全量消息回显机器人消息时为 true）</summary>
    [JsonPropertyName("bot")]
    public bool IsBot { get; set; }
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

/// <summary>
/// 消息 @ 提及（新版 payload 的 mentions 字段）。
/// 官方文档声称不含机器人自身，但实际推送会包含：@ 机器人时对应项的
/// is_you=true（"是否机器人自己"），是判断全量消息是否 @ 机器人的权威信号。
/// </summary>
public class Mention
{
    /// <summary>被 @ 用户的 ID（OpenID 格式）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>是否为机器人</summary>
    [JsonPropertyName("bot")]
    public bool IsBot { get; set; }

    /// <summary>是否为当前机器人自己（权威的 @ 信号）</summary>
    [JsonPropertyName("is_you")]
    public bool IsYou { get; set; }

    /// <summary>群成员 OpenID（群聊场景）</summary>
    [JsonPropertyName("member_openid")]
    public string MemberOpenId { get; set; } = "";

    /// <summary>用户 OpenID（单聊场景）</summary>
    [JsonPropertyName("user_openid")]
    public string UserOpenId { get; set; } = "";

    /// <summary>用户名/群名片</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    /// <summary>@ 范围（如 single=单人）</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    /// <summary>群内角色</summary>
    [JsonPropertyName("member_role")]
    public string MemberRole { get; set; } = "";
}

public interface IBotMessage
{
    public string Id { get; }
    public bool IsGroup { get; }
    public string Detail { get; set; }
    public string Timestamp { get; }
    public string OpenId { get; }
    public string AuthorOpenId { get; }

    /// <summary>消息内容类型：0=普通文本 / 3=结构化卡片 / 101=并行消息 / 102=聊天记录 / 103=引用消息</summary>
    public int MessageType { get; }

    /// <summary>消息元素（引用/聊天记录消息在此携带被引用内容与附件）</summary>
    public MsgElement[] MsgElements { get; }

    /// <summary>消息场景（ext 含 msg_idx / ref_msg_idx / auth_token）</summary>
    public MessageScene? Scene { get; }
}

/// <summary>
/// 消息场景：官方 ext 为 key=value 字符串数组，引用场景下含
/// msg_idx（本条消息索引）、ref_msg_idx（被引用消息索引）、auth_token（鉴权令牌）。
/// </summary>
public class MessageScene
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("ext")]
    public string[] Ext { get; set; } = [];
}

/// <summary>
/// 消息元素：message_type=103（引用消息）/102（聊天记录）时承载被引用内容。
/// 可递归嵌套（msg_elements 内还有 msg_elements）。
/// </summary>
public class MsgElement
{
    /// <summary>该元素对应的被引用消息索引</summary>
    [JsonPropertyName("msg_idx")]
    public string MsgIdx { get; set; } = "";

    [JsonPropertyName("author")]
    public Author? Author { get; set; }

    /// <summary>该元素的消息内容类型（含义同上，103 = 其本身也是引用）</summary>
    [JsonPropertyName("message_type")]
    public int MessageType { get; set; }

    /// <summary>消息正文内容（被引用的文本）</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>该元素携带的附件（被引用的图片等）</summary>
    [JsonPropertyName("attachments")]
    public Attachment[] Attachments { get; set; } = [];

    /// <summary>嵌套消息元素（递归结构）</summary>
    [JsonPropertyName("msg_elements")]
    public MsgElement[] MsgElements { get; set; } = [];
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

    [JsonPropertyName("mentions")]
    public List<Mention> Mentions { get; set; } = [];

    [JsonPropertyName("message_type")]
    public int MessageType { get; set; } = 0;

    [JsonPropertyName("msg_elements")]
    public MsgElement[] MsgElements { get; set; } = [];

    [JsonPropertyName("message_scene")]
    public MessageScene? Scene { get; set; }

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

    [JsonPropertyName("mentions")]
    public List<Mention> Mentions { get; set; } = [];

    [JsonPropertyName("message_type")]
    public int MessageType { get; set; } = 0;

    [JsonPropertyName("msg_elements")]
    public MsgElement[] MsgElements { get; set; } = [];

    [JsonPropertyName("message_scene")]
    public MessageScene? Scene { get; set; }

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

    /// <summary>@ 提及（私聊一般没有，保留字段以兼容异常推送）</summary>
    [JsonPropertyName("mentions")]
    public List<Mention> Mentions { get; set; } = [];

    [JsonPropertyName("message_type")]
    public int MessageType { get; set; } = 0;

    [JsonPropertyName("msg_elements")]
    public MsgElement[] MsgElements { get; set; } = [];

    [JsonPropertyName("message_scene")]
    public MessageScene? Scene { get; set; }

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
