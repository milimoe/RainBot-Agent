using System.Collections.Concurrent;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Context;

/// <summary>
/// Block E 历史管理：内存链表（尾部追加、头部整条丢弃）+ SQLite 持久化。
/// 绝不改写前文：只删除整条、只追加尾部。
/// </summary>
public class HistoryStore(RuntimeConfig config, Database db, ILogger<HistoryStore> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly Database _db = db;
    private readonly ILogger<HistoryStore> _logger = logger;
    private readonly ConcurrentDictionary<string, LinkedList<HistoryEntry>> _histories = new();

    /// <summary>追加一条历史（尾部），超上限时头部整条丢弃</summary>
    public async Task AppendAsync(string groupOpenId, IncomingMessage message)
    {
        LinkedList<HistoryEntry> list = _histories.GetOrAdd(groupOpenId, _ => []);
        lock (list)
        {
            list.AddLast(new HistoryEntry
            {
                UserOpenId = message.SenderOpenId,
                Content = message.Content,
                Time = message.ReceivedAt,
                IsAt = message.IsAtRobot
            });
            int max = _config.Config.Context.MaxHistoryPerGroup;
            while (list.Count > max)
            {
                list.RemoveFirst(); // 头部整条丢弃
            }
        }
        await _db.InsertMessageAsync(message.MsgId, message.GroupOpenId, message.SenderOpenId, message.Content, message.IsAtRobot, message.ReceivedAt);
        await _db.TrimHistoryAsync(message.GroupOpenId, _config.Config.Context.MaxHistoryPerGroup);
    }

    /// <summary>
    /// 取最近历史（尾部优先），受 token 预算约束：从尾部向前取整条消息，
    /// 超出预算则整条丢弃（头部丢弃数 = 未纳入条数）。
    /// </summary>
    public List<HistoryEntry> GetRecent(string groupOpenId, int tokenBudget, out int dropped)
    {
        LinkedList<HistoryEntry>? list = _histories.GetOrAdd(groupOpenId, _ => []);
        List<HistoryEntry> result = [];
        int total = 0;
        dropped = 0;
        lock (list)
        {
            // 倒序取
            LinkedListNode<HistoryEntry>? node = list.Last;
            while (node != null)
            {
                int tokens = TokenEstimator.EstimateMessage(node.Value.Content);
                if (total + tokens > tokenBudget)
                {
                    dropped++;
                }
                else
                {
                    result.Add(node.Value);
                    total += tokens;
                }
                node = node.Previous;
            }
        }
        result.Reverse(); // 恢复时间正序
        return result;
    }

    /// <summary>从头部整条删除（压缩前防御第一步）</summary>
    public int TrimHead(string groupOpenId, int count)
    {
        LinkedList<HistoryEntry>? list = _histories.GetOrAdd(groupOpenId, _ => []);
        int removed = 0;
        lock (list)
        {
            while (removed < count && list.Count > 0)
            {
                list.RemoveFirst();
                removed++;
            }
        }
        if (removed > 0 && _logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("群 {Group} 头部丢弃 {Count} 条历史", groupOpenId, removed);
        return removed;
    }

    /// <summary>蒸馏压缩后：清空历史，仅保留最近 keepCount 条</summary>
    public void ClearKeep(string groupOpenId, int keepCount)
    {
        LinkedList<HistoryEntry>? list = _histories.GetOrAdd(groupOpenId, _ => []);
        lock (list)
        {
            while (list.Count > keepCount)
            {
                list.RemoveFirst();
            }
        }
    }

    /// <summary>彻底清空（降级重置重建缓存）</summary>
    public void ClearAll(string groupOpenId)
    {
        _histories[groupOpenId] = [];
    }

    /// <summary>历史总条数</summary>
    public int Count(string groupOpenId) => _histories.TryGetValue(groupOpenId, out LinkedList<HistoryEntry>? list) ? list.Count : 0;
}

/// <summary>一条历史消息</summary>
public class HistoryEntry
{
    public required string UserOpenId { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Time { get; init; }
    public bool IsAt { get; init; }
}
