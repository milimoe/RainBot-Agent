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
/// 全局串行（SemaphoreSlim），保证同群/跨群工作流不并发交错。
/// </summary>
public class WorkflowRunner(
    BlockComposer blockComposer,
    WatermarkManager watermarkManager,
    ReActLoop reactLoop,
    OutputFilter outputFilter,
    SendQueue sendQueue,
    CacheMonitor cacheMonitor,
    RuntimeConfig config,
    ILogger<WorkflowRunner> logger)
{
    private readonly BlockComposer _blockComposer = blockComposer;
    private readonly WatermarkManager _watermarkManager = watermarkManager;
    private readonly ReActLoop _reactLoop = reactLoop;
    private readonly OutputFilter _outputFilter = outputFilter;
    private readonly SendQueue _sendQueue = sendQueue;
    private readonly CacheMonitor _cacheMonitor = cacheMonitor;
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<WorkflowRunner> _logger = logger;
    private readonly SemaphoreSlim _workflowLock = new(1, 1);

    /// <summary>
    /// 执行一次触发工作流。
    /// </summary>
    /// <param name="ctx">触发上下文</param>
    /// <param name="replyMsgId">被动回复时引用原消息 ID</param>
    public async Task RunAsync(TriggerContext ctx, string? replyMsgId, CancellationToken ct = default)
    {
        await _workflowLock.WaitAsync(ct);
        try
        {
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
                return;
            }

            // 3. ReAct 循环（工具调用追加尾部，不污染前缀）
            bool isAdmin = ctx.SenderOpenId != null && await _config.IsAdminAsync(ctx.SenderOpenId);
            ToolExecutionContext toolCtx = new()
            {
                GroupOpenId = ctx.GroupOpenId,
                SenderOpenId = ctx.SenderOpenId,
                IsAdmin = isAdmin,
                AllowProfileUpdate = ctx.AllowProfileUpdate
            };
            ReActResult result = await _reactLoop.RunAsync(compose.Messages, toolCtx, ct: ct);

            // 4. 输出风控
            string? text = _outputFilter.Filter(result.Text);
            if (text == null)
            {
                _logger.LogInformation("群 {Group} 输出被风控拦截，不发送", ctx.GroupOpenId);
            }
            else
            {
                string content = text;
                // 调试模式：输出末尾追加一行「x tokens, x tools」统计（输入+输出 token、工具调用次数）；
                // Markdown 回复模式下使用块引用格式「> x tokens, x tools」
                if (_config.Config.DebugMode)
                {
                    int tokens = (result.Usage?.PromptTokens ?? 0) + (result.Usage?.CompletionTokens ?? 0);
                    string footer = _config.Config.MarkdownReply
                        ? $"> {tokens} tokens, {result.ToolCallCount} tools"
                        : $"{tokens} tokens, {result.ToolCallCount} tools";
                    content = $"{text}\n{footer}";
                }
                await _sendQueue.EnqueueAsync(new SendTask
                {
                    GroupOpenId = ctx.GroupOpenId,
                    Content = content,
                    MsgId = replyMsgId
                });
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("群 {Group} 触发「{Type}」已回复：{Text}", ctx.GroupOpenId, ctx.Type, text);
            }

            // 5. 成本统计（缓存命中率监控）
            await _cacheMonitor.RecordAsync(ctx.GroupOpenId, ctx.Type.ToString(), result.Usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用停止中
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作流执行异常（group={Group}, type={Type}）", ctx.GroupOpenId, ctx.Type);
        }
        finally
        {
            _workflowLock.Release();
        }
    }
}
