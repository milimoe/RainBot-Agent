using System.Collections.Concurrent;
using RainBot.Models;

namespace RainBot.Services.Bots;

/// <summary>
/// 各机器人实例的连接状态登记处（QQ 官方与 OneBot 共用）。
/// WebUI 管理页、/health 与运维日志从这里取状态。
/// </summary>
public class BotConnectionRegistry
{
    private readonly ConcurrentDictionary<string, BotConnectionState> _states = new();

    public void SetConnected(string botId, bool connected)
    {
        BotConnectionState state = _states.GetOrAdd(botId, _ => new BotConnectionState());
        state.Connected = connected;
        if (connected)
        {
            state.LastConnectedAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetError(string botId, string? error)
        => _states.GetOrAdd(botId, _ => new BotConnectionState()).Error = error;

    public bool IsConnected(string botId)
        => _states.TryGetValue(botId, out BotConnectionState? state) && state.Connected;

    public DateTimeOffset? LastConnectedAt(string botId)
        => _states.TryGetValue(botId, out BotConnectionState? state) ? state.LastConnectedAt : null;

    /// <summary>汇总所有实例状态（缺失的实例按未连接输出）</summary>
    public List<BotInstanceStatus> Summarize(IEnumerable<BotInstance> instances, Func<string, List<string>>? activeTransports = null)
    {
        List<BotInstanceStatus> result = [];
        foreach (BotInstance instance in instances)
        {
            _states.TryGetValue(instance.Id, out BotConnectionState? state);
            result.Add(new BotInstanceStatus
            {
                Id = instance.Id,
                Name = instance.Name,
                Platform = instance.Platform.ToString(),
                Enabled = instance.Enabled,
                Connected = state?.Connected ?? false,
                ActiveTransports = activeTransports?.Invoke(instance.Id) ?? [],
                Error = state?.Error
            });
        }
        return result;
    }

    private sealed class BotConnectionState
    {
        public bool Connected { get; set; }
        public DateTimeOffset? LastConnectedAt { get; set; }
        public string? Error { get; set; }
    }
}
