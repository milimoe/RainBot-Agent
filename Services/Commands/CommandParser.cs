using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;

namespace RainBot.Services.Commands;

/// <summary>
/// 指令解析与执行：
/// - 用户指令：/忘掉我（清除自己的 L0 画像）
/// - 管理员指令：/admin list | set | mute | unmute | stats | admin add/remove | forget | help
/// - /status：状态查看（管理员）
/// </summary>
public class CommandParser(RuntimeConfig config, GroupStateManager states, Database db, ILogger<CommandParser> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly GroupStateManager _states = states;
    private readonly Database _db = db;
    private readonly ILogger<CommandParser> _logger = logger;

    private static readonly Regex StripTagsRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>尝试解析为指令；返回 null 表示不是指令</summary>
    public ParsedCommand? Parse(string content)
    {
        string cleaned = StripTagsRegex.Replace(content, "").Trim().TrimStart('/', '／');
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        string[] parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string[] args = parts.Length > 1 ? parts[1..] : [];

        return command switch
        {
            "忘掉我" or "forgetme" => new ParsedCommand(CommandKind.ForgetMe),
            "status" => new ParsedCommand(CommandKind.Status),
            "admin" => ParseAdmin(args),
            _ => null
        };
    }

    private static ParsedCommand? ParseAdmin(string[] args)
    {
        if (args.Length == 0)
        {
            return new ParsedCommand(CommandKind.AdminHelp);
        }
        string sub = args[0].ToLowerInvariant();
        return sub switch
        {
            "help" or "帮助" => new ParsedCommand(CommandKind.AdminHelp),
            "list" or "ls" or "参数" => new ParsedCommand(CommandKind.AdminList),
            "set" or "设置" => args.Length >= 3 ? new ParsedCommand(CommandKind.AdminSet, string.Join(' ', args.Skip(1).Take(args.Length - 2)), args[^1]) : null,
            "mute" or "静默" => new ParsedCommand(CommandKind.AdminMute, args.Length >= 2 ? args[1] : "0"),
            "unmute" or "解除静默" => new ParsedCommand(CommandKind.AdminUnmute),
            "stats" or "统计" => new ParsedCommand(CommandKind.AdminStats),
            "admin" => args.Length >= 2 && (args[1].ToLowerInvariant() == "add" || args[1].ToLowerInvariant() == "remove")
                ? new ParsedCommand(CommandKind.AdminManage, args[1].ToLowerInvariant(), args.Length >= 3 ? args[2] : "")
                : null,
            "forget" or "清除" => args.Length >= 2 ? new ParsedCommand(CommandKind.AdminForget, args[1]) : null,
            _ => new ParsedCommand(CommandKind.AdminHelp)
        };
    }

    /// <summary>执行指令，返回回复文本（null = 不回复）</summary>
    public async Task<string?> ExecuteAsync(ParsedCommand command, string groupOpenId, string senderOpenId, bool isAdmin)
    {
        switch (command.Kind)
        {
            case CommandKind.ForgetMe:
                await _db.DeleteUserProfileAsync(groupOpenId, senderOpenId);
                _logger.LogInformation("用户 {User} 已清除自己的画像", senderOpenId);
                return "好，我这就把你忘掉，就当从没认识过你 🌧️";

            case CommandKind.Status:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return await BuildStatusAsync(groupOpenId);

            case CommandKind.AdminHelp:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return "管理员指令：\n/admin list 查看参数\n/admin set 参数 值\n/admin mute [分钟] / unmute\n/admin stats\n/admin admin add|remove openid\n/admin forget 短id\n/status";

            case CommandKind.AdminList:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return await BuildParamListAsync();

            case CommandKind.AdminSet:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                if (string.IsNullOrWhiteSpace(command.Key) || string.IsNullOrWhiteSpace(command.Value))
                {
                    return "用法：/admin set 参数 值（如 /admin set Trigger.PassiveCooldownSeconds 60）";
                }
                string? error = await _config.SetAsync(command.Key, command.Value);
                return error ?? $"参数 {command.Key} 已更新为 {command.Value} ☔";

            case CommandKind.AdminMute:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                if (int.TryParse(command.Key, out int minutes) && minutes > 0)
                {
                    await _states.SetMutedAsync(groupOpenId, true);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(minutes));
                        await _states.SetMutedAsync(groupOpenId, false);
                        _logger.LogInformation("群 {Group} 定时静默到期自动解除", groupOpenId);
                    });
                    return $"本群已静默 {minutes} 分钟，有事叫我 ☔";
                }
                await _states.SetMutedAsync(groupOpenId, true);
                return "本群已永久静默 ☔";

            case CommandKind.AdminUnmute:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                await _states.SetMutedAsync(groupOpenId, false);
                return "本群已解除静默，我又活了 🌧️";

            case CommandKind.AdminStats:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return await BuildStatsAsync(groupOpenId);

            case CommandKind.AdminManage:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                if (string.IsNullOrWhiteSpace(command.Value))
                {
                    return "用法：/admin admin add|remove openid";
                }
                if (command.Key == "add")
                {
                    await _config.AddAdminAsync(command.Value);
                    return $"已将 {command.Value[..Math.Min(8, command.Value.Length)]}… 添加为管理员 ☔";
                }
                await _config.RemoveAdminAsync(command.Value);
                return $"已移除管理员 {command.Value[..Math.Min(8, command.Value.Length)]}… ☔";

            case CommandKind.AdminForget:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                string target = command.Key?.Trim() ?? "";
                UserProfile? profile = target.StartsWith("u", StringComparison.OrdinalIgnoreCase) && target.Length <= 12
                    ? await _db.FindUserByShortIdAsync(groupOpenId, target)
                    : await _db.GetUserProfileAsync(groupOpenId, target);
                if (profile == null)
                {
                    return $"没有找到用户 {target} 的画像。";
                }
                await _db.DeleteUserProfileAsync(groupOpenId, profile.UserOpenId);
                return $"已清除用户 {target} 的画像 ☔";

            default:
                return null;
        }
    }

    private async Task<string> BuildParamListAsync()
    {
        List<(string Key, string Value, bool Overridden)> items = await _config.ListAllAsync();
        var lines = items.Select(i => i.Overridden ? $"⚙ {i.Key} = {i.Value}（已修改）" : $"{i.Key} = {i.Value}");
        return string.Join('\n', lines);
    }

    private async Task<string> BuildStatsAsync(string groupOpenId)
    {
        double hitRate = await _db.GetCacheHitRateAsync(groupOpenId);
        GroupState state = _states.GetOrCreate(groupOpenId);
        return $"本群：消息 {state.TotalMessages} 条，缓存命中率 {hitRate:0.0%}，静默状态：{(state.Muted == true ? "已静默" : "正常")} ☔";
    }

    private async Task<string> BuildStatusAsync(string groupOpenId)
    {
        double hitRate = await _db.GetCacheHitRateAsync(groupOpenId);
        GroupState state = _states.GetOrCreate(groupOpenId);
        List<string> admins = await _config.GetAdminOpenIdsAsync();
        return $"机器人：雨 🌧️\n本群消息 {state.TotalMessages} 条，缓存命中率 {hitRate:0.0%}\n管理员 {admins.Count} 人，静默：{(state.Muted == true ? "是" : "否")}，降级：{(state.Degraded ? "是" : "否")}";
    }
}

public enum CommandKind
{
    ForgetMe,
    Status,
    AdminHelp,
    AdminList,
    AdminSet,
    AdminMute,
    AdminUnmute,
    AdminStats,
    AdminManage,
    AdminForget
}

public class ParsedCommand(CommandKind kind, string? key = null, string? value = null)
{
    public CommandKind Kind { get; } = kind;
    public string? Key { get; } = key;
    public string? Value { get; } = value;
}
