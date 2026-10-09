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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workflowLocks = new(StringComparer.Ordinal);

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
        SemaphoreSlim workflowLock = _workflowLocks.GetOrAdd(ctx.GroupOpenId, _ => new SemaphoreSlim(1, 1));
        await workflowLock.WaitAsync(ct);
        try
        {
            int maxAge = _config.Config.Trigger.BacklogMaxAgeSeconds;
            if (ctx.Type != TriggerType.Warmup && maxAge > 0 &&
                DateTimeOffset.UtcNow - ctx.CreatedAt > TimeSpan.FromSeconds(maxAge))
                return false;
            // 1. 组装上下文（Block A-F，前缀稳定）
            ComposeResult compose = await _blockComposer.BuildAsync(ctx);

            // 2. 水位治理：蒸馏/重置后需重新组装一次（最多一次，避免死循环）
            WatermarkAction action = await _watermarkManager.EnforceAsync(compose);
            if (action is WatermarkAction.Distilled or WatermarkAction.Reset)
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

            // 4. 输出风控
            string? text = _outputFilter.Filter(result.Text);
            if (text == null)
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
                // 思维显示（需 DebugMode + DebugShowReasoning）：思维链用 ``` 包起来，与回复一起发出
                string content = ComposeDebugContent(text, result.Reasoning);
                // 调试模式：输出末尾追加一行「x tokens, x tools」统计（输入+输出 token、工具调用次数）；
                // Markdown 回复模式下使用块引用格式「> x tokens, x tools」
                if (_config.Config.DebugMode)
                {
                    int tokens = (result.Usage?.PromptTokens ?? 0) + (result.Usage?.CompletionTokens ?? 0);
                    string footer = _config.Config.MarkdownReply
                        ? $"> {tokens} tokens, {result.ToolCallCount} tools"
                        : $"{tokens} tokens, {result.ToolCallCount} tools";
                    content = $"{content}\n{footer}";
                }
                await _sendQueue.EnqueueAsync(new SendTask
                {
                    BotId = ctx.BotId,
                    GroupOpenId = ctx.GroupOpenId,
                    Content = content,
                    IsPrivate = ctx.IsPrivate,
                    AtUserId = toolCtx.RequestedAtUserId,
                    MsgId = replyMsgId
                });
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("{Label} {Group} 触发「{Type}」已回复：{Text}", ctx.IsPrivate ? "私聊" : "群", ctx.GroupOpenId, ctx.Type, text);
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
