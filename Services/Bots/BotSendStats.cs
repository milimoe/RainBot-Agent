using System.Collections.Concurrent;
using RainBot.Services.Storage;

namespace RainBot.Services.Bots;

/// <summary>
/// 机器人发送统计：按实例累计成功/失败次数与最后一次错误。
/// 内存计数用于实时展示，同时落库（bot_send_stats 表）保证重启后不丢。
/// </summary>
public class BotSendStats(Database db, ILogger<BotSendStats> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _counters = new();
    private readonly Database _db = db;
    private readonly ILogger<BotSendStats> _logger = logger;

    /// <summary>启动时从数据库载入累计值</summary>
    public async Task InitializeAsync()
    {
        try
        {
            List<BotSendStatRow> rows = await _db.GetBotSendStatsAsync();
            foreach (BotSendStatRow row in rows)
            {
                _counters[row.BotId] = new Entry
                {
                    Sent = row.Sent,
                    Failed = row.Failed,
                    LastError = string.IsNullOrWhiteSpace(row.LastError) ? null : row.LastError
                };
            }
            if (rows.Count > 0)
            {
                _logger.LogInformation("发送统计已载入：{Count} 个实例", rows.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送统计载入失败（不影响启动）");
        }
    }

    /// <summary>记录一次发送结果</summary>
    public async Task RecordAsync(string botId, bool success, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(botId))
        {
            return;
        }
        Entry entry = _counters.GetOrAdd(botId, _ => new Entry());
        lock (entry)
        {
            if (success)
            {
                entry.Sent++;
            }
            else
            {
                entry.Failed++;
                entry.LastError = error ?? "未知错误";
            }
        }
        try
        {
            await _db.UpsertBotSendStatsAsync(botId, entry.Sent, entry.Failed, entry.LastError);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "发送统计落库失败（{BotId}）", botId);
        }
    }

    /// <summary>全部实例统计快照（按实例 Id 排序）</summary>
    public IReadOnlyList<BotSendStatSnapshot> Snapshot()
        => _counters
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new BotSendStatSnapshot(kv.Key, kv.Value.Sent, kv.Value.Failed, kv.Value.LastError))
            .ToList();

    /// <summary>单个实例统计（无记录返回 0）</summary>
    public BotSendStatSnapshot Get(string botId)
        => _counters.TryGetValue(botId, out Entry? e)
            ? new BotSendStatSnapshot(botId, e.Sent, e.Failed, e.LastError)
            : new BotSendStatSnapshot(botId, 0, 0, null);

    private sealed class Entry
    {
        public long Sent { get; set; }
        public long Failed { get; set; }
        public string? LastError { get; set; }
    }
}

/// <summary>发送统计快照</summary>
public record BotSendStatSnapshot(string BotId, long Sent, long Failed, string? LastError);
