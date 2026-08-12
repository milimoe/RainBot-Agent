using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Tools;

namespace RainBot.Services.Llm;

/// <summary>
/// ReAct 循环：模型 → 工具调用 → 执行 → 结果追加尾部 → 继续，直至无工具调用或达轮次上限。
/// 工具结果只追加在消息尾部（不插入前缀），维持缓存命中。
/// 轮次上限防死循环；异常兜底为一句符合人设的短句。
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
        int maxRounds = _config.Config.Llm.MaxToolRounds;
        List<ChatMessage> working = [.. messages];
        List<ToolDef> tools = _toolRegistry.GetToolDefs();
        Usage? lastUsage = null;
        string? lastText = null;

        for (int round = 0; round <= maxRounds; round++)
        {
            ChatResult result;
            try
            {
                result = await _deepSeekClient.ChatAsync(working, tools, maxTokens: 256, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM 调用失败（round={Round}）", round);
                return new ReActResult { Text = fallback, Usage = lastUsage, Failed = true };
            }

            lastUsage = result.Usage;
            lastText = result.Message.Content;

            if (result.Message.ToolCalls is { Count: > 0 })
            {
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
                continue;
            }

            // 无工具调用：正常输出
            break;
        }

        if (string.IsNullOrWhiteSpace(lastText))
        {
            _logger.LogWarning("ReAct 循环结束后无文本输出（rounds={Max}），使用兜底文案", maxRounds);
            lastText = fallback;
        }

        return new ReActResult { Text = lastText, Usage = lastUsage, Failed = false };
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

public class ReActResult
{
    public required string Text { get; init; }
    public Usage? Usage { get; init; }
    public bool Failed { get; init; }
}
