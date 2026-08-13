using System.Text.Encodings.Web;
using System.Text.Json;
using RainBot.Services.Config;

namespace RainBot.Services.Fun;

/// <summary>
/// 随机反驳不词表（移植自原版 RainBOT SayNo 系统，JSON 文件配置 + 热更新）。
/// - 词表存放于 JSON 文件（默认 sayno.json，路径 Rain.SayNoPath），字段名与原版 PluginConfig 一致；
/// - 首次运行自动生成内置默认词表文件，方便直接编辑；
/// - 热重载：编辑保存后自动生效（检测文件修改时间），无需重启；
/// - 支持管理员指令（/admin sayno）增删词，改动写回 JSON 持久化。
/// </summary>
public class SayNoWordsService(RuntimeConfig config, ILogger<SayNoWordsService> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<SayNoWordsService> _logger = logger;
    private readonly Lock _lock = new();
    private SayNoWordSet _current = SayNoWordSet.Default;
    private DateTime _lastWriteTime = DateTime.MinValue;
    private bool _loaded;

    /// <summary>JSON 序列化选项：缩进 + 不转义中文（保证手工可编辑）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>词表文件路径（相对路径解析到运行目录）</summary>
    private string FilePath
    {
        get
        {
            string path = _config.Config.SayNoPath;
            return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        }
    }

    /// <summary>
    /// 当前词表快照（每次访问检测文件修改，自动热重载）。
    /// </summary>
    public SayNoWordSet Current
    {
        get
        {
            string filePath = FilePath;
            lock (_lock)
            {
                DateTime lastWrite = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
                if (!_loaded || lastWrite != _lastWriteTime)
                {
                    _current = File.Exists(filePath) ? LoadFromFile(filePath) : EnsureDefaultFile(filePath);
                    _lastWriteTime = File.GetLastWriteTimeUtc(filePath);
                    _loaded = true;
                    _logger.LogInformation("SayNo 词表已加载/热重载：{Path}", filePath);
                }
                return _current;
            }
        }
    }

    /// <summary>全部词表（供指令展示）</summary>
    public IReadOnlyDictionary<string, string[]> AllTables => Current.ToDictionary();

    /// <summary>词表文件路径（供指令展示）</summary>
    public string FilePathForDisplay => FilePath;

    /// <summary>
    /// 增删词并写回 JSON（管理员指令用）。
    /// part 为表名（大小写不敏感，与原版一致）；返回 null 成功，否则错误信息。
    /// </summary>
    public async Task<string?> UpdateAsync(string part, bool add, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "词不能为空。";
        }
        lock (_lock)
        {
            SayNoWordSet current = Current;
            if (!SayNoWordSet.TryUpdate(ref current, part, add, value.Trim(), out string? error))
            {
                return error;
            }
            string filePath = FilePath;
            string json = JsonSerializer.Serialize(current, JsonOptions);
            // 原子写：临时文件 + 重命名，避免热重载读到半截内容
            string tmp = filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, filePath, overwrite: true);
            _current = current;
            _lastWriteTime = File.GetLastWriteTimeUtc(filePath);
            _loaded = true;
        }
        _logger.LogInformation("SayNo 词表已更新：{Part} {Op} {Value}", part, add ? "添加" : "移除", value);
        return null;
    }

    private static SayNoWordSet LoadFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        SayNoWordSet? set = JsonSerializer.Deserialize<SayNoWordSet>(json);
        return set ?? SayNoWordSet.Default;
    }

    private static SayNoWordSet EnsureDefaultFile(string filePath)
    {
        // 首次运行：生成默认词表文件，方便用户按格式编辑
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string json = JsonSerializer.Serialize(SayNoWordSet.Default, JsonOptions);
        File.WriteAllText(filePath, json);
        return SayNoWordSet.Default;
    }
}

/// <summary>
/// SayNo 词表快照（不可变 record，字段名与原版 PluginConfig 一致）。
/// </summary>
public sealed record SayNoWordSet
{
    public string[] Trigger { get; init; } = [];
    public string[] TriggerBeforeNo { get; init; } = [];
    public string[] IgnoreTriggerAfterNo { get; init; } = [];
    public string[] IgnoreTriggerBeforeCan { get; init; } = [];
    public string[] TriggerAfterYes { get; init; } = [];
    public string[] WillNotSayNo { get; init; } = [];
    public string[] SayNoWords { get; init; } = [];
    public string[] SayDontHaveWords { get; init; } = [];
    public string[] SayNotYesWords { get; init; } = [];
    public string[] SayDontWords { get; init; } = [];
    public string[] SayWantWords { get; init; } = [];
    public string[] SayThinkWords { get; init; } = [];
    public string[] SaySpecialNoWords { get; init; } = [];

