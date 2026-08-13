using Microsoft.Extensions.Caching.Memory;
using RainBot.Models;
using RainBot.Services.Storage;

namespace RainBot.Services.Config;

/// <summary>
/// QQ 网关凭据（AppID / Secret）运行时服务：
/// - 初始值来自配置（appsettings "Bot" 段 / 环境变量 BOT__APPID / BOT__SECRET）；
/// - WebUI 维护后落库（settings 表 Bot.AppId / Bot.Secret）并即时生效；
/// - 修改时触发 CredentialsChanged 事件：WebSocket 服务断开现有连接，用新凭据立即重连，
///   同时清空 Access Token 缓存。
/// </summary>
public class BotConfigService
{
    public const string AccessTokenCacheKey = "QQBotAccessToken";

    private readonly BotConfig _config;
    private readonly Database _db;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<BotConfigService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BotConfigService(IConfiguration configuration, Database db, IMemoryCache memoryCache, ILogger<BotConfigService> logger)
    {
        _config = configuration.GetSection("Bot").Get<BotConfig>() ?? new BotConfig();
        _db = db;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    /// <summary>凭据变更事件（订阅者应断开现有网关连接）</summary>
    public event Action? CredentialsChanged;

    /// <summary>当前生效的 Bot 配置（AppId/Secret 可变，读取方应每次访问 Current）</summary>
    public BotConfig Current => _config;

    /// <summary>启动时加载数据库中的凭据覆盖</summary>
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
                _logger.LogInformation("已从数据库加载 QQ 网关凭据覆盖");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>当前凭据（供 WebUI 展示与编辑）</summary>
    public async Task<(string AppId, string Secret, bool Overridden)> GetCredentialsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            bool overridden = await _db.GetSettingAsync("Bot.AppId") != null || await _db.GetSettingAsync("Bot.Secret") != null;
            return (_config.AppId, _config.Secret, overridden);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 更新 AppID/Secret：落库、清 Token 缓存、触发断线重连。
    /// 返回 null 表示成功，否则为错误信息。
    /// </summary>
    public async Task<string?> SetCredentialsAsync(string appId, string secret)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
        {
            return "AppID 与 Secret 不能为空。";
        }
        appId = appId.Trim();
        secret = secret.Trim();

        await _lock.WaitAsync();
        try
        {
            _config.AppId = appId;
            _config.Secret = secret;
            await _db.UpsertSettingAsync("Bot.AppId", appId);
            await _db.UpsertSettingAsync("Bot.Secret", secret);
            _memoryCache.Remove(AccessTokenCacheKey);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("QQ 网关凭据已更新，断开现有连接并用新凭据重连…");
        CredentialsChanged?.Invoke();
        return null;
    }
}
