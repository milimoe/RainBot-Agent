namespace RainBot.Models;

/// <summary>触发类型</summary>
public enum TriggerType
{
    /// <summary>被动触发：@ 机器人 / 回复机器人</summary>
    Passive,

    /// <summary>随机插嘴：普通群消息按概率触发（未 @ 机器人）</summary>
    RandomChat,

    /// <summary>主动暖群：密度唤醒或沉默唤醒</summary>
    Warmup
}

/// <summary>
/// 统一后的入站消息（协议层解析 → 去重 → 进入处理队列）
/// </summary>
public class IncomingMessage
{
    /// <summary>来源机器人实例 Id（决定数据命名空间与发送回程）</summary>
    public string BotId { get; init; } = "";

    /// <summary>消息 ID（用于去重）</summary>
    public required string MsgId { get; init; }

    /// <summary>消息序号（QQ 推送字段）</summary>
    public long MsgSeq { get; init; }

    /// <summary>群 OpenID（群消息必有）</summary>
    public required string GroupOpenId { get; init; }

    /// <summary>发送者 OpenID</summary>
    public required string SenderOpenId { get; init; }

    /// <summary>发送者昵称/群名片（官方 API 提供，可能为空）</summary>
    public string? Username { get; init; }

    /// <summary>消息内容（含 <@!xxx> 等富文本标签）</summary>
    public required string Content { get; init; }

    /// <summary>
    /// 图片消息的图片 URL 列表（QQ 官方 = attachments 中 image/* + 引用消息里的图片；OneBot = image 消息段直链）。
    /// 仅用于本轮多模态请求内联，不写入历史（历史里是 [图片] 占位）。
    /// </summary>
    public List<string> ImageUrls { get; init; } = [];

    /// <summary>被引用的消息正文（官方引用消息随事件下发；空 = 本条不是引用）</summary>
    public string QuotedContent { get; init; } = "";

    /// <summary>本条消息索引（官方 message_scene.ext 的 msg_idx，供他人引用时本地回溯）</summary>
    public string MsgIdx { get; init; } = "";

    /// <summary>被引用消息索引（官方 message_scene.ext 的 ref_msg_idx）</summary>
    public string RefMsgIdx { get; init; } = "";

    /// <summary>展示/入库文本：纯图片消息用 [图片] 占位、纯引用用 [引用消息] 占位，避免空内容进历史与上下文</summary>
    public string DisplayContent => !string.IsNullOrWhiteSpace(Content)
        ? Content
        : ImageUrls.Count > 0 ? "[图片]"
        : !string.IsNullOrWhiteSpace(QuotedContent) ? "[引用消息]"
        : "";

    /// <summary>是否 @ 了机器人（GROUP_AT_MESSAGE_CREATE 事件或全量消息中含 @ 标记）</summary>
    public bool IsAtRobot { get; init; }

    /// <summary>是否来自管理员</summary>
    public bool IsAdmin { get; init; }

    /// <summary>收到时间（本地 UTC）</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>是否全量消息事件（GROUP_MESSAGE_CREATE，非 @ 推送）</summary>
    public bool IsFullMessage { get; init; }

    /// <summary>是否机器人自己的消息（全量模式回显，author.bot=true）</summary>
    public bool IsFromBot { get; init; }

    /// <summary>
    /// 是否私聊（C2C）。私聊时 GroupOpenId 存的是会话键 {实例Id}:p{用户号}；
    /// 私聊消息一律视为对机器人发言（IsAtRobot=true），且不参与随机互动与暖群调度。
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// 是否跳过统计/历史入库/随机互动等副作用。
    /// 开启「接收所有消息」后，@ 消息会同时推送 GROUP_AT_MESSAGE_CREATE 与 GROUP_MESSAGE_CREATE
    /// （同 msg_id）。当全量事件先到、@ 事件后到时，全量事件已完成统计/历史/随机互动，
    /// @ 事件以该标记补执行：输入风控 → 指令 → 被动触发 → 工作流（避免重复计数与重复历史）。
    /// </summary>
    public bool SkipSideEffects { get; init; }
}

/// <summary>
/// 一次触发的工作流上下文：组装 Block、执行 ReAct、发送共用
/// </summary>
public class TriggerContext
{
    /// <summary>触发该工作流的机器人实例 Id（决定回复回程）</summary>
    public string BotId { get; init; } = "";

    /// <summary>群键（内部格式 {实例Id}:{原始群号}；私聊为 {实例Id}:p{用户号}）</summary>
    public required string GroupOpenId { get; init; }

    /// <summary>是否私聊会话（决定回复走私聊发送接口）</summary>
    public bool IsPrivate { get; init; }

    /// <summary>触发类型</summary>
    public required TriggerType Type { get; init; }

    /// <summary>触发原因描述（进 Block F，如"被用户 @ 提问"、"群内 1 分钟 12 条消息"）</summary>
    public required string Reason { get; init; }

    /// <summary>触发者 OpenID（被动触发时有值；暖群为空）</summary>
    public string? SenderOpenId { get; init; }

    /// <summary>触发者画像召回（L2，进 Block F，下轮丢弃）</summary>
    public string? RecalledProfile { get; init; }

    /// <summary>被回复/引用的消息内容（回复触发时用于理解上下文）</summary>
    public string? QuotedContent { get; init; }

    /// <summary>暖群时决策 Prompt 额外输入（话题热度等）</summary>
    public string? WarmupHint { get; init; }

    /// <summary>是否允许模型调用画像更新工具（仅主动暖群允许）</summary>
    public bool AllowProfileUpdate { get; init; }

    /// <summary>触发消息附带的图片 URL（被动/插嘴带图时非空；暖群为空），由组装器下载后内联进多模态请求</summary>
    public List<string> ImageUrls { get; init; } = [];

    /// <summary>触发时间（本地 UTC）</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
