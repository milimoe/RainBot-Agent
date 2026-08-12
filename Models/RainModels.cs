namespace RainBot.Models;

/// <summary>触发类型</summary>
public enum TriggerType
{
    /// <summary>被动触发：@ 机器人 / 回复机器人</summary>
    Passive,

    /// <summary>主动暖群：密度唤醒或沉默唤醒</summary>
    Warmup
}

/// <summary>
/// 统一后的入站消息（协议层解析 → 去重 → 进入处理队列）
/// </summary>
public class IncomingMessage
{
    /// <summary>消息 ID（用于去重）</summary>
    public required string MsgId { get; init; }

    /// <summary>消息序号（QQ 推送字段）</summary>
    public long MsgSeq { get; init; }

    /// <summary>群 OpenID（群消息必有）</summary>
    public required string GroupOpenId { get; init; }

    /// <summary>发送者 OpenID</summary>
    public required string SenderOpenId { get; init; }

    /// <summary>消息内容（含 <@!xxx> 等富文本标签）</summary>
    public required string Content { get; init; }

    /// <summary>是否 @ 了机器人（GROUP_AT_MESSAGE_CREATE 事件或全量消息中含 @ 标记）</summary>
    public bool IsAtRobot { get; init; }

    /// <summary>是否来自管理员</summary>
    public bool IsAdmin { get; init; }

    /// <summary>收到时间（本地 UTC）</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>是否全量消息事件（GROUP_MESSAGE_CREATE，非 @ 推送）</summary>
    public bool IsFullMessage { get; init; }
}

/// <summary>
/// 一次触发的工作流上下文：组装 Block、执行 ReAct、发送共用
/// </summary>
public class TriggerContext
{
    /// <summary>群 OpenID</summary>
    public required string GroupOpenId { get; init; }

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

    /// <summary>触发时间（本地 UTC）</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
