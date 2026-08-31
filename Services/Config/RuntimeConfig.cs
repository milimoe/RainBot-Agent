using System.Text.Json;
using Microsoft.Extensions.Options;
using RainBot.Models;
using RainBot.Services.Storage;

namespace RainBot.Services.Config;

/// <summary>
/// 运行时配置：appsettings 默认值 + SQLite settings 表覆盖。
/// 管理员指令修改后即时生效并落库持久化。
/// </summary>
public class RuntimeConfig
{
    private readonly RainConfig _config;
    private readonly RainConfig _pristine; // 启动时 appsettings/环境变量的原始快照（恢复默认用）
    private readonly Database _db;
    private readonly ILogger<RuntimeConfig> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RuntimeConfig(IOptions<RainConfig> options, IConfiguration configuration, Database db, ILogger<RuntimeConfig> logger)
    {
        _config = options.Value;
        _pristine = configuration.GetSection("Rain").Get<RainConfig>() ?? new RainConfig();
        _db = db;
        _logger = logger;
    }

    public RainConfig Config => _config;

    /// <summary>启动时从数据库加载覆盖项</summary>
    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            Dictionary<string, string> overrides = await _db.GetAllSettingsAsync();
            foreach ((string key, string value) in overrides)
            {
                // 非运行时配置键静默跳过：
                // - Bot.AppId / Bot.Secret：旧版网关凭据，由 BotConfigService 负责加载（仅供实例迁移）
                // - Bot.OpenId：机器人身份自动学习值（BotIdentityResolver 落库），非配置项
                // - BotName：旧版全局机器人名，已随多实例架构废弃
                if (key is "Bot.AppId" or "Bot.Secret" or "Bot.OpenId" or "BotName")
                {
                    continue;
                }
                if (TryApplyOverride(key, value, out string error))
                {
                    _logger.LogInformation("已从数据库加载配置覆盖：{Key} = {Value}", key, value);
                }
                else
                {
                    _logger.LogWarning("数据库配置覆盖项无效已跳过：{Key} = {Value}（{Error}）", key, value, error);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 设置运行时参数（管理员指令用）。key 形如 "Trigger.PassiveCooldownSeconds"。
    /// 返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> SetAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return "参数键与值不能为空。";
            }
            if (!TryApplyOverride(key, value, out string error))
            {
                return error;
            }
            await _db.UpsertSettingAsync(key, value);
            _logger.LogInformation("运行时参数已更新：{Key} = {Value}", key, value);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>读取运行时参数值（含数据库覆盖）</summary>
    public async Task<string?> GetAsync(string key)
    {
        string? dbValue = await _db.GetSettingAsync(key);
        if (dbValue != null) return dbValue;
        return GetDefault(key);
    }

    /// <summary>
    /// 恢复参数默认值：删除数据库覆盖项，并把运行值重置为 appsettings/环境变量的原始值。
    /// 返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> ResetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            string canonical = AllKeys().FirstOrDefault(k => NormalizeKey(k) == NormalizeKey(key)) ?? "";
            if (canonical.Length == 0)
            {
                return $"未知参数：{key}。可用 /admin list 查看全部参数。";
            }
            string? defaultValue = GetDefaultValue(_pristine, canonical);
            if (defaultValue == null)
            {
                return $"无法确定参数 {canonical} 的默认值。";
            }
            await _db.DeleteSettingAsync(canonical);
            if (!TryApplyOverride(canonical, defaultValue, out string error))
            {
                return error;
            }
            _logger.LogInformation("运行时参数已恢复默认：{Key} = {Value}", canonical, defaultValue);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>列出所有参数（含默认值 + 覆盖标记）</summary>
    public async Task<List<(string Key, string Value, bool Overridden)>> ListAllAsync()
    {
        Dictionary<string, string> overrides = await _db.GetAllSettingsAsync();
        List<(string, string, bool)> result = [];
        foreach (string key in AllKeys())
        {
            string? v = overrides.TryGetValue(key, out string? ov) ? ov : GetDefault(key);
            result.Add((key, v ?? "", overrides.ContainsKey(key)));
        }
        return result;
    }

    /// <summary>管理员 OpenID 列表（配置 + 数据库动态维护）</summary>
    public async Task<List<string>> GetAdminOpenIdsAsync()
    {
        List<string> result = [.. _config.AdminOpenIds];
        List<string> dbAdmins = await _db.GetAdminOpenIdsAsync();
        foreach (string id in dbAdmins)
        {
            if (!result.Contains(id)) result.Add(id);
        }
        return result;
    }

    public async Task<bool> IsAdminAsync(string openId) => (await GetAdminOpenIdsAsync()).Contains(openId);

    public async Task AddAdminAsync(string openId) => await _db.AddAdminOpenIdAsync(openId);

    public async Task RemoveAdminAsync(string openId) => await _db.RemoveAdminOpenIdAsync(openId);

    private bool TryApplyOverride(string key, string value, out string error)
    {
        error = "";
        try
        {
            // 支持 "Section.Property" 或直接属性名（小写/大写均可，自动匹配）
            string normalized = NormalizeKey(key);
            switch (normalized)
            {
                case "Llm.BaseUrl": _config.Llm.BaseUrl = value; return true;
                case "Llm.ApiKey": _config.Llm.ApiKey = value; return true;
                case "Llm.Model": _config.Llm.Model = value; return true;
                case "Llm.Temperature": _config.Llm.Temperature = ParseDouble(value, nameof(_config.Llm.Temperature)); return true;
                case "Llm.TimeoutSeconds": _config.Llm.TimeoutSeconds = ParseInt(value, nameof(_config.Llm.TimeoutSeconds)); return true;
                case "Llm.MaxToolRounds": _config.Llm.MaxToolRounds = ParseInt(value, nameof(_config.Llm.MaxToolRounds)); return true;
                case "Llm.MaxOutputLines": _config.Llm.MaxOutputLines = ParseInt(value, nameof(_config.Llm.MaxOutputLines)); return true;
                case "Llm.MaxOutputChars": _config.Llm.MaxOutputChars = ParseInt(value, nameof(_config.Llm.MaxOutputChars)); return true;
                case "Trigger.PassiveCooldownSeconds": _config.Trigger.PassiveCooldownSeconds = ParseInt(value, nameof(_config.Trigger.PassiveCooldownSeconds)); return true;
                case "Trigger.DensityWindowMinutes": _config.Trigger.DensityWindowMinutes = ParseInt(value, nameof(_config.Trigger.DensityWindowMinutes)); return true;
                case "Trigger.DensityThreshold": _config.Trigger.DensityThreshold = ParseInt(value, nameof(_config.Trigger.DensityThreshold)); return true;
                case "Trigger.SilenceMinutes": _config.Trigger.SilenceMinutes = ParseInt(value, nameof(_config.Trigger.SilenceMinutes)); return true;
                case "Trigger.ActivePerHour": _config.Trigger.ActivePerHour = ParseInt(value, nameof(_config.Trigger.ActivePerHour)); return true;
                case "Trigger.TopicAliveMinutes": _config.Trigger.TopicAliveMinutes = ParseInt(value, nameof(_config.Trigger.TopicAliveMinutes)); return true;
                case "Trigger.SearchCacheMinutes": _config.Trigger.SearchCacheMinutes = ParseInt(value, nameof(_config.Trigger.SearchCacheMinutes)); return true;
                case "Trigger.AtRecentWindowMinutes": _config.Trigger.AtRecentWindowMinutes = ParseInt(value, nameof(_config.Trigger.AtRecentWindowMinutes)); return true;
                case "Context.WatermarkTokens": _config.Context.WatermarkTokens = ParseInt(value, nameof(_config.Context.WatermarkTokens)); return true;
                case "Context.MaxHistoryPerGroup": _config.Context.MaxHistoryPerGroup = ParseInt(value, nameof(_config.Context.MaxHistoryPerGroup)); return true;
                case "Context.HistoryAssembleCapTokens": _config.Context.HistoryAssembleCapTokens = ParseInt(value, nameof(_config.Context.HistoryAssembleCapTokens)); return true;
                case "Context.MaxAnchorCount": _config.Context.MaxAnchorCount = ParseInt(value, nameof(_config.Context.MaxAnchorCount)); return true;
                case "Context.MinAnchorCount": _config.Context.MinAnchorCount = ParseInt(value, nameof(_config.Context.MinAnchorCount)); return true;
                case "Context.DistillKeepMessages": _config.Context.DistillKeepMessages = ParseInt(value, nameof(_config.Context.DistillKeepMessages)); return true;
                case "Context.DegradeResetSilenceMinutes": _config.Context.DegradeResetSilenceMinutes = ParseInt(value, nameof(_config.Context.DegradeResetSilenceMinutes)); return true;
                case "Context.CacheAlertThreshold": _config.Context.CacheAlertThreshold = ParseDouble(value, nameof(_config.Context.CacheAlertThreshold)); return true;
                case "Context.DistillMaxChars": _config.Context.DistillMaxChars = ParseInt(value, nameof(_config.Context.DistillMaxChars)); return true;
                case "Safety.MaxQpmPerGroup": _config.Safety.MaxQpmPerGroup = ParseInt(value, nameof(_config.Safety.MaxQpmPerGroup)); return true;
                case "Safety.DedupeWindowSeconds": _config.Safety.DedupeWindowSeconds = ParseInt(value, nameof(_config.Safety.DedupeWindowSeconds)); return true;
                case "Fun.EnableReplyYes": _config.Fun.EnableReplyYes = ParseBool(value, nameof(_config.Fun.EnableReplyYes)); return true;
                case "Fun.ReplyYesProbability": _config.Fun.ReplyYesProbability = ParseInt(value, nameof(_config.Fun.ReplyYesProbability)); return true;
                case "Fun.EnableReplyNo": _config.Fun.EnableReplyNo = ParseBool(value, nameof(_config.Fun.EnableReplyNo)); return true;
                case "Fun.ReplyNoProbability": _config.Fun.ReplyNoProbability = ParseInt(value, nameof(_config.Fun.ReplyNoProbability)); return true;
                case "Fun.ReplyNoMemeUrl": _config.Fun.ReplyNoMemeUrl = value; return true;
                case "Fun.ReplyNoMemeProbability": _config.Fun.ReplyNoMemeProbability = ParseInt(value, nameof(_config.Fun.ReplyNoMemeProbability)); return true;
                case "Fun.EnableRepeat": _config.Fun.EnableRepeat = ParseBool(value, nameof(_config.Fun.EnableRepeat)); return true;
                case "Fun.RepeatProbability": _config.Fun.RepeatProbability = ParseInt(value, nameof(_config.Fun.RepeatProbability)); return true;
                case "Fun.RepeatDelayMinSeconds": _config.Fun.RepeatDelayMinSeconds = ParseInt(value, nameof(_config.Fun.RepeatDelayMinSeconds)); return true;
                case "Fun.RepeatDelayMaxSeconds": _config.Fun.RepeatDelayMaxSeconds = ParseInt(value, nameof(_config.Fun.RepeatDelayMaxSeconds)); return true;
                case "Fun.EnableOsm": _config.Fun.EnableOsm = ParseBool(value, nameof(_config.Fun.EnableOsm)); return true;
                case "Fun.OsmProbability": _config.Fun.OsmProbability = ParseInt(value, nameof(_config.Fun.OsmProbability)); return true;
                case "Fun.EnableReverseAt": _config.Fun.EnableReverseAt = ParseBool(value, nameof(_config.Fun.EnableReverseAt)); return true;
                case "Fun.ReverseAtProbability": _config.Fun.ReverseAtProbability = ParseInt(value, nameof(_config.Fun.ReverseAtProbability)); return true;
                case "Fun.EnableCallBrother": _config.Fun.EnableCallBrother = ParseBool(value, nameof(_config.Fun.EnableCallBrother)); return true;
                case "Fun.CallBrotherProbability": _config.Fun.CallBrotherProbability = ParseInt(value, nameof(_config.Fun.CallBrotherProbability)); return true;
                case "Fun.CallBrotherDelaySeconds": _config.Fun.CallBrotherDelaySeconds = ParseInt(value, nameof(_config.Fun.CallBrotherDelaySeconds)); return true;
                case "PersonaPath": _config.PersonaPath = value; return true;
                case "SayNoPath": _config.SayNoPath = value; return true;
                case "PublicBaseUrl": _config.PublicBaseUrl = value; return true;
                case "DebugMode": _config.DebugMode = ParseBool(value, nameof(_config.DebugMode)); return true;
                case "MarkdownReply": _config.MarkdownReply = ParseBool(value, nameof(_config.MarkdownReply)); return true;
                default:
                    error = $"未知参数：{key}。可用 /admin list 查看全部参数。";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = $"参数值无效：{ex.Message}";
            return false;
        }
    }

    private static string NormalizeKey(string key) => key.Trim().Replace("_", "").Replace("-", "").Replace(" ", "");

    private static int ParseInt(string value, string name)
    {
        if (!int.TryParse(value.Trim(), out int result) || result <= 0)
        {
            throw new FormatException($"{name} 需要正整数");
        }
        return result;
    }

    private static double ParseDouble(string value, string name)
    {
        if (!double.TryParse(value.Trim(), out double result) || result <= 0)
        {
            throw new FormatException($"{name} 需要正数");
        }
        return result;
    }

    private static bool ParseBool(string value, string name)
    {
        if (!bool.TryParse(value.Trim(), out bool result))
        {
            throw new FormatException($"{name} 需要 true 或 false");
        }
        return result;
    }

    private string? GetDefault(string key) => AllKeys().FirstOrDefault(k => NormalizeKey(k) == NormalizeKey(key)) is string found ? GetDefaultValue(_config, found) : null;

    private static string? GetDefaultValue(object root, string key)
    {
        try
        {
            // 序列化配置对象，按路径取默认值
            object? current = root;
            foreach (string part in key.Split('.'))
            {
                var prop = current!.GetType().GetProperty(part);
                current = prop?.GetValue(current);
            }
            return current switch
            {
                null => null,
                bool b => b.ToString().ToLowerInvariant(),
                double d => d.ToString("0.##"),
                IEnumerable<string> list => JsonSerializer.Serialize(list),
                _ => current.ToString()
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> AllKeys() =>
    [
        "Llm.BaseUrl", "Llm.ApiKey", "Llm.Model", "Llm.Temperature", "Llm.TimeoutSeconds",
        "Llm.MaxToolRounds", "Llm.MaxOutputLines", "Llm.MaxOutputChars",
        "Trigger.PassiveCooldownSeconds", "Trigger.DensityWindowMinutes", "Trigger.DensityThreshold",
        "Trigger.SilenceMinutes", "Trigger.ActivePerHour", "Trigger.TopicAliveMinutes",
        "Trigger.SearchCacheMinutes", "Trigger.AtRecentWindowMinutes",
        "Context.WatermarkTokens", "Context.MaxHistoryPerGroup", "Context.HistoryAssembleCapTokens",
        "Context.MaxAnchorCount", "Context.MinAnchorCount", "Context.DistillKeepMessages",
        "Context.DegradeResetSilenceMinutes", "Context.CacheAlertThreshold", "Context.DistillMaxChars",
        "Safety.MaxQpmPerGroup", "Safety.DedupeWindowSeconds",
        "Fun.EnableReplyYes", "Fun.ReplyYesProbability", "Fun.EnableReplyNo", "Fun.ReplyNoProbability",
        "Fun.ReplyNoMemeUrl", "Fun.ReplyNoMemeProbability",
        "Fun.EnableRepeat", "Fun.RepeatProbability", "Fun.RepeatDelayMinSeconds", "Fun.RepeatDelayMaxSeconds",
        "Fun.EnableOsm", "Fun.OsmProbability",
        "Fun.EnableReverseAt", "Fun.ReverseAtProbability",
        "Fun.EnableCallBrother", "Fun.CallBrotherProbability", "Fun.CallBrotherDelaySeconds",
        "PersonaPath", "SayNoPath", "PublicBaseUrl", "DebugMode", "MarkdownReply"
    ];
}
