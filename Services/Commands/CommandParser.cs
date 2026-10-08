using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.Fun;
using RainBot.Services.Llm;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;

namespace RainBot.Services.Commands;
/// <summary>
/// 指令解析与执行：
/// - 用户指令：/忘掉我（清除自己的 L0 画像）
/// - 管理员指令：/admin list | set | mute | unmute | stats | reasoning | admin add/remove | forget | help
/// - /status：状态查看（管理员）
/// </summary>
public class CommandParser(RuntimeConfig config, GroupStateManager states, Database db, SayNoWordsService sayNoWords, OsmImageCatalog osmCatalog, ReasoningRecorder reasoningRecorder, BotInstanceStore botStore, ILogger<CommandParser> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly GroupStateManager _states = states;
    private readonly Database _db = db;
    private readonly SayNoWordsService _sayNoWords = sayNoWords;
    private readonly OsmImageCatalog _osmCatalog = osmCatalog;
    private readonly ReasoningRecorder _reasoningRecorder = reasoningRecorder;
    private readonly BotInstanceStore _botStore = botStore;
    private readonly ILogger<CommandParser> _logger = logger;

    private static readonly Regex StripTagsRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>/admin sayno list | /admin sayno 表名 add|remove 词</summary>
    private static ParsedCommand? ParseSayNo(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0].ToLowerInvariant() is "list" or "ls" or "列表"))
        {
            return new ParsedCommand(CommandKind.AdminSayNoList);
        }
        if (args.Length >= 3 && args[1].ToLowerInvariant() is "add" or "remove")
        {
            return new ParsedCommand(CommandKind.AdminSayNoUpdate, args[0], $"{args[1].ToLowerInvariant()} {string.Join(' ', args[2..])}");
        }
        return null;
    }

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
            "fun" or "娱乐" => new ParsedCommand(CommandKind.FunStatus),
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
            "reasoning" or "思考" or "思维链" => new ParsedCommand(CommandKind.AdminReasoning),
            "admin" => args.Length >= 2 && (args[1].ToLowerInvariant() == "add" || args[1].ToLowerInvariant() == "remove")
                ? new ParsedCommand(CommandKind.AdminManage, args[1].ToLowerInvariant(), args.Length >= 3 ? args[2] : "")
                : null,
            "forget" or "清除" => args.Length >= 2 ? new ParsedCommand(CommandKind.AdminForget, args[1]) : null,
            "sayno" => ParseSayNo(args),
            _ => new ParsedCommand(CommandKind.AdminHelp)
        };
    }

    /// <summary>执行指令，返回回复文本（null = 不回复）。botId 决定管理员维护与状态查看的目标实例</summary>
    public async Task<string?> ExecuteAsync(ParsedCommand command, string groupOpenId, string senderOpenId, bool isAdmin, string? botId = null)
    {
        botId = string.IsNullOrWhiteSpace(botId) ? Database.LegacyBotId : botId;
        switch (command.Kind)
        {
            case CommandKind.ForgetMe:
                await _db.DeleteUserProfileAsync(groupOpenId, senderOpenId);
                _logger.LogInformation("用户 {User} 已清除自己的画像", senderOpenId);
                return "好，我这就把你忘掉，就当从没认识过你 🌧️";

            case CommandKind.Status:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return await BuildStatusAsync(groupOpenId, botId);

            case CommandKind.FunStatus:
                // 随机互动状态（所有人可查看）
                return BuildFunStatusAsync();

            case CommandKind.AdminHelp:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return "管理员指令：\n/admin list 查看参数\n/admin set 参数 值\n/admin mute [分钟] / unmute\n/admin stats\n/admin reasoning 查看最后的思维链\n/admin admin add|remove id（本实例管理员，QQ 官方填 openid / OneBot 填 QQ 号）\n/admin forget 短id\n/status";

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
                if (error != null)
                {
                    return error;
                }
                string ok = $"参数 {command.Key} 已更新为 {command.Value} ☔";
                // 思维显示依赖调试模式：单开不生效，明确提示，避免"开了没反应"
                if (_config.Config.DebugShowReasoning && !_config.Config.DebugMode)
                {
                    ok += "\n注意：思维显示还需 DebugMode=true 才会生效（/admin set DebugMode true）";
                }
                return ok;

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

            case CommandKind.AdminReasoning:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return BuildReasoningView(groupOpenId);

            case CommandKind.AdminManage:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                if (string.IsNullOrWhiteSpace(command.Value))
                {
                    return "用法：/admin admin add|remove openid（QQ 官方实例填群内 openid，OneBot11 实例填 QQ 号）";
                }
                if (command.Key == "add")
                {
                    string? addError = await _botStore.AddAdminAsync(botId, command.Value);
                    return addError ?? $"已添加为本实例管理员：{command.Value} ☔";
                }
                string? removeError = await _botStore.RemoveAdminAsync(botId, command.Value);
                return removeError ?? $"已移除本实例管理员：{command.Value} ☔";

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

            case CommandKind.AdminSayNoList:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                return await BuildSayNoListAsync();

            case CommandKind.AdminSayNoUpdate:
                if (!isAdmin) return "这个指令只有管理员能用哦 🌧️";
                if (command.Key == null || command.Value == null)
                {
                    return "用法：/admin sayno 表名 add|remove 词（如 /admin sayno saynowords add 才不{0}）";
                }
                string[] op = command.Value.Split(' ', 2);
                bool isAdd = op[0] == "add";
                string word = op.Length > 1 ? op[1] : "";
                string? sayNoError = await _sayNoWords.UpdateAsync(command.Key, isAdd, word);
                return sayNoError ?? $"词表 {command.Key} 已{(isAdd ? "添加" : "移除")}：{word} ☔";

            default:
                return null;
        }
    }

    private async Task<string> BuildParamListAsync()
    {
        List<(string Key, string Value, bool Overridden)> items = await _config.ListAllAsync();
        var lines = items.Select(i =>
        {
            // 密钥类参数脱敏：解除输出截断后 /admin list 会完整输出，绝不能泄露 DeepSeek Key
            string value = i.Key == "Llm.ApiKey" && !string.IsNullOrWhiteSpace(i.Value)
                ? "••••••（已配置，不显示）"
                : i.Value;
            return i.Overridden ? $"⚙ {i.Key} = {value}（已修改）" : $"{i.Key} = {value}";
        });
        return string.Join('\n', lines);
    }

    private async Task<string> BuildStatsAsync(string groupOpenId)
    {
        double hitRate = await _db.GetCacheHitRateAsync(groupOpenId);
        GroupState state = _states.GetOrCreate(groupOpenId);
        return $"本群：消息 {state.TotalMessages} 条，缓存命中率 {hitRate:0.0%}，静默状态：{(state.Muted == true ? "已静默" : "正常")} ☔";
    }

    /// <summary>思维链查看字数上限（QQ 单条消息长度有限，超长截断）</summary>
    private const int MaxReasoningDisplayChars = 1200;

    /// <summary>查看本群最后一次模型思维链（调试模式记录）</summary>
    private string BuildReasoningView(string groupOpenId)
    {
        if (!_config.Config.DebugMode)
        {
            return "当前未开启调试模式（DebugMode），不会记录思维链。先用 /admin set DebugMode true 开启 ☔";
        }
        ReasoningSnapshot? snapshot = _reasoningRecorder.GetLast(groupOpenId);
        if (snapshot == null)
        {
            return "本群还没有记录到思维链（开启调试模式后等下一次触发 LLM 再查）🌧️";
        }
        string text = snapshot.Text.Length > MaxReasoningDisplayChars
            ? snapshot.Text[..MaxReasoningDisplayChars] + "…（已截断）"
            : snapshot.Text;
        return $"最后一次思维链（{snapshot.TimeUtc.ToLocalTime():MM-dd HH:mm:ss}，群 {AnchorManager.ShortId(snapshot.GroupOpenId)}，finish={snapshot.FinishReason ?? "unknown"}，output={snapshot.CompletionTokens} tokens）：\n{text}";
    }

    private async Task<string> BuildStatusAsync(string groupOpenId, string botId)
    {
        double hitRate = await _db.GetCacheHitRateAsync(groupOpenId);
        GroupState state = _states.GetOrCreate(groupOpenId);
        BotInstance? instance = _botStore.Get(botId);
        int adminCount = instance?.Admins.Count ?? 0;
        return $"机器人：{instance?.Name ?? botId} 🌧️\n本群消息 {state.TotalMessages} 条，缓存命中率 {hitRate:0.0%}\n本实例管理员 {adminCount} 人，静默：{(state.Muted == true ? "是" : "否")}，降级：{(state.Degraded ? "是" : "否")}";
    }

    private string BuildFunStatusAsync()
    {
        FunConfig fun = _config.Config.Fun;
        int osmCount = _osmCatalog.Images.Count;
        string osm = fun.EnableOsm
            ? (osmCount > 0 ? $"开启（{fun.OsmProbability}%，{osmCount} 张图）" : "开启（但未配置公网域名或无图片，自动禁用）")
            : "关闭";
        return $"随机互动状态：\n" +
            $"反驳是：{(fun.EnableReplyYes ? $"开启（{fun.ReplyYesProbability}%）" : "关闭")}\n" +
            $"反驳不：{(fun.EnableReplyNo ? $"开启（{fun.ReplyNoProbability}%，烂梗 {fun.ReplyNoMemeProbability}%）" : "关闭")}\n" +
            $"复读：{(fun.EnableRepeat ? $"开启（{fun.RepeatProbability}%，延迟 {fun.RepeatDelayMinSeconds}-{fun.RepeatDelayMaxSeconds}s）" : "关闭")}\n" +
            $"OSM：{osm}\n" +
            $"反向艾特：{(fun.EnableReverseAt ? $"开启（{fun.ReverseAtProbability}%）" : "关闭")}\n" +
            $"叫哥：{(fun.EnableCallBrother ? $"开启（{fun.CallBrotherProbability}%）" : "关闭")}";
    }

    /// <summary>SayNo 词表一览（管理员）</summary>
    private async Task<string> BuildSayNoListAsync()
    {
        var tables = _sayNoWords.AllTables;
        List<string> lines = [];
        foreach ((string name, string[] words) in tables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"[{name}] ({words.Length})");
            if (words.Length > 0)
            {
                lines.Add(string.Join("、", words));
            }
        }
        return "反驳不词表（文件：" + _sayNoWords.FilePathForDisplay + "）：\n" + string.Join('\n', lines);
    }
}

public enum CommandKind
{
    ForgetMe,
    Status,
    FunStatus,
    AdminHelp,
    AdminList,
    AdminSet,
    AdminMute,
    AdminUnmute,
    AdminStats,
    AdminReasoning,
    AdminManage,
    AdminForget,
    AdminSayNoList,
    AdminSayNoUpdate
}

public class ParsedCommand(CommandKind kind, string? key = null, string? value = null)
{
    public CommandKind Kind { get; } = kind;
    public string? Key { get; } = key;
    public string? Value { get; } = value;
}
