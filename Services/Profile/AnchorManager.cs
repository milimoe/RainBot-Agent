using System.Collections.Concurrent;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Profile;

/// <summary>
/// L1 常驻锚点（Block D）：每群 Top N 高互动用户的极简标签（单条 &lt; 20 tokens）。
/// 输出按 openid 字典序排列保证前缀稳定；成员变化低频，带内存缓存。
/// </summary>
public class AnchorManager(RuntimeConfig config, Database db, ILogger<AnchorManager> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly Database _db = db;
    private readonly ILogger<AnchorManager> _logger = logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, string Text)> _cache = new();

    /// <summary>获取该群 Block D 锚点文本（空串表示无锚点）</summary>
    public async Task<string> GetAnchorsTextAsync(string groupOpenId, int maxCount, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(groupOpenId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(5))
        {
            return cached.Text;
        }

        List<UserProfile> topUsers = await _db.GetTopUsersAsync(groupOpenId, maxCount);
        // 按 openid 字典序输出，保证 Block D 前缀稳定
        List<string> lines = topUsers
            .OrderBy(u => u.UserOpenId, StringComparer.Ordinal)
            .Select(u =>
            {
                List<string> tags = u.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(2).ToList();
                string tagText = tags.Count > 0 ? " " + string.Join(" ", tags.Select(t => $"#{t}")) : "";
                return $"{UserIdentityResolver.ShortId(u.UserOpenId)}:{tagText}";
            })
            .Where(l => l.Length <= 40) // 单条 < 20 tokens 约束（保守按 2 字符/token 估算）
            .ToList();

        string text = lines.Count > 0 ? string.Join("\n", lines) : "";
        _cache[groupOpenId] = (DateTimeOffset.UtcNow, text);
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("群 {Group} 锚点已刷新：{Count} 条", groupOpenId, lines.Count);
        return text;
    }

    /// <summary>锚点变化时立即失效缓存</summary>
    public void Invalidate(string groupOpenId)
    {
        _cache.TryRemove(groupOpenId, out _);
    }

    public static string ShortId(string openId) => UserIdentityResolver.ShortId(openId)[1..];
}
