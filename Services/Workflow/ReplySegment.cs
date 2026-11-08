using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Llm;
using RainBot.Services.Safety;

namespace RainBot.Services.Workflow;

/// <summary>
/// 分步答复的共享常量与解析：模型输出分隔符自决，系统做硬上限兜底。
///
/// 权威性设计（回答「人设写了只能回一次会不会覆盖」）：
/// 1. <see cref="CapabilitySection"/> 是静态文本，固定拼在 Block A 最末尾，
///    排在人设正文与 prompt.json 铁律之后，并显式声明冲突时以本节为准；
/// 2. 它不随 <see cref="MultiReplyConfig"/> 变化，因此配置热改不会破坏 DeepSeek 前缀缓存；
/// 3. 配置 Enabled=false 时由 <see cref="ReplySegmentPlanner"/> 直接剥离分隔符强制单段，
///    人设措辞不参与开关判定 —— 人设只影响模型「是否选择」输出分隔符（软影响），
///    不能禁用或启用系统能力（硬开关）。
/// </summary>
public static class ReplySegment
{
    /// <summary>模型输出的段分隔符（提示词与解析共用同一常量）</summary>
    public const string Separator = "<seg/>";

    /// <summary>兼容模型写出的变体：大小写、空格、可选闭合斜杠、方括号与中文标记</summary>
    private static readonly Regex SeparatorPattern = new(
        @"(?i)(?:<\s*seg\s*/?\s*>|\[\s*seg\s*\]|【\s*分段\s*】|\|\|\|)",
        RegexOptions.Compiled);

    /// <summary>
    /// Block A 末尾的系统能力段。静态常量：开关状态不写进来，避免 A 块随配置热改而失效缓存。
    /// </summary>
    public const string CapabilitySection =
        "【系统能力 - 与上文人设/铁律冲突时以本节为准】\n" +
        "1. 一次触发可以拆成多条消息发出。需要分几次说、或要多次搜索时，在要分开的位置输出分隔符 <seg/>，系统会按顺序逐条发送。\n" +
        "2. 用不用分隔符由你自己判断：日常闲聊一条说完就不分段；确实话多、想先给结论再补充、或需要边查边说时才分。系统可能因配置关闭或达到上限把多段合并成一条，内容本身照常说完整。\n" +
        "3. 每段仍是口语短句，别写「首先/总之」这类分点总结，也别为了显得话多而连发。\n" +
        "4. 上文若写有「只回一句」「同一话题不连续发言」「最多两行」等，那是单条消息的语气要求，不是对本能力的禁用；与本节冲突时以本节为准。彻底关闭多段由管理员在配置里操作，不需要你自行禁用。\n" +
        "5. 复杂或需要反复查证的问题，可调用 delegate_research 派子代理去查，它会用 web_search / open_page 独立调研后给你摘要；你只负责把结论用人话说给群友。";

    /// <summary>按分隔符拆段；配置关闭时用 <see cref="Strip"/> 合并而非本方法。</summary>
    public static List<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return SeparatorPattern.Split(text)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    /// <summary>剥离分隔符并合并为一段（关闭多段回复时使用，保留内容不丢）。</summary>
    public static string Strip(string? text) => SeparatorPattern.Replace(text ?? "", "\n").Trim();
}

/// <summary>
/// 把一次 ReAct 结果规划成一组待发送消息。纯逻辑，便于单测。
/// 逐段过输出风控（openid / 注入 / 兜底话术 / 单段行数字符上限），
/// 再施加系统硬上限（段数、总字符），最后只把收口段回流模型历史。
/// </summary>
public static class ReplySegmentPlanner
{
    public sealed record Plan(
        IReadOnlyList<string> Segments,
        string HistoryText,
        bool MultiSegment,
        int DroppedChars);

    /// <summary>暖群与随机插嘴强制单段：二者有空哨兵/防刷屏语义，连发会破坏自然度。</summary>
    private static bool ForceSingle(TriggerType type, MultiReplyConfig cfg)
        => !cfg.Enabled || type is TriggerType.Warmup or TriggerType.RandomChat;

    public static Plan Build(ReActResult result, TriggerType type, MultiReplyConfig cfg, OutputFilter filter, ILogger logger)
    {
        // 1. 单段路径：剥离分隔符、忽略工具轮中间发言，与原行为完全一致。
        if (ForceSingle(type, cfg))
        {
            string merged = ReplySegment.Strip(result.Text);
            string? safe = filter.Filter(merged);
            return safe == null
                ? new Plan([], "", false, 0)
                : new Plan([safe], safe, false, 0);
        }

        // 最终文本为空（无内容静默 / LLM 失败）时不发中间发言，避免只留半截「我去查一下」
        if (string.IsNullOrWhiteSpace(result.Text)) return new Plan([], "", false, 0);

        // 2. 中间发言（工具轮 content）+ 收口文本的分段结果，逐段过风控。
        List<string> raw = [.. result.Interim, .. ReplySegment.Split(result.Text)];
        List<string> filtered = [];
        foreach (string part in raw)
        {
            string? safe = filter.Filter(ReplySegment.Strip(part));
            if (safe != null) filtered.Add(safe);
        }
        if (filtered.Count == 0) return new Plan([], "", false, 0);

        // 3. 段数硬上限：超出的段合并进最后一段（不静默丢掉前半部分内容）。
        int maxSegments = Math.Max(1, cfg.MaxSegments);
        if (filtered.Count > maxSegments)
        {
            List<string> head = filtered.Take(maxSegments - 1).ToList();
            string tail = string.Join("\n", filtered.Skip(maxSegments - 1));
            head.Add(filter.Filter(tail) ?? tail);
            filtered = head;
            logger.LogInformation("【分步答复】段数超过上限 {Max}，尾部已合并为一段", maxSegments);
        }

        // 4. 总字符硬上限：逐段累计，超出部分截断并在日志中记录被丢弃的字符数。
        int maxTotal = Math.Clamp(cfg.MaxTotalChars, 1, 20000);
        List<string> budgeted = [];
        int used = 0;
        int dropped = 0;
        foreach (string part in filtered)
        {
            int remain = maxTotal - used;
            if (remain <= 0)
            {
                dropped += part.Length;
                continue;
            }
            if (part.Length > remain)
            {
                // 预留一个字符给省略号，保证所有段合计严格不超过 maxTotal
                string clipped = remain > 1 ? part[..(remain - 1)] + "…" : "…";
                dropped += part.Length - clipped.Length;
                string? safe = filter.Filter(clipped);
                budgeted.Add(safe ?? clipped);
                used = maxTotal;
                continue;
            }
            budgeted.Add(part);
            used += part.Length;
        }
        if (dropped > 0) logger.LogInformation("【分步答复】总字数超过上限 {Max}，截断丢弃 {Dropped} 字符", maxTotal, dropped);
        if (budgeted.Count == 0) return new Plan([], "", false, dropped);

        // 5. 只有收口段回流模型历史，避免自己的多段回显污染 Block E。
        return new Plan(budgeted, budgeted[^1], budgeted.Count > 1, dropped);
    }
}
