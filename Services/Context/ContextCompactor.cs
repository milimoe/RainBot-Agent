using RainBot.Services.Config;
using RainBot.Services.Profile;
using RainBot.Services.Trigger;

namespace RainBot.Services.Context;

public sealed class ContextCompactor(GroupStateManager states, HistoryStore history, Distiller distiller, RuntimeConfig config)
{
    public async Task<string> CompactAsync(string group, CancellationToken ct = default)
    {
        var gate = states.ContextLock(group);
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = history.GetRecent(group, int.MaxValue, out _);
            if (snapshot.Count == 0) return "当前群没有可压缩的上下文。";
            string summary = await distiller.CompressAsync(group, snapshot, ct);
            if (string.IsNullOrWhiteSpace(summary)) return "上下文压缩失败，原历史已保留，请稍后重试。";
            int keep = Math.Max(0, config.Config.Context.DistillKeepMessages);
            int removed = history.CompactSnapshot(group, snapshot, keep);
            return $"已压缩当前群上下文：{snapshot.Count} 条历史生成摘要，移除 {removed} 条旧消息，保留最近 {Math.Min(keep, snapshot.Count)} 条及新到消息。";
        }
        finally { gate.Release(); }
    }
}
