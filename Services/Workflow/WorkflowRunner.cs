using System.Collections.Concurrent;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Safety;
using RainBot.Services.Trigger;
using RainBot.Services.Tools;

namespace RainBot.Services.Workflow;

/// <summary>
/// 工作流执行器：上下文组装 → 水位治理 → ReAct 循环 → 输出风控 → 发送 → 统计。
/// 按会话串行，同群消息有序，不同群可并行。
/// </summary>
public class WorkflowRunner(
    BlockComposer blockComposer,
    WatermarkManager watermarkManager,
    ReActLoop reactLoop,
    OutputFilter outputFilter,
    SendQueue sendQueue,
    CacheMonitor cacheMonitor,
    RuntimeConfig config,
    Services.Bots.BotInstanceStore bots,
    GroupStateManager states,
    ILogger<WorkflowRunner> logger)
{
    private readonly BlockComposer _blockComposer = blockComposer;
    private readonly WatermarkManager _watermarkManager = watermarkManager;
    private readonly ReActLoop _reactLoop = reactLoop;
    private readonly OutputFilter _outputFilter = outputFilter;
    private readonly SendQueue _sendQueue = sendQueue;
    private readonly CacheMonitor _cacheMonitor = cacheMonitor;
    private readonly RuntimeConfig _config = config;
    private readonly Services.Bots.BotInstanceStore _bots = bots;
    private readonly ILogger<WorkflowRunner> _logger = logger;

    /// <summary>思维显示时思维链的展示字数上限（防单条消息过长，超出截断）</summary>
    private const int MaxReasoningDisplayChars = 1000;

    /// <summary>
    /// 组装调试内容：思维显示开启（DebugMode + DebugShowReasoning）且模型有思维内容时，
    /// 把思维链用 ``` 包起来拼在回复前面；思维链单独过一遍风控（替换 openid、拒绝注入），
    /// 不通过则只发回复本身。
    /// </summary>
    private string ComposeDebugContent(string text, string? reasoning)
    {
        if (!_config.Config.DebugMode || !_config.Config.DebugShowReasoning || string.IsNullOrWhiteSpace(reasoning))
        {
            return text;
        }
        string? safe = _outputFilter.Filter(reasoning, applyLlmLimits: false);
        if (safe == null)
        {
            _logger.LogWarning("思维链未通过输出风控，本次仅发送回复内容");
            return text;
        }
        if (safe.Length > MaxReasoningDisplayChars)
        {
            safe = safe[..MaxReasoningDisplayChars] + "…（已截断）";
        }
        return $"```\n{safe}\n```\n{text}";
    }

    /// <summary>
    /// 执行一次触发工作流。
    /// </summary>
    /// <param name="ctx">触发上下文</param>
    /// <param name="replyMsgId">被动回复时引用原消息 ID</param>
    /// <returns>是否真正入队发送了消息（无内容可说时静默跳过会返回 false）</returns>
    public async Task<bool> RunAsync(TriggerContext ctx, string? replyMsgId, CancellationToken ct = default)
    {
        SemaphoreSlim workflowLock = states.ContextLock(ctx.GroupOpenId);
        await workflowLock.WaitAsync(ct);
        try
        {
            if (_bots.Get(ctx.BotId.Length > 0 ? ctx.BotId : Services.Bots.BotKeys.GetBotId(ctx.GroupOpenId))?.Paused == true
                || ctx.ContextVersion is long version && version != states.GetOrCreate(ctx.GroupOpenId).ContextVersion) return false;
            int maxAge = _config.Config.Trigger.BacklogMaxAgeSeconds;
            if (ctx.Type != TriggerType.Warmup && maxAge > 0 &&
                DateTimeOffset.UtcNow - ctx.CreatedAt > TimeSpan.FromSeconds(maxAge))
                return false;
            // 1. 组装上下文（Block A-F，前缀稳定）
            ComposeResult compose = await _blockComposer.BuildAsync(ctx);

            // 2. 水位治理：蒸馏/重置后需重新组装一次（最多一次，避免死循环）
            WatermarkAction action = await _watermarkManager.EnforceAsync(compose, ct);
            if (action is WatermarkAction.Compacted or WatermarkAction.Distilled or WatermarkAction.Reset)
            {
                compose = await _blockComposer.BuildAsync(ctx);
            }
            else if (action == WatermarkAction.DegradedSkipped)
            {
                _logger.LogInformation("群 {Group} 处于降级期且静默未满，本次工作流跳过", ctx.GroupOpenId);
                return false;
            }

            // 3. ReAct 循环（工具调用追加尾部，不污染前缀）
            bool isAdmin = ctx.SenderOpenId != null && await _bots.IsAdminAsync(ctx.BotId, ctx.SenderOpenId);
            ToolExecutionContext toolCtx = new()
            {
                GroupOpenId = ctx.GroupOpenId,
                IsPrivate = ctx.IsPrivate,
                SenderOpenId = ctx.SenderOpenId,
                IsAdmin = isAdmin,
                AllowProfileUpdate = ctx.AllowProfileUpdate
            };

            // LLM 无内容可说（空输出/调用失败）时一律静默跳过，任何触发类型都不发
            // 「想不出怎么接话题」这类被动兜底话术。
            ReActResult result = await _reactLoop.RunAsync(compose.Messages, toolCtx, ct: ct, compose: compose);

            // 4. 输出风控 + 分段规划：模型用分隔符自决，系统做段数/总字数硬上限兜底。
            //    配置关闭或人设写「只回一次」都不会让配置失效（权威开关在 MultiReply.Enabled）。
            ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(
                result, ctx.Type, _config.Config.MultiReply, _outputFilter, _logger);
            if (plan.Segments.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    // LLM 无有效内容（历史空/话题已冷/LLM 觉得没必要接/失败）→ 保持安静，不发任何兜底话术
                    _logger.LogInformation("群 {Group} {Type}无有效内容（failed={Failed}），静默跳过不发送", ctx.GroupOpenId, ctx.Type, result.Failed);
                }
                else
                {
                    _logger.LogInformation("群 {Group} 输出被风控拦截，不发送", ctx.GroupOpenId);
                }
                return false;
            }
            else
            {
                List<string> contents = [.. plan.Segments];
                int last = contents.Count - 1;
                // 思维显示（需 DebugMode + DebugShowReasoning）与调试统计行只挂在收口段
                contents[last] = ComposeDebugContent(contents[last], result.Reasoning);
                if (_config.Config.DebugMode)
                {
                    int tokens = (result.Usage?.PromptTokens ?? 0) + (result.Usage?.CompletionTokens ?? 0);
                    string footer = _config.Config.MarkdownReply
                        ? $"> {tokens} tokens, {result.ToolCallCount} tools"
                        : $"{tokens} tokens, {result.ToolCallCount} tools";
                    contents[last] = $"{contents[last]}\n{footer}";
                }
                await _sendQueue.EnqueueAsync(new SendTask
                {
                    ContextVersion = states.GetOrCreate(ctx.GroupOpenId).ContextVersion,
                    BotId = ctx.BotId,
                    GroupOpenId = ctx.GroupOpenId,
                    Content = contents[last],
                    // 多段用一条「段组」任务发送，保证段序与整组重试；单段走原路径
                    Segments = plan.MultiSegment ? contents : null,
                    SegmentGapSeconds = plan.MultiSegment ? _config.Config.MultiReply.SegmentGapSeconds : 0,
                    // 只有收口段回流模型历史，避免自己的多段回显污染 Block E
                    ContextContent = plan.HistoryText,
                    IsPrivate = ctx.IsPrivate,
                    AtUserId = toolCtx.RequestedAtUserId,
                    MsgId = replyMsgId
                });
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("{Label} {Group} 触发「{Type}」已回复（{Count} 段）：{Text}", ctx.IsPrivate ? "私聊" : "群", ctx.GroupOpenId, ctx.Type, contents.Count, string.Join(" ⇢ ", plan.Segments));
            }

            // 5. 成本统计（缓存命中率监控）
            await _cacheMonitor.RecordAsync(ctx.GroupOpenId, ctx.Type.ToString(), result.Usage);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用停止中
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作流执行异常（group={Group}, type={Type}）", ctx.GroupOpenId, ctx.Type);
            return false;
        }
        finally
        {
            workflowLock.Release();
        }
    }
}

