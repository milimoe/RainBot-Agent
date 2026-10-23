using System.Text.Json;
using RainBot.Services.Llm;

namespace RainBot.Services.Context;

/// <summary>
/// 轻量 token 估算（仅用于水位预算，不追求精确；估算值偏保守）。
/// 中文按 ~0.8 token/字，ASCII 按 ~4 字符/token，每条消息加固定开销。
/// </summary>
public static class TokenEstimator
{
    /// <summary>单条消息固定开销（角色标记、换行等）</summary>
    public const int PerMessageOverhead = 8;

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int cjk = text.Count(c => c > 0x2E80);
        int ascii = text.Length - cjk;
        return (int)Math.Ceiling(cjk * 0.8 + ascii / 4.0) + 4;
    }

    /// <summary>每轮请求的文本估算，包含角色、工具定义与工具调用；不含图片视觉 tokens。</summary>
    public static int EstimateRequestText(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef>? tools = null)
    {
        int total = 0;
        foreach (var message in messages)
        {
            string text = message.ContentParts is { Count: > 0 }
                ? string.Concat(message.ContentParts.Where(p => p.Type == "text").Select(p => p.Text))
                : message.Content ?? "";
            total += EstimateMessage(message.Role + "\n" + text);
            if (message.ToolCalls is { Count: > 0 }) total += Estimate(JsonSerializer.Serialize(message.ToolCalls));
            if (message.ToolCallId != null) total += Estimate(message.ToolCallId);
        }
        if (tools is { Count: > 0 }) total += Estimate(JsonSerializer.Serialize(tools));
        return total;
    }

    public static int EstimateMessage(string content) => Estimate(content) + PerMessageOverhead;
}

