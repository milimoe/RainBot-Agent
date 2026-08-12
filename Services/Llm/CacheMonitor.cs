using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Llm;

/// <summary>
/// 缓存命中率监控（成本风控）：每轮 LLM 用量落库，按群计算最近 N 次命中率，
/// 低于阈值触发告警日志（限频防刷屏）。
/// </summary>
public class CacheMonitor(Database db, RuntimeConfig config, ILogger<CacheMonitor> logger)
{
    private readonly Database _db = db;
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<CacheMonitor> _logger = logger;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = [];
    private readonly Lock _lock = new();

    private const int SampleCount = 50;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(30);

    /// <summary>记录一次 LLM 用量并检查命中率</summary>
    public async Task RecordAsync(string groupOpenId, string triggerType, Usage? usage)
    {
        if (usage == null)
        {
            return;
        }
        await _db.InsertStatAsync(groupOpenId, triggerType, usage.PromptCacheHitTokens, usage.PromptCacheMissTokens, usage.PromptTokens, usage.CompletionTokens);
        await CheckAndAlertAsync(groupOpenId);
    }

    private async Task CheckAndAlertAsync(string groupOpenId)
    {
        double hitRate = await _db.GetCacheHitRateAsync(groupOpenId, SampleCount);
        double threshold = _config.Config.Context.CacheAlertThreshold;
        if (hitRate < threshold)
        {
            lock (_lock)
            {
                if (_lastAlert.TryGetValue(groupOpenId, out DateTimeOffset last) && DateTimeOffset.UtcNow - last < AlertCooldown)
                {
                    return;
                }
                _lastAlert[groupOpenId] = DateTimeOffset.UtcNow;
            }
            _logger.LogWarning("【成本风控】群 {Group} 最近 {Count} 次调用缓存命中率 {Rate:0.0%}，低于 {Threshold:0%}，请检查上下文组装逻辑（前缀是否被污染）",
                groupOpenId, SampleCount, hitRate, threshold);
        }
    }
}
