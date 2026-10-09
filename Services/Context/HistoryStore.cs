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

    /// <summary>追加一条历史（尾部），超上限时头部整条丢弃。纯图片消息以 [图片] 占位入库。</summary>
    public async Task AppendAsync(string groupOpenId, IncomingMessage message)
    {
        string content = message.DisplayContent;
        LinkedList<HistoryEntry> list = _histories.GetOrAdd(groupOpenId, _ => []);
        lock (list)
        {
            list.AddLast(new HistoryEntry
            {
                UserOpenId = message.SenderOpenId,
                Nickname = message.Username,
                Content = content,
                Time = message.ReceivedAt,
                IsAt = message.IsAtRobot,
                ImageUrls = message.ImageUrls,
                MsgIdx = message.MsgIdx
            });
            int max = _config.Config.Context.MaxHistoryPerGroup;
            while (list.Count > max)
            {
                list.RemoveFirst(); // 头部整条丢弃
            }
        }
        await _db.InsertMessageAsync(message.MsgId, message.GroupOpenId, message.SenderOpenId, content, message.IsAtRobot, message.ReceivedAt, message.Username);
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

    /// <summary>
    /// 回溯最近一张图片（「先发图、再 @ 机器人」的跟进提问场景）：
    /// 从尾部向前找**触发者本人**最近一条带图消息，取其图片 URL；仅限时间窗口内。
    /// 只认发送者本人的图片（不跨人取图），避免把别人刚发的图误当成本次提问的对象。
    /// 图片 URL 只存内存，重启后回溯为空（不影响当轮直接带图的消息）。
    /// </summary>
    public List<string> FindRecentImageUrls(string groupOpenId, string? senderOpenId, TimeSpan window, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(senderOpenId) || !_histories.TryGetValue(groupOpenId, out LinkedList<HistoryEntry>? list))
        {
            return [];
        }
        DateTimeOffset cutoff = now - window;
        lock (list)
        {
            for (LinkedListNode<HistoryEntry>? node = list.Last; node != null; node = node.Previous)
            {
                HistoryEntry entry = node.Value;
                if (entry.Time < cutoff)
                {
                    break; // 更早的也超窗（历史按追加顺序，时间近似递增）
                }
                if (entry.ImageUrls.Count > 0 && entry.UserOpenId == senderOpenId)
                {
                    return entry.ImageUrls;
                }
            }
        }
        return [];
    }

    /// <summary>
    /// 按消息索引回溯（官方引用消息只带 ref_msg_idx、未随事件下发引用内容时）：
    /// 找本人历史里 msg_idx 匹配的那条，取其正文与图片。只存内存，重启后为空。
    /// </summary>
    public HistoryEntry? FindByMsgIdx(string groupOpenId, string msgIdx)
    {
        if (string.IsNullOrEmpty(msgIdx) || !_histories.TryGetValue(groupOpenId, out LinkedList<HistoryEntry>? list))
        {
            return null;
        }
        lock (list)
        {
            for (LinkedListNode<HistoryEntry>? node = list.Last; node != null; node = node.Previous)
            {
                if (!string.IsNullOrEmpty(node.Value.MsgIdx) && node.Value.MsgIdx == msgIdx)
                {
                    return node.Value;
                }
            }
        }
        return null;
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
    public string? Nickname { get; init; }
    public required string UserOpenId { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Time { get; init; }
    public bool IsAt { get; init; }

    /// <summary>该消息附带的图片 URL（仅内存保留、不落库）：供「先发图再 @ 机器人」的跟进提问回溯取图</summary>
    public List<string> ImageUrls { get; init; } = [];

    /// <summary>本条消息索引（官方 msg_idx，仅内存）：供他人引用本条时按 ref_msg_idx 回溯</summary>
    public string MsgIdx { get; init; } = "";
}
