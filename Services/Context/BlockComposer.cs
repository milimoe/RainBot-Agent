using System.Text;
using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Llm;
using RainBot.Services.Persona;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.Tools;

namespace RainBot.Services.Context;

/// <summary>
/// 上下文组装器（核心成本策略）：
/// 顺序固定 System → Tools → Group Profile → User Anchors → History → Current Query；
/// 可变内容只追加尾部；历史从头部整条丢弃；序列化无时间戳/随机数污染前缀。
/// 消息结构：messages[0]=system(Block A)，messages[1]=user(B+C+D 静态前缀)，
/// messages[2]=user(Block E 历史)，messages[3]=user(Block F 当前)。
/// </summary>
public class BlockComposer(
    PersonaLoader personaLoader,
    ToolRegistry toolRegistry,
    GroupStateManager states,
    AnchorManager anchorManager,
    HistoryStore historyStore,
    RuntimeConfig config,
    ILogger<BlockComposer> logger)
{
    private readonly PersonaLoader _personaLoader = personaLoader;
    private readonly ToolRegistry _toolRegistry = toolRegistry;
    private readonly GroupStateManager _states = states;
    private readonly AnchorManager _anchorManager = anchorManager;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<BlockComposer> _logger = logger;

    /// <summary>
    /// Block B 抬头：工具清单是「裸 JSON 数组」，正文里出现结构化 JSON 容易被模型模仿成
    /// 「在回复里手打 web_search(query="…")」而不走 tool_calls 通道（temperature 0.9 下风险更高）。
    /// 该抬头为常量 → 不破坏前缀稳定性。
    /// </summary>
    private const string ToolListHeader =
        "[工具能力清单]\n" +
        "下面这段 JSON 只说明你有哪些外部能力、各自需要什么参数，供你判断何时需要借助外部信息。\n" +
        "需要调用时，必须走结构化工具调用（function call）通道；严禁在回复正文里手写、复述或以文本形式模拟调用语法。\n";

    /// <summary>组装完整上下文</summary>
    public async Task<ComposeResult> BuildAsync(TriggerContext ctx)
    {
        var cfg = _config.Config.Context;
        int anchorCount = cfg.MaxAnchorCount;

        // 1. 固定块 A-D（低/中缓存区）
        string blockA = _personaLoader.GetSystemPrompt();
        string blockB = ToolListHeader + _toolRegistry.GetSchemaJson();
        GroupProfile profile = await _states.GetProfileAsync(ctx.GroupOpenId);
        string blockC = BuildGroupProfileBlock(profile);
        string blockD = await _anchorManager.GetAnchorsTextAsync(ctx.GroupOpenId, anchorCount);

        int fixedTokens = TokenEstimator.Estimate(blockA) + TokenEstimator.Estimate(blockB) + TokenEstimator.Estimate(blockC) + TokenEstimator.Estimate(blockD);

        // 2. Block E：历史预算 = min(历史预算, 水位 - 固定块 - 当前块 - 余量)
        string blockF = BuildCurrentBlock(ctx);
        int fTokens = TokenEstimator.Estimate(blockF);
        int margin = 2000;
        int eBudget = Math.Min(
            cfg.HistoryAssembleCapTokens,
            Math.Max(1000, cfg.WatermarkTokens - fixedTokens - fTokens - margin));

        List<HistoryEntry> history = _historyStore.GetRecent(ctx.GroupOpenId, eBudget, out int dropped);
        string blockE = BuildHistoryBlock(history);
        int eTokens = TokenEstimator.Estimate(blockE);

        // 3. 压缩前防御第二步：固定块异常巨大（几乎不可能）导致超水位时，缩减锚点
        if (fixedTokens + eTokens + fTokens > cfg.WatermarkTokens && anchorCount > cfg.MinAnchorCount)
        {
            int shrunkAnchorCount = cfg.MinAnchorCount;
            string shrunkD = await _anchorManager.GetAnchorsTextAsync(ctx.GroupOpenId, shrunkAnchorCount, forceRefresh: true);
            if (TokenEstimator.Estimate(shrunkD) < TokenEstimator.Estimate(blockD))
            {
                _logger.LogWarning("群 {Group} 固定块超水位，锚点已缩减至 {Count}", ctx.GroupOpenId, shrunkAnchorCount);
                blockD = shrunkD;
                fixedTokens = TokenEstimator.Estimate(blockA) + TokenEstimator.Estimate(blockB) + TokenEstimator.Estimate(blockC) + TokenEstimator.Estimate(blockD);
            }
        }

        List<ChatMessage> messages =
        [
            ChatMessage.System(blockA),
            ChatMessage.User(blockB + "\n\n" + blockC + "\n\n" + blockD),
            ChatMessage.User(blockE),
            ChatMessage.User(blockF)
        ];

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("上下文组装：A={A}B={B}C={C}D={D}E={E}(丢{Drop}条)F={F}，总计≈{Total} tokens",
                TokenEstimator.Estimate(blockA), TokenEstimator.Estimate(blockB), TokenEstimator.Estimate(blockC),
                TokenEstimator.Estimate(blockD), eTokens, dropped, fTokens,
                TokenEstimator.Estimate(blockA) + TokenEstimator.Estimate(blockB) + TokenEstimator.Estimate(blockC) + TokenEstimator.Estimate(blockD) + eTokens + fTokens);
        }

        return new ComposeResult
        {
            Messages = messages,
            HistoryCount = history.Count,
            HistoryDropped = dropped,
            EstimatedTokens = fixedTokens + eTokens + fTokens,
            HistoryTokens = eTokens,
            FixedTokens = fixedTokens,
            GroupOpenId = ctx.GroupOpenId
        };
    }

    private static string BuildGroupProfileBlock(GroupProfile profile)
    {
        StringBuilder sb = new("[群画像]\n");
        sb.AppendLine($"群类型：{(string.IsNullOrWhiteSpace(profile.Type) ? "未知" : profile.Type)}");
        sb.AppendLine($"群禁忌：{(string.IsNullOrWhiteSpace(profile.Taboos) ? "无" : profile.Taboos)}");
        if (!string.IsNullOrWhiteSpace(profile.Summary))
        {
            sb.AppendLine($"群记忆：{profile.Summary}");
        }
        return sb.ToString();
    }

    private static string BuildHistoryBlock(List<HistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return "[群消息历史]\n（暂无）";
        }
        StringBuilder sb = new("[群消息历史]\n");
        foreach (HistoryEntry entry in history)
        {
            string time = entry.Time.ToLocalTime().ToString("HH:mm");
            string content = CleanContent(entry.Content);
            if (content.Length > 200) content = content[..200] + "…";
            sb.AppendLine($"[{time}] u{AnchorManager.ShortId(entry.UserOpenId)}: {content}");
        }
        return sb.ToString();
    }

    private string BuildCurrentBlock(TriggerContext ctx)
    {
        StringBuilder sb = new("[当前]\n");
        sb.AppendLine($"触发原因：{ctx.Reason}");
        if (!string.IsNullOrWhiteSpace(ctx.RecalledProfile))
        {
            sb.AppendLine($"相关群友画像：{ctx.RecalledProfile}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.WarmupHint))
        {
            sb.AppendLine($"暖群提示：{ctx.WarmupHint}");
        }
        if (ctx.Type == TriggerType.Warmup)
        {
            // 暖群 = 主动破冰：明确告知 LLM 现在没人说话、需要它开口，
            // 避免模型把"静默触发"误解为要回复某人而空转/拒绝。
            sb.AppendLine();
            sb.AppendLine("[任务] 现在是主动暖场时间：群里安静了一阵子，请你自然地开口说话，而不是回复某个具体的人。可以结合上面的群画像/最近话题找个轻松切入点，或聊聊天气、日常、趣事，语气保持人设。直接输出你要说的那句话即可。");
        }
        return sb.ToString();
    }

    /// <summary>清理消息中的富文本标签（@ 标签替换为可读形式）</summary>
    private static string CleanContent(string content)
    {
        // <@openid>（新格式）或 <@!openid>（历史格式）→ @用户（保留可读性）
        return Regex.Replace(content, @"<@!?[^>]+>", m => "@用户")
            .Replace("<reply>", "")
            .Replace("</reply>", "")
            .Replace("<break/>", "\n")
            .Trim();
    }
}

/// <summary>组装结果</summary>
public class ComposeResult
{
    public required List<ChatMessage> Messages { get; init; }
    public required string GroupOpenId { get; init; }
    public int HistoryCount { get; init; }
    public int HistoryDropped { get; init; }
    public int EstimatedTokens { get; init; }
    public int HistoryTokens { get; init; }
    public int FixedTokens { get; init; }
}
