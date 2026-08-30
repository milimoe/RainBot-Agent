using System.Text.Json;
using System.Text.Json.Serialization;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.Bots;

/// <summary>
/// 机器人实例注册表：以 SQLite bot_instances 表为准（WebUI 可热增删改、启停），
/// 首次运行为空时用 Rain:Bots 配置段做种子；若连种子都没有，则把旧版 Bot 段（AppId/Secret）
/// 迁移为默认实例 qq，保证老部署升级后数据仍然对得上。
/// </summary>
public class BotInstanceStore
{
    private readonly Database _db;
    private readonly RuntimeConfig _config;
    private readonly BotConfigService _botConfig;
    private readonly ILogger<BotInstanceStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<BotInstance> _instances = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public BotInstanceStore(Database db, RuntimeConfig config, BotConfigService botConfig, ILogger<BotInstanceStore> logger)
    {
        _db = db;
        _config = config;
        _botConfig = botConfig;
        _logger = logger;
    }

    /// <summary>全部实例（按 Id 排序）</summary>
    public IReadOnlyList<BotInstance> All => _instances;

    /// <summary>实例增删改/启停事件（网关管理器订阅后重建连接）</summary>
    public event Action? InstancesChanged;

    /// <summary>启动时加载；为空则播种</summary>
    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await ReloadLockedAsync();
            if (_instances.Count == 0)
            {
                await SeedLockedAsync();
                await ReloadLockedAsync();
            }
            _logger.LogInformation("机器人实例已加载：{Count} 个（{Ids}）",
                _instances.Count, string.Join(", ", _instances.Select(i => $"{i.Id}[{i.Platform}]")));
        }
        finally
        {
            _lock.Release();
        }
    }

    public BotInstance? Get(string id) => _instances.FirstOrDefault(i => i.Id == id);

    public IReadOnlyList<BotInstance> EnabledInstances => _instances.Where(i => i.Enabled).ToList();

    /// <summary>新增或更新实例（校验后落库）</summary>
    public async Task<string?> UpsertAsync(BotInstance instance)
    {
        string? error = Validate(instance);
        if (error != null)
        {
            return error;
        }
        await _lock.WaitAsync();
        try
        {
            await _db.UpsertBotInstanceAsync(instance.Id, instance.Name, instance.Platform.ToString(), instance.Enabled, SerializeConfig(instance));
            await ReloadLockedAsync();
        }
        finally
        {
            _lock.Release();
        }
        _logger.LogInformation("机器人实例已保存：{Id}（{Platform}）", instance.Id, instance.Platform);
        InstancesChanged?.Invoke();
        return null;
    }

    /// <summary>删除实例（历史数据保留）</summary>
    public async Task<string?> DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            if (_instances.All(i => i.Id != id))
            {
                return $"实例不存在：{id}";
            }
            await _db.DeleteBotInstanceAsync(id);
            await ReloadLockedAsync();
        }
        finally
        {
            _lock.Release();
        }
        _logger.LogInformation("机器人实例已删除：{Id}", id);
        InstancesChanged?.Invoke();
        return null;
    }

    /// <summary>启用/禁用实例</summary>
    public async Task<string?> SetEnabledAsync(string id, bool enabled)
    {
        BotInstance? existing = Get(id);
        if (existing == null)
        {
            return $"实例不存在：{id}";
        }
        existing.Enabled = enabled;
        return await UpsertAsync(existing);
    }

    /// <summary>
    /// 记录机器人自身身份（QQ 官方 = 群内 openid；OneBot = 机器人 QQ 号）。
    /// 从事件中自动学习后调用，已存在则不重复写库。
    /// </summary>
    public async Task LearnSelfIdentityAsync(string botId, string selfId)
    {
        if (string.IsNullOrWhiteSpace(selfId))
        {
            return;
        }
        BotInstance? instance = Get(botId);
        if (instance == null)
        {
            return;
        }

        bool changed = false;
        if (instance.Platform == BotPlatform.QqOfficial && string.IsNullOrWhiteSpace(instance.Qq.SelfOpenId))
        {
            instance.Qq.SelfOpenId = selfId;
            changed = true;
        }
        else if (instance.Platform == BotPlatform.OneBot11 && string.IsNullOrWhiteSpace(instance.OneBot.SelfQq))
        {
            instance.OneBot.SelfQq = selfId;
            changed = true;
        }
        if (!changed)
        {
            return;
        }

        await _db.UpsertBotInstanceAsync(instance.Id, instance.Name, instance.Platform.ToString(), instance.Enabled, SerializeConfig(instance));
        _logger.LogInformation("实例 {BotId} 已自动学习自身身份：{SelfId}", botId, selfId);
    }

    // ---------- 内部 ----------

    private async Task ReloadLockedAsync()
    {
        List<BotInstanceRow> rows = await _db.GetBotInstancesAsync();
        List<BotInstance> list = [];
        foreach (BotInstanceRow row in rows)
        {
            BotInstance instance = new()
            {
                Id = row.Id,
                Name = row.Name,
                Platform = Enum.TryParse(row.Platform, true, out BotPlatform p) ? p : BotPlatform.QqOfficial,
                Enabled = row.Enabled
            };
            try
            {
                BotInstanceConfigDto? dto = JsonSerializer.Deserialize<BotInstanceConfigDto>(row.ConfigJson, JsonOptions);
                if (dto != null)
                {
                    instance.PersonaPath = dto.PersonaPath ?? "";
                    instance.Qq = dto.Qq ?? new QqOfficialConfig();
                    instance.OneBot = dto.OneBot ?? new OneBotConfig();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "实例 {Id} 配置 JSON 解析失败，使用默认配置", row.Id);
            }
            list.Add(instance);
        }
        _instances = list;
    }

    /// <summary>首次运行播种：优先 Rain:Bots 配置段，否则从旧版 Bot 段迁移</summary>
    private async Task SeedLockedAsync()
    {
        List<BotInstance> seeds = _config.Config.Bots;
        if (seeds.Count > 0)
        {
            foreach (BotInstance seed in seeds)
            {
                await _db.UpsertBotInstanceAsync(seed.Id, seed.Name, seed.Platform.ToString(), seed.Enabled, SerializeConfig(seed));
            }
            _logger.LogInformation("已从配置段播种 {Count} 个机器人实例", seeds.Count);
            return;
        }

        // 旧版迁移：单 QQ 官方机器人 → 默认实例 qq
        BotConfig legacy = _botConfig.Current;
        BotInstance migrated = new()
        {
            Id = Database.LegacyBotId,
            Name = "QQ 官方机器人",
            Platform = BotPlatform.QqOfficial,
            Enabled = !string.IsNullOrWhiteSpace(legacy.AppId) && !string.IsNullOrWhiteSpace(legacy.Secret),
            Qq = new QqOfficialConfig
            {
                AppId = legacy.AppId,
                Secret = legacy.Secret,
                UseSandbox = legacy.UseSandbox
            }
        };
        await _db.UpsertBotInstanceAsync(migrated.Id, migrated.Name, migrated.Platform.ToString(), migrated.Enabled, SerializeConfig(migrated));
        _logger.LogInformation("已迁移旧版 QQ 网关凭据为默认实例 {Id}", migrated.Id);
    }

    private static string SerializeConfig(BotInstance instance) => JsonSerializer.Serialize(new BotInstanceConfigDto
    {
        PersonaPath = instance.PersonaPath,
        Qq = instance.Qq,
        OneBot = instance.OneBot
    }, JsonOptions);

    private static string? Validate(BotInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Id))
        {
            return "实例 Id 不能为空。";
        }
        if (instance.Id.Contains(':'))
        {
            return "实例 Id 不能包含冒号（: 用作群键分隔符）。";
        }
        if (!instance.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return "实例 Id 只能包含字母、数字、下划线与连字符。";
        }
        if (instance.Name.Length == 0)
        {
            instance.Name = instance.Id;
        }
        if (instance.Platform == BotPlatform.QqOfficial && string.IsNullOrWhiteSpace(instance.Qq.AppId) && instance.Enabled)
        {
            return "QQ 官方实例启用时必须填写 AppId。";
        }
        if (instance.Platform == BotPlatform.OneBot11 && instance.Enabled
            && !instance.OneBot.Http.Enabled && !instance.OneBot.WsForward.Enabled && !instance.OneBot.WsReverse.Enabled)
        {
            return "OneBot11 实例启用时至少要开启一条通道（HTTP / WS 正向 / WS 反向）。";
        }
        return null;
    }

    private sealed class BotInstanceConfigDto
    {
        public string? PersonaPath { get; set; }
        public QqOfficialConfig? Qq { get; set; }
        public OneBotConfig? OneBot { get; set; }
    }
}
