namespace RainBot.Services.Fun;

/// <summary>
/// 随机反驳不词表（移植自原版 RainBOT SayNo 系统，内置合理默认值）。
/// 关键词表与词汇表语义：
/// - 关键词表：Trigger（不/没/是/别）、TriggerBeforeNo（太）、IgnoreTriggerAfterNo（"不"后跟这些词不反驳）、
///   TriggerAfterYes（"是"后跟这些词才反驳）、WillNotSayNo（"别"后跟这些词不反驳）
/// - 词汇表：反驳"不/没/是/别/要/想"及特殊反驳（太X了），{0} 为消息中关键词后的第一个字
/// </summary>
public static class SayNoWords
{
    /// <summary>主触发词</summary>
    public static readonly string[] Trigger = ["不", "没", "是", "别"];

    /// <summary>"不" 后出现这些词不反驳（不要/不能/不是/不会/不想/不该）</summary>
    public static readonly string[] IgnoreTriggerAfterNo = ["要", "能", "是", "会", "想", "该", "应该"];

    /// <summary>"是" 后出现这些词才反驳</summary>
    public static readonly string[] TriggerAfterYes = ["吗", "吧", "嘛", "啊", "么"];

    /// <summary>"别" 后出现这些词不反驳</summary>
    public static readonly string[] WillNotSayNo = ["别", "不要"];

    /// <summary>反驳"不X"</summary>
    public static readonly string[] ReplyNoWords = ["不{0}", "就不{0}", "{0}你个头", "说不{0}也没用"];

    /// <summary>反驳"没X"</summary>
    public static readonly string[] SayDontHaveWords = ["有{0}啊", "其实有{0}", "才没有{0}"];

    /// <summary>反驳"是"（配合 TriggerAfterYes）</summary>
    public static readonly string[] SayNotYesWords = ["才不是呢", "是吗？我不信", "对对对，你说的都对"];

    /// <summary>反驳"别X"</summary>
    public static readonly string[] SayDontWords = ["就要{0}", "我偏要{0}", "{0}？我偏不"];

    /// <summary>反驳"要"</summary>
    public static readonly string[] SayWantWords = ["才不要", "不想", "要什么要"];

    /// <summary>反驳"想"</summary>
    public static readonly string[] SayThinkWords = ["才没想", "想什么呢", "不想"];

    /// <summary>特殊反驳"太X了" → 前缀 + X</summary>
    public static readonly string[] SaySpecialNoWords = ["不", "才不", "就"];
}
