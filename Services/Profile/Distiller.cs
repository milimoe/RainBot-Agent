using System.Text.Json;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Storage;

namespace RainBot.Services.Profile;

/// <summary>
/// 蒸馏压缩（强制压缩阶段）：调用 Flash 将长历史压缩为 3-5 条核心事实摘要，
/// 存库（distill_summaries），随后清空 Block E 仅保留最近几条。
/// </summary>
public class Distiller(RuntimeConfig config, DeepSeekClient deepSeekClient, Database db, HistoryStore historyStore, ILogger<Distiller> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly DeepSeekClient _deepSeekClient = deepSeekClient;
    private readonly Database _db = db;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly ILogger<Distiller> _logger = logger;

    /// <summary>
    /// 执行蒸馏压缩：返回摘要文本（失败时返回空串，调用方继续降级但不清空历史）。
    /// </summary>
    public async Task<string> CompressAsync(string groupOpenId, IReadOnlyList<HistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return "";
        }

        // 历史文本：按时间正序拼接（蒸馏输入在消息尾部，不影响前缀缓存）
        string historyText = string.Join("\n", history.Select(h => $"{ShortId(h.UserOpenId)}: {h.Content}"));

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("你是群聊记忆压缩器。把群聊历史压缩为 3-5 条核心事实摘要，保留：话题、人物兴趣、关键信息、情绪变化。只输出 JSON 字符串数组，如 [\"摘要1\",\"摘要2\"]，不要任何其他文字。"),
            ChatMessage.User($"群聊历史：\n{historyText}")
        };

        try
        {
            ChatResult result = await _deepSeekClient.ChatAsync(messages, maxTokens: 500);
            string? content = result.Message.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }
            string summary = ExtractJsonArray(content);
            await _db.SaveDistillSummaryAsync(groupOpenId, summary);
            _logger.LogInformation("群 {Group} 历史已蒸馏压缩，原文 {Count} 条 → 摘要 {Summary}", groupOpenId, history.Count, summary);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "群 {Group} 蒸馏压缩失败", groupOpenId);
            return "";
        }
    }

    /// <summary>从 LLM 输出中提取 JSON 数组（容错前后缀文本）</summary>
    private static string ExtractJsonArray(string text)
    {
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }
        return text.Trim();
    }

    private static string ShortId(string openId) => openId.Length > 6 ? openId[..6] : openId;
}