    /// <summary>内置默认词表（与原版语义一致）</summary>
    public static SayNoWordSet Default => new()
    {
        Trigger = ["不", "没", "是", "别"],
        TriggerBeforeNo = ["太"],
        IgnoreTriggerAfterNo = ["要", "能", "是", "会", "想", "该", "应该"],
        IgnoreTriggerBeforeCan = ["只"],
        TriggerAfterYes = ["吗", "吧", "嘛", "啊", "么"],
        WillNotSayNo = ["别", "不要"],
        SayNoWords = ["不{0}", "就不{0}", "{0}你个头", "说不{0}也没用"],
        SayDontHaveWords = ["有{0}啊", "其实有{0}", "才没有{0}"],
        SayNotYesWords = ["才不是呢", "是吗？我不信", "对对对，你说的都对"],
        SayDontWords = ["就要{0}", "我偏要{0}", "{0}？我偏不"],
        SayWantWords = ["才不要", "不想", "要什么要"],
        SayThinkWords = ["才没想", "想什么呢", "不想"],
        SaySpecialNoWords = ["不", "才不", "就"]
    };

    /// <summary>全部表（表名 → 词列表，供展示与指令分发）</summary>
    public Dictionary<string, string[]> ToDictionary() => new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(Trigger)] = Trigger,
        [nameof(TriggerBeforeNo)] = TriggerBeforeNo,
        [nameof(IgnoreTriggerAfterNo)] = IgnoreTriggerAfterNo,
        [nameof(IgnoreTriggerBeforeCan)] = IgnoreTriggerBeforeCan,
        [nameof(TriggerAfterYes)] = TriggerAfterYes,
        [nameof(WillNotSayNo)] = WillNotSayNo,
        [nameof(SayNoWords)] = SayNoWords,
        [nameof(SayDontHaveWords)] = SayDontHaveWords,
        [nameof(SayNotYesWords)] = SayNotYesWords,
        [nameof(SayDontWords)] = SayDontWords,
        [nameof(SayWantWords)] = SayWantWords,
        [nameof(SayThinkWords)] = SayThinkWords,
        [nameof(SaySpecialNoWords)] = SaySpecialNoWords
    };

    /// <summary>
    /// 按表名（大小写不敏感）增删词，通过 with 生成新快照。
    /// </summary>
    public static bool TryUpdate(ref SayNoWordSet set, string part, bool add, string value, out string? error)
    {
        error = null;
        List<string> Modify(string[] source)
        {
            List<string> list = [.. source];
            if (add)
            {
                if (!list.Contains(value, StringComparer.Ordinal))
                {
                    list.Add(value);
                }
            }
            else
            {
                list.Remove(value);
            }
            return list;
        }
        SayNoWordSet updated;
        switch (part.Trim().ToLowerInvariant())
        {
            case "trigger": updated = set with { Trigger = [.. Modify(set.Trigger)] }; break;
            case "triggerbeforeno": updated = set with { TriggerBeforeNo = [.. Modify(set.TriggerBeforeNo)] }; break;
            case "ignoretriggerafterno": updated = set with { IgnoreTriggerAfterNo = [.. Modify(set.IgnoreTriggerAfterNo)] }; break;
            case "ignoretriggerbeforecan": updated = set with { IgnoreTriggerBeforeCan = [.. Modify(set.IgnoreTriggerBeforeCan)] }; break;
            case "triggerafteryes": updated = set with { TriggerAfterYes = [.. Modify(set.TriggerAfterYes)] }; break;
            case "willnotsayno": updated = set with { WillNotSayNo = [.. Modify(set.WillNotSayNo)] }; break;
            case "saynowords": updated = set with { SayNoWords = [.. Modify(set.SayNoWords)] }; break;
            case "saydonthavewords": updated = set with { SayDontHaveWords = [.. Modify(set.SayDontHaveWords)] }; break;
            case "saynotyeswords": updated = set with { SayNotYesWords = [.. Modify(set.SayNotYesWords)] }; break;
            case "saydontwords": updated = set with { SayDontWords = [.. Modify(set.SayDontWords)] }; break;
            case "saywantwords": updated = set with { SayWantWords = [.. Modify(set.SayWantWords)] }; break;
            case "saythinkwords": updated = set with { SayThinkWords = [.. Modify(set.SayThinkWords)] }; break;
            case "sayspecialnowords": updated = set with { SaySpecialNoWords = [.. Modify(set.SaySpecialNoWords)] }; break;
            default:
                error = $"未知词表：{part}。可用表名：trigger、triggerbeforeno、ignoretriggerafterno、ignoretriggerbeforecan、triggerafteryes、willnotsayno、saynowords、saydonthavewords、saynotyeswords、saydontwords、saywantwords、saythinkwords、sayspecialnowords";
                return false;
        }
        set = updated;
        return true;
    }
}
