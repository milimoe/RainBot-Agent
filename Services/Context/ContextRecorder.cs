using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RainBot.Services.Llm;

namespace RainBot.Services.Context;

public sealed record ContextSnapshot(DateTimeOffset TimeUtc, string Text, int EstimatedTokens,
    int WatermarkTokens, int HistoryCount, int HistoryDropped, IReadOnlyDictionary<string, int> Blocks,
    int ExtraTokens, int ToolSchemaTokens, int ImageCount, bool Truncated);

/// <summary>保留最近一次实际模型请求的文字快照，不保存图片 base64，不调用模型。</summary>
public sealed class ContextRecorder
{
    private const int MaxSessions = 64;
    private const int MaxSnapshotChars = 1_000_000;
    private const int PageChars = 1200;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ContextSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Record(string group, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef> tools,
        ComposeResult? compose, int watermarkTokens)
    {
        StringBuilder text = new();
        int tokens = 0, extra = 0, images = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            StringBuilder body = new(message.ContentParts is { Count: > 0 } ? "" : message.Content ?? "");
            if (message.ContentParts is { Count: > 0 })
                foreach (var part in message.ContentParts)
                {
                    if (part.Type == "text") body.AppendLine(part.Text);
                    else if (part.Type == "image_url") { body.AppendLine("[图片：内容未导出]"); images++; }
                }
            if (message.ToolCalls is { Count: > 0 }) body.AppendLine(JsonSerializer.Serialize(message.ToolCalls));
            string content = body.ToString();
            int messageTokens = TokenEstimator.EstimateMessage(content);
            tokens += messageTokens;
            if (compose != null && i >= compose.Messages.Count) extra += messageTokens;
            text.AppendLine($"【消息 {i + 1} · {message.Role}{(message.ToolCallId == null ? "" : $" · tool_call_id={message.ToolCallId}")}】").AppendLine(content).AppendLine();
        }
        string toolSchema = JsonSerializer.Serialize(tools);
        int toolTokens = tools.Count > 0 ? TokenEstimator.Estimate(toolSchema) : 0;
        tokens += toolTokens;
        if (tools.Count > 0) text.AppendLine("【请求 tools 定义】").AppendLine(toolSchema);
        // 整体脱敏后分页，避免完整 ID 被跨页切开而绕过发送侧脱敏。
        string safe = Regex.Replace(text.ToString(), "[0-9a-fA-F]{20,}", "[ID]")
            .Replace("<qqbot-", "&lt;qqbot-", StringComparison.OrdinalIgnoreCase);
        bool truncated = safe.Length > MaxSnapshotChars;
        if (truncated) safe = safe[..MaxSnapshotChars];
        var snapshot = new ContextSnapshot(DateTimeOffset.UtcNow, safe, tokens, watermarkTokens,
            compose?.HistoryCount ?? 0, compose?.HistoryDropped ?? 0,
            new Dictionary<string, int>(compose?.BlockTokens ?? new Dictionary<string, int>()), extra, toolTokens, images, truncated);
        lock (_lock)
        {
            _snapshots[group] = snapshot;
            if (_snapshots.Count > MaxSessions)
                _snapshots.Remove(_snapshots.MinBy(s => s.Value.TimeUtc).Key);
        }
    }

    public ContextSnapshot? GetLast(string group)
    {
        lock (_lock) return _snapshots.GetValueOrDefault(group);
    }

    public string View(string group, string? mode, string? pageText)
    {
        ContextSnapshot? snapshot = GetLast(group);
        if (snapshot == null) return "当前会话尚无上下文快照，等下一次模型对话后再查看。快照仅存内存，重启后清空。";
        string time = snapshot.TimeUtc.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (mode == "full")
        {
            int page = 1;
            int pages = Math.Max(1, (int)Math.Ceiling(snapshot.Text.Length / (double)PageChars));
            if (pageText != null && (!int.TryParse(pageText, out page) || page < 1 || page > pages))
                return $"页码应为 1–{pages}，用法：/context full [页码]。";
            int start = (page - 1) * PageChars;
            int end = Math.Min(page * PageChars, snapshot.Text.Length);
            // 避免分页将 Emoji 等 UTF-16 代理对切开。
            if (start > 0 && char.IsLowSurrogate(snapshot.Text[start]) && char.IsHighSurrogate(snapshot.Text[start - 1])) start--;
            if (end < snapshot.Text.Length && char.IsLowSurrogate(snapshot.Text[end]) && char.IsHighSurrogate(snapshot.Text[end - 1])) end--;
            string content = snapshot.Text[start..end];
            string next = page < pages ? $"\n下一页：/context full {page + 1}" : "\n（全文结束）";
            return $"上下文快照 {time}（北京时间），第 {page}/{pages} 页{(snapshot.Truncated ? "，导出已截断" : "")}：\n{content}{next}";
        }
        if (mode != null) return "用法：/context 查看窗口概览；/context full [页码] 查看全文。";
        string blocks = string.Join("\n", snapshot.Blocks.Select(b => $"{b.Key}：≈{b.Value:N0} tokens"));
        double ratio = snapshot.WatermarkTokens > 0 ? (double)snapshot.EstimatedTokens / snapshot.WatermarkTokens : 0;
        return $"当前会话最近一次模型请求（{time}，北京时间）：\n" +
            $"估算输入：≈{snapshot.EstimatedTokens:N0} tokens\n治理水位：{snapshot.WatermarkTokens:N0} tokens，占用 {ratio:P1}（不是模型硬上限）\n" +
            $"历史：纳入 {snapshot.HistoryCount} 条，组装丢弃 {snapshot.HistoryDropped} 条\n{blocks}\n" +
            $"追加消息：≈{snapshot.ExtraTokens:N0} tokens\n请求 tools 定义：≈{snapshot.ToolSchemaTokens:N0} tokens\n" +
            $"图片：{snapshot.ImageCount} 张（图片 tokens 未计入估算，全文只显示占位）\n/context full [页码] 查看脱敏全文；只保留最近 64 个会话，重启清空。";
    }
}
