using System.Collections.Concurrent;
using RainBot.Models;
using RainBot.Services.Storage;

namespace RainBot.Services.Trigger;

/// <summary>
/// 每群运行时状态管理：消息统计（休眠计数）、冷却、主动发言频控、静默计时、静默/降级标记。
/// 全部内存态 + 群静默/降级落库持久化。
/// </summary>
public class GroupStateManager
{
    private readonly ConcurrentDictionary<string, GroupState> _states = new();
    private readonly Database _db;
    private readonly ILogger<GroupStateManager> _logger;

    public GroupStateManager(Database db, ILogger<GroupStateManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    public GroupState GetOrCreate(string groupOpenId) => _states.GetOrAdd(groupOpenId, static id => new GroupState { GroupOpenId = id });

    /// <summary>群消息到达：更新统计（休眠计数）与静默计时</summary>
    public async Task OnMessageAsync(IncomingMessage message)
    {
        GroupState state = GetOrCreate(message.GroupOpenId);
        DateTimeOffset now = message.ReceivedAt;
        state.LastMessageUtc = now;
        state.IsPrivate = message.IsPrivate;
        state.DensityWindow.Add(now);
        state.TotalMessages++;
        if (message.IsAdmin)
        {
            state.LastAdminMessageUtc = now;
        }
        if (message.IsAtRobot && message.IsAdmin)
        {
            state.LastAdminAtMessageUtc = now;
        }
        if (state.Muted == null)
        {
            // 首次加载静默状态
            GroupProfile? profile = await _db.GetGroupProfileAsync(message.GroupOpenId);
            state.Muted = profile?.Muted ?? false;
        }
    }

    /// <summary>获取群画像（Block C 与静默/降级状态）</summary>
    public async Task<GroupProfile> GetProfileAsync(string groupOpenId)
    {
        GroupProfile? profile = await _db.GetGroupProfileAsync(groupOpenId);
        if (profile == null)
        {
            profile = new GroupProfile { GroupOpenId = groupOpenId };
            await _db.UpsertGroupProfileAsync(profile);
        }
        return profile;
    }

    public async Task SetMutedAsync(string groupOpenId, bool muted)
    {
        GroupState state = GetOrCreate(groupOpenId);
        state.Muted = muted;
        GroupProfile profile = await GetProfileAsync(groupOpenId);
        profile.Muted = muted;
        await _db.UpsertGroupProfileAsync(profile);
        _logger.LogInformation("群 {Group} 静默状态已改为：{Muted}", groupOpenId, muted);
    }

    public async Task SetDegradedAsync(string groupOpenId, bool degraded)
    {
        GroupState state = GetOrCreate(groupOpenId);
        state.Degraded = degraded;
        GroupProfile profile = await GetProfileAsync(groupOpenId);
        profile.Degraded = degraded;
        if (degraded)
        {
            profile.LastReset = "";
        }
        await _db.UpsertGroupProfileAsync(profile);
    }

    /// <summary>降级重置：清空历史与状态，重建上下文缓存</summary>
    public async Task ResetContextAsync(string groupOpenId)
    {
        GroupState state = GetOrCreate(groupOpenId);
        state.Degraded = false;
        state.LastResetUtc = DateTimeOffset.UtcNow;
        state.DensityWindow.Clear();
        GroupProfile profile = await GetProfileAsync(groupOpenId);
        profile.Degraded = false;
        profile.LastReset = DateTimeOffset.UtcNow.ToString("o");
        await _db.UpsertGroupProfileAsync(profile);
        _logger.LogInformation("群 {Group} 上下文已彻底重置，准备重建缓存", groupOpenId);
    }

    public IReadOnlyCollection<GroupState> AllStates() => _states.Values.ToList();
}

/// <summary>单个群的运行时状态</summary>
public class GroupState
{
    public required string GroupOpenId { get; init; }

    /// <summary>最后一条群消息时间（静默检测）</summary>
    public DateTimeOffset LastMessageUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>最后一条管理员发言时间（沉默唤醒"无管理员发言"条件）</summary>
    public DateTimeOffset LastAdminMessageUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>最后一条管理员 @ 消息时间</summary>
    public DateTimeOffset LastAdminAtMessageUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>密度窗口时间戳（滑动）</summary>
    public List<DateTimeOffset> DensityWindow { get; } = [];

    /// <summary>被动触发冷却：上次被动响应时间</summary>
    public DateTimeOffset LastPassiveTriggerUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>最近 1 小时主动发言时间戳（滑动，频控）</summary>
    public List<DateTimeOffset> ActiveWindow { get; } = [];

    /// <summary>
    /// 最近一次暖群尝试时间（无论成功/静默跳过都记录，用于失败重试冷却：
    /// 防止"无内容可说"时每 30s 空转反复调 LLM）。
    /// </summary>
    public DateTimeOffset LastWarmupAttemptUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>累计消息数</summary>
    public long TotalMessages { get; set; }

    /// <summary>管理员静默标记（null=未加载，加载后 bool）</summary>
    public bool? Muted { get; set; }

    /// <summary>降级运行标记（压缩后提升阈值）</summary>
    public bool Degraded { get; set; }

    /// <summary>上次上下文重置时间（降级恢复用）</summary>
    public DateTimeOffset LastResetUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>是否私聊会话（私聊不参与暖群调度）</summary>
    public bool IsPrivate { get; set; }
}
