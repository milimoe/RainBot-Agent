using Microsoft.Extensions.Caching.Memory;
using RainBot.Models;
using RainBot.Services.Storage;

namespace RainBot.Services.Config;

/// <summary>
/// 旧版全局 QQ 网关凭据服务（仅作兼容与迁移用，多实例架构下凭据按实例维护于「机器人」页）：
/// - 读取 appsettings "Bot" 段 / 环境变量 BOT__APPID / BOT__SECRET 及旧版 WebUI 落库的
///   settings 表覆盖（Bot.AppId / Bot.Secret）；
/// - 供 BotInstanceStore 首次运行时把旧版单机器人凭据迁移为默认实例 qq；
/// - 供 QQBotService 作为实例凭据缺失时的兼容回落。
/// 新部署请直接在 WebUI「机器人」页维护各实例凭据，本服务不再提供写入入口。
/// </summary>
public class BotConfigService
{
    public const string AccessTokenCacheKey = "QQBotAccessToken";

    private readonly BotConfig _config;
    private readonly Database _db;
    private readonly ILogger<BotConfigService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BotConfigService(IConfiguration configuration, Database db, ILogger<BotConfigService> logger)
    {
        _config = configuration.GetSection("Bot").Get<BotConfig>() ?? new BotConfig();
        _db = db;
        _logger = logger;
    }

    /// <summary>当前生效的旧版全局凭据（只读，迁移与兼容回落用）</summary>
    public BotConfig Current => _config;

    /// <summary>启动时加载数据库中的旧版凭据覆盖（旧版 WebUI 写入，仅供迁移）</summary>
    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            string? appId = await _db.GetSettingAsync("Bot.AppId");
            string? secret = await _db.GetSettingAsync("Bot.Secret");
            if (!string.IsNullOrWhiteSpace(appId) || !string.IsNullOrWhiteSpace(secret))
            {
                if (!string.IsNullOrWhiteSpace(appId)) _config.AppId = appId;
                if (!string.IsNullOrWhiteSpace(secret)) _config.Secret = secret;
                _logger.LogInformation("已从数据库加载旧版 QQ 网关凭据覆盖（仅用于实例迁移）");
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
