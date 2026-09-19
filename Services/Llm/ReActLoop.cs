using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Tools;

namespace RainBot.Services.Llm;

/// <summary>
/// ReAct 循环：模型 → 工具调用 → 执行 → 结果追加尾部 → 继续，直至无工具调用或达轮次上限。
/// 工具结果只追加在消息尾部（不插入前缀），维持缓存命中。
/// 轮次上限防死循环；触顶时补一次 tool_choice="none" 的收口请求，让模型基于已有工具结果作答；
/// 异常兜底为一句符合人设的短句。
/// </summary>
public class ReActLoop(RuntimeConfig config, DeepSeekClient deepSeekClient, ToolRegistry toolRegistry, ILogger<ReActLoop> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly DeepSeekClient _deepSeekClient = deepSeekClient;
    private readonly ToolRegistry _toolRegistry = toolRegistry;
    private readonly ILogger<ReActLoop> _logger = logger;

    /// <summary>执行一次完整工作流，返回最终回复文本</summary>
    public async Task<ReActResult> RunAsync(IReadOnlyList<ChatMessage> messages, ToolExecutionContext context, string fallback = "嗯……我暂时想不出怎么接这个话题，等我缓缓 🌧️", CancellationToken ct = default)
    {
        LlmConfig llm = _config.Config.Llm;
        int maxRounds = Math.Max(1, llm.MaxToolRounds);
        List<ChatMessage> working = [.. messages];
        List<ToolDef> tools = _toolRegistry.GetToolDefs();
        Usage? lastUsage = null;
        string? lastText = null;
        int toolCallCount = 0;
        bool awaitingFinalAnswer = false; // true = 上一轮还在调工具，结果尚未被消化

        // 轮次语义：round 0..maxRounds-1 为「工具轮」（最多 maxRounds 次工具调用决策），
        // 触顶后另有一次收口轮（不计入工具轮次）。
        for (int round = 0; round < maxRounds; round++)
        {
            // 温度两档：首轮尚未产生工具结果时通常是直接答复（多数会话不发工具调用），
            // 用主温度保人设语气；一旦进入工具链中段（需消化工具结果、可能再调用），
            // 降到 ToolTemperature 保证工具选择与参数生成稳定。收口轮回到主温度。
            double temperature = awaitingFinalAnswer ? llm.ToolTemperature : llm.Temperature;

            ChatResult result;
            try
            {
                result = await _deepSeekClient.ChatAsync(working, tools, maxTokens: llm.ToolRoundMaxTokens, temperature: temperature, ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM 调用失败（round={Round}）", round);
                return new ReActResult { Text = fallback, Usage = lastUsage, ToolCallCount = toolCallCount, Failed = true };
            }

            lastUsage = result.Usage;
            lastText = result.Message.Content;

            if (result.Message.ToolCalls is { Count: > 0 })
            {
                toolCallCount += result.Message.ToolCalls.Count;
                // 追加助手工具调用消息
                working.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = result.Message.Content,
                    ToolCalls = result.Message.ToolCalls
                });

                foreach (ChatToolCall call in result.Message.ToolCalls)
                {
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("工具调用：{Name}({Args})", call.Function.Name, call.Function.Arguments);
                    string toolResult = await _toolRegistry.ExecuteAsync(call.Function.Name, call.Function.Arguments, context);
                    // 工具结果追加尾部（tool 角色），前缀不变
                    working.Add(ChatMessage.ToolResult(call.Id, Truncate(toolResult, 1500)));
                }
                awaitingFinalAnswer = true;
                continue;
            }

            // 无工具调用：正常输出
            awaitingFinalAnswer = false;
            break;
        }

        // 触顶收口：工具轮用尽但最后一批工具结果还没被组织成回复。
        // tools 仍原样传入、只把 tool_choice 设为 "none"——保持输入 token 序列与前面各轮一致，
        // 不因「去掉 tools 字段」破坏前缀缓存。
        if (awaitingFinalAnswer)
        {
            working.Add(ChatMessage.User($"[系统] 工具调用轮次已用尽（已调用 {toolCallCount} 次）。请直接基于以上工具结果给出最终回复，不要再请求调用工具；信息不足就照实简短说明。"));
            try
            {
                ChatResult closing = await _deepSeekClient.ChatAsync(
                    working, tools, maxTokens: ClosingMaxTokens(llm), temperature: llm.Temperature, toolChoice: "none", ct: ct);
                lastUsage = closing.Usage;
                if (!string.IsNullOrWhiteSpace(closing.Message.Content))
                {
                    lastText = closing.Message.Content;
                }
                else
                {
                    _logger.LogWarning("收口请求未返回文本（tool_calls 已被禁用，模型输出异常）");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收口请求失败（已执行 {Count} 次工具调用，丢弃最后一轮工具结果）", toolCallCount);
            }
        }

        if (string.IsNullOrWhiteSpace(lastText))
        {
            _logger.LogWarning("ReAct 循环结束后无文本输出（rounds={Max}），使用兜底文案", maxRounds);
            lastText = fallback;
        }

        return new ReActResult { Text = lastText, Usage = lastUsage, ToolCallCount = toolCallCount, Failed = false };
    }

    /// <summary>收口轮 max_tokens：按输出字符上限推导（中文约 0.8~1 token/字，留一倍余量）</summary>
    private static int ClosingMaxTokens(LlmConfig llm) => Math.Clamp(llm.MaxOutputChars * 2, 128, 1024);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

public class ReActResult
{
    public required string Text { get; init; }
    public Usage? Usage { get; init; }

    /// <summary>本轮工作流实际执行的工具调用次数（0 = 未调用工具）</summary>
    public int ToolCallCount { get; init; }
    public bool Failed { get; init; }
}
