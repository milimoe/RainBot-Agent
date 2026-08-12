using System.Text;
using System.Text.Json.Nodes;
using RainBot.Services.Config;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.Tools;

namespace RainBot.Services.Tools;

/// <summary>
/// 管理员工具：改参数 / 查统计 / 静默群。所有操作校验发送者是否在机器人管理员列表。
/// </summary>
public class AdminTools(RuntimeConfig config, GroupStateManager states, Database db, ILogger<AdminTools> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly GroupStateManager _states = states;
    private readonly Database _db = db;
    private readonly ILogger<AdminTools> _logger = logger;

    /// <summary>注册工具执行器（由 Program.cs 装配时调用）</summary>
    public void Register(ToolRegistry registry)
    {
        registry.RegisterExecutor("admin_set_setting", async (args, ctx) =>
        {
            if (!ctx.IsAdmin) return "该操作仅管理员可用。";
            JsonObject? param = ToolRegistry.ParseArguments(args);
            string? key = param?["key"]?.GetValue<string>();
            string? value = param?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return "参数 key 和 value 不能为空。";
            }
            string? error = await _config.SetAsync(key, value);
            return error ?? $"参数 {key} 已更新为 {value}。";
        });

        registry.RegisterExecutor("admin_get_stats", async (_, ctx) =>
        {
            if (!ctx.IsAdmin) return "该操作仅管理员可用。";
            StringBuilder sb = new();
            foreach (GroupState state in _states.AllStates())
            {
                double hitRate = await _db.GetCacheHitRateAsync(state.GroupOpenId);
                string muted = state.Muted == true ? "（已静默）" : "";
                sb.AppendLine($"群 {state.GroupOpenId[..Math.Min(8, state.GroupOpenId.Length)]}: 消息 {state.TotalMessages} 条, 缓存命中率 {hitRate:0.0%}{muted}");
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "暂无统计数据。";
        });

        registry.RegisterExecutor("admin_mute_group", async (args, ctx) =>
        {
            if (!ctx.IsAdmin) return "该操作仅管理员可用。";
            JsonObject? param = ToolRegistry.ParseArguments(args);
            int minutes = 0;
            if (param?["minutes"] is JsonValue mv)
            {
                _ = mv.TryGetValue(out minutes);
            }
            await _states.SetMutedAsync(ctx.GroupOpenId, true);
            if (minutes > 0)
            {
                // 定时自动解除静默
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes));
                    await _states.SetMutedAsync(ctx.GroupOpenId, false);
                    _logger.LogInformation("群 {Group} 定时静默已到期自动解除", ctx.GroupOpenId);
                });
                return $"本群已静默 {minutes} 分钟。";
            }
            return "本群已永久静默。使用 admin_unmute_group 解除。";
        });

        registry.RegisterExecutor("admin_unmute_group", async (_, ctx) =>
        {
            if (!ctx.IsAdmin) return "该操作仅管理员可用。";
            await _states.SetMutedAsync(ctx.GroupOpenId, false);
            return "本群已解除静默。";
        });
    }
}
