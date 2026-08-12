using System.Text.RegularExpressions;
using RainBot.Services.Context;

namespace RainBot.Services.Topic;

/// <summary>
/// 轻量话题感知：提取最近消息的高频词与情绪（问号/感叹号密度），
/// 供暖群决策与介入判断使用（不调用 LLM，成本可控）。
/// </summary>
public class TopicAnalyzer(ILogger<TopicAnalyzer> logger)
{
    private readonly ILogger<TopicAnalyzer> _logger = logger;

    /// <summary>常见功能字（bigram 停用）</summary>
    private static readonly HashSet<char> StopChars = new("的了是在我你他她它们有和就不也都很个这那与及或对从为着吧吗呢啊哈呀哦嗯");

    private static readonly Regex EnglishWords = new(@"[a-zA-Z]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 分析最近历史，返回暖群提示文本（进 Block F），如：
    /// "最近话题：装机、显卡；问号多（5 个）讨论较热"
    /// </summary>
    public string Analyze(IReadOnlyList<HistoryEntry> history, int maxMessages = 20)
    {
        if (history.Count == 0)
        {
            return "";
        }

        List<string> recent = history.Skip(Math.Max(0, history.Count - maxMessages)).Select(h => h.Content).ToList();
        string joined = string.Join(" ", recent);

        // 高频词：英文词 + 中文双字组合
        Dictionary<string, int> freq = [];
        foreach (Match m in EnglishWords.Matches(joined))
        {
            string word = m.Value.ToLowerInvariant();
            if (word.Length >= 2 && word.Length <= 12)
            {
                freq[word] = freq.GetValueOrDefault(word) + 1;
            }
        }
        for (int i = 0; i < joined.Length - 1; i++)
        {
            char c1 = joined[i], c2 = joined[i + 1];
            if (c1 > 0x2E80 && c2 > 0x2E80 && !StopChars.Contains(c1) && !StopChars.Contains(c2))
            {
                string gram = $"{c1}{c2}";
                freq[gram] = freq.GetValueOrDefault(gram) + 1;
            }
        }
        List<string> topTopics = freq
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        int questionMarks = joined.Count(c => c == '？' || c == '?');
        int exclamationMarks = joined.Count(c => c == '！' || c == '!');
        int messages = recent.Count;

        List<string> parts = [];
        if (topTopics.Count > 0)
        {
            parts.Add($"最近话题：{string.Join("、", topTopics)}");
        }
        if (questionMarks >= 3)
        {
            parts.Add($"问号较多（{questionMarks} 个）");
        }
        if (exclamationMarks >= 3)
        {
            parts.Add($"情绪热烈（感叹号 {exclamationMarks} 个）");
        }
        parts.Add($"近 {messages} 条消息");

        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("话题分析：{Hint}", string.Join("；", parts));
        return string.Join("；", parts);
    }
}
