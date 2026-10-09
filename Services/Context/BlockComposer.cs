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
    VisionImageLoader visionLoader,
    RuntimeConfig config,
    UserIdentityResolver identities,
    Database database,
    ILogger<BlockComposer> logger)
{
    private readonly PersonaLoader _personaLoader = personaLoader;
    private readonly ToolRegistry _toolRegistry = toolRegistry;
    private readonly GroupStateManager _states = states;
    private readonly AnchorManager _anchorManager = anchorManager;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly VisionImageLoader _visionLoader = visionLoader;
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
        string blockA = _personaLoader.GetSystemPrompt(ctx.BotId);
        string blockB = ToolListHeader + _toolRegistry.GetSchemaJson();
        GroupProfile profile = await _states.GetProfileAsync(ctx.GroupOpenId);
        string blockC = BuildGroupProfileBlock(profile, await database.GetRecentDistillSummariesAsync(ctx.GroupOpenId));
        string blockD = await _anchorManager.GetAnchorsTextAsync(ctx.GroupOpenId, anchorCount);

        int fixedTokens = TokenEstimator.Estimate(blockA) + TokenEstimator.Estimate(blockB) + TokenEstimator.Estimate(blockC) + TokenEstimator.Estimate(blockD);

        // 2. Block E：历史预算 = min(历史预算, 水位 - 固定块 - 当前块 - 余量)
        string? senderNickname = ctx.SenderNickname;
        if (!string.IsNullOrWhiteSpace(ctx.SenderOpenId))
            senderNickname = await identities.GetNicknameAsync(ctx.GroupOpenId, ctx.SenderOpenId) ?? senderNickname;
        string blockF = BuildCurrentBlock(ctx, senderNickname);
        int fTokens = TokenEstimator.Estimate(blockF);
        int margin = 2000;
        int eBudget = Math.Min(
            cfg.HistoryAssembleCapTokens,
            Math.Max(1000, cfg.WatermarkTokens - fixedTokens - fTokens - margin));

        List<HistoryEntry> history = _historyStore.GetRecent(ctx.GroupOpenId, eBudget, out int dropped);
        string blockE = await BuildHistoryBlockAsync(ctx.GroupOpenId, history);
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
            await BuildCurrentMessageAsync(blockF, ctx)
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
            BlockTokens = new Dictionary<string, int>
            {
                ["A 人设与规则"] = TokenEstimator.Estimate(blockA),
                ["B 工具能力"] = TokenEstimator.Estimate(blockB),
                ["C 群画像与记忆"] = TokenEstimator.Estimate(blockC),
                ["D 群友锚点"] = TokenEstimator.Estimate(blockD),
                ["E 历史消息"] = eTokens,
                ["F 当前触发"] = fTokens
            },
            HistoryCount = history.Count,
            HistoryDropped = dropped,
            EstimatedTokens = fixedTokens + eTokens + fTokens,
            HistoryTokens = eTokens,
            FixedTokens = fixedTokens,
            GroupOpenId = ctx.GroupOpenId
        };
    }

    /// <summary>单条消息最多内联的图片数（防刷图把请求体撑爆）</summary>
    private const int MaxInlineImages = 4;

    /// <summary>
    /// 组装 Block F 当前消息：带图且开启视觉时，把图片下载并 base64 内联为多模态块
    /// （文本块 + 图片块，DeepSeek 视觉格式）。图片只在本轮请求内联、不落 history；
    /// 全部图片加载失败则退化为纯文本（正文含 [图片] 占位）。
    /// </summary>
    private async Task<ChatMessage> BuildCurrentMessageAsync(string blockF, TriggerContext ctx)
    {
        if (ctx.ImageUrls.Count == 0 || !_config.Config.Llm.EnableVision)
        {
            return ChatMessage.User(blockF);
        }
        List<ContentPart> parts = [ContentPart.TextPart(blockF)];
        foreach (string url in ctx.ImageUrls.Take(MaxInlineImages))
        {
            string? dataUrl = await _visionLoader.ToDataUrlAsync(url);
            if (dataUrl != null)
            {
                parts.Add(ContentPart.ImagePart(dataUrl));
            }
        }
        if (ctx.ImageUrls.Count > MaxInlineImages)
        {
            _logger.LogInformation("群 {Group} 消息含 {Total} 张图片，本轮只内联前 {Max} 张", ctx.GroupOpenId, ctx.ImageUrls.Count, MaxInlineImages);
        }
        if (parts.Count == 1)
        {
            return ChatMessage.User(blockF); // 全部加载失败 → 纯文本
        }
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("群 {Group} 本轮内联图片 {Count} 张（触发：{Type}）", ctx.GroupOpenId, parts.Count - 1, ctx.Type);
        }
        return ChatMessage.UserWithParts(parts);
    }

    private static string BuildGroupProfileBlock(GroupProfile profile, IReadOnlyList<string> summaries)
    {
        StringBuilder sb = new("[群画像]\n");
        sb.AppendLine($"群类型：{(string.IsNullOrWhiteSpace(profile.Type) ? "未知" : profile.Type)}");
        sb.AppendLine($"群禁忌：{(string.IsNullOrWhiteSpace(profile.Taboos) ? "无" : profile.Taboos)}");
        if (!string.IsNullOrWhiteSpace(profile.Summary))
        {
            sb.AppendLine($"群记忆：{profile.Summary}");
        }
        if (summaries.Count > 0)
            sb.AppendLine("历史蒸馏记忆：\n" + string.Join("\n", summaries));
        return sb.ToString();
    }

    private async Task<string> BuildHistoryBlockAsync(string groupOpenId, List<HistoryEntry> history)
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
            string? nickname = await identities.GetNicknameAsync(groupOpenId, entry.UserOpenId) ?? entry.Nickname;
            string speaker = string.IsNullOrWhiteSpace(nickname) ? UserIdentityResolver.ShortId(entry.UserOpenId) : $"{nickname}({UserIdentityResolver.ShortId(entry.UserOpenId)})";
            sb.AppendLine($"[{time}] {speaker}: {content}");
        }
        return sb.ToString();
    }

    private string BuildCurrentBlock(TriggerContext ctx, string? senderNickname)
    {
        StringBuilder sb = new("[当前]\n");
        sb.AppendLine($"触发原因：{ctx.Reason}");
        if (!string.IsNullOrWhiteSpace(ctx.SenderOpenId))
        {
            sb.AppendLine($"当前触发者：{senderNickname ?? "群友"}({UserIdentityResolver.ShortId(ctx.SenderOpenId)})");
            sb.AppendLine($"触发者完整标识：{ctx.SenderOpenId}（仅供工具参数，禁止写入回复正文）");
        }
        if (ctx.PendingMessages.Count > 0)
        {
            sb.AppendLine("本批待回应消息（请合并成一次自然回复）：");
            foreach (PendingSpeaker pending in ctx.PendingMessages)
                sb.AppendLine($"{pending.Nickname ?? "群友"}({UserIdentityResolver.ShortId(pending.OpenId)}): {CleanContent(pending.Content)}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.RecalledProfile))
        {
            sb.AppendLine($"相关群友画像：{ctx.RecalledProfile}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.WarmupHint))
        {
            sb.AppendLine($"暖群提示：{ctx.WarmupHint}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.QuotedContent))
        {
            // 用户引用了某条消息提问：把被引用内容显式给出，模型才明白"这个/他说的"指什么
            string quoted = CleanContent(ctx.QuotedContent);
            if (quoted.Length > 600)
            {
                quoted = quoted[..600] + "…";
            }
            sb.AppendLine($"被引用的消息：{quoted}");
        }
        if (ctx.ImageUrls.Count > 0)
        {
            // 图片本体作为多模态块紧跟在本块之后；此处只给文字提示，历史里是 [图片] 占位
            sb.AppendLine($"图片消息：本次附带 {ctx.ImageUrls.Count} 张图片（含引用消息中的图片，内容见下方，请结合图片理解后再回应）。");
        }
        if (ctx.Type == TriggerType.Warmup)
        {
            // 暖群 = 主动破冰：明确告知 LLM 现在没人说话、需要它开口，
            // 避免模型把"静默触发"误解为要回复某人而空转/拒绝。
            sb.AppendLine();
            sb.AppendLine("[任务] 现在是主动暖场时间：群里安静了一阵子，请你自然地开口说话，而不是回复某个具体的人。可以结合上面的群画像/最近话题找个轻松切入点，或聊聊天气、日常、趣事，语气保持人设。直接输出你要说的那句话即可。");
        }
        else if (ctx.Type == TriggerType.RandomChat)
        {
            // 随机插嘴 = 路过搭话：没人 @ 它，必须允许模型选择不接话，
            // 否则概率命中的每条消息都会被硬回。
            // 空内容用固定哨兵 (empty) 表达：模型写「空内容」时常会输出「（空）」等字面文本被发送出去，
            // 固定哨兵由 ReActLoop 归一为空文本 → 走「无内容 → 静默跳过」路径。
            sb.AppendLine();
            sb.AppendLine("[任务] 这次没有人在 @ 你，你只是在旁听群聊时刚好看到这条消息。觉得值得接就自然插一句，像顺手搭话的群友，保持人设，不要自我介绍、不要客套；接不上或没必要回，就只输出固定内容 (empty) 保持安静（系统会静默处理，不会发出任何消息，不要写别的兜底话）。");
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
    public Dictionary<string, int> BlockTokens { get; init; } = [];
    public required List<ChatMessage> Messages { get; init; }
    public required string GroupOpenId { get; init; }
    public int HistoryCount { get; init; }
    public int HistoryDropped { get; init; }
    public int EstimatedTokens { get; init; }
    public int HistoryTokens { get; init; }
    public int FixedTokens { get; init; }
}
