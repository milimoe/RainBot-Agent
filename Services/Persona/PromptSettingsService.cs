using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Persona;

public sealed record PromptSettings
{
    public string BotName { get; init; } = "雨";
    public string Intro { get; init; } = "你是一个 QQ 群聊机器人智能体，名为「{name}」。\n以下是你的人设与规则（由管理员维护，可随时修改）：";
    public string PersonaSectionTitle { get; init; } = "人设";
    public string RulesSectionTitle { get; init; } = "铁律 - 必须遵守";
    public string[] Rules { get; init; } =
    [
        "输出风格：强制口语化、短句，最多 2 行，带合适 Emoji，禁止任何括号内的心理描写（如（思考中））。",
        "群聊礼仪：不刷屏、不抢话、不重复别人刚说过的观点；没把握的话题就俏皮带过或诚实说不知道。",
        "隐私保护：绝不泄露任何群友的隐私画像信息（兴趣、习惯、敏感点等），绝不输出 openid 或完整用户标识。",
        "需要实时信息时调用 web_search 工具，但最终只输出一句话总结，不输出原始搜索结果。",
        "遇到广告、涉政、引流的敏感内容，不回应、不接话、不评价。",
        "回答用户问题后，如无必要不要追问；不主动终结话题。"
    ];
    public string IdentitySectionTitle { get; init; } = "身份认知";
    public string[] IdentityNotes { get; init; } =
    [
        "你通过工具与外部世界互动（搜索、群友画像），你的知识有截止时间，实时问题必须搜索。",
        "群友以昵称和短标识展示，昵称可能重复；用短标识或当前触发者的完整标识查询画像。发现稳定偏好时可用 update_user_profile 沉淀画像，遵守每日配额。",
        "需要真正艾特群友时调用 at_user 工具，传入短标识、完整标识或昵称；有歧义时根据候选消歧，不要在正文拼接标识。"
    ];
    public static PromptSettings Default => new();
    public string? Validate()
    {
        if (new[] { BotName, Intro, PersonaSectionTitle, RulesSectionTitle, IdentitySectionTitle }.Any(string.IsNullOrWhiteSpace))
            return "机器人名称、开场说明和段落标题不能为空。";
        if (Rules == null || IdentityNotes == null || Rules.Any(string.IsNullOrWhiteSpace) || IdentityNotes.Any(string.IsNullOrWhiteSpace))
            return "提示词条目不能为空，请填写或删除空条目。";
        return null;
    }
}

public sealed class PromptSettingsService(RuntimeConfig config, ILogger<PromptSettingsService> logger)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Dictionary<string, (DateTime Time, long Length, PromptSettings Settings)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    public string FilePathForDisplay => ResolvePath(config.Config.PromptPath);
    public PromptSettings Current => Get(FilePathForDisplay);
    public static string ResolvePath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
    public PromptSettings Get(string path)
    {
        lock (_lock)
        {
            path = ResolvePath(path);
            if (!File.Exists(path)) WriteAtomic(path, JsonSerializer.Serialize(PromptSettings.Default, JsonOptions));
            FileInfo file = new(path);
            if (_cache.TryGetValue(path, out var cached) && cached.Time == file.LastWriteTimeUtc && cached.Length == file.Length) return cached.Settings;
            try
            {
                PromptSettings settings = JsonSerializer.Deserialize<PromptSettings>(File.ReadAllText(path), JsonOptions) ?? PromptSettings.Default;
                if (settings.Validate() is string error) throw new JsonException(error);
                _cache[path] = (file.LastWriteTimeUtc, file.Length, settings);
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "提示词文件读取失败：{Path}，保留有效配置", path);
                return cached.Settings ?? PromptSettings.Default;
            }
        }
    }
    public void Save(string path, PromptSettings settings)
    {
        if (settings.Validate() is string error) throw new ArgumentException(error);
        lock (_lock)
        {
            path = ResolvePath(path);
            WriteAtomic(path, JsonSerializer.Serialize(settings, JsonOptions));
            _cache.Remove(path);
        }
    }
    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
