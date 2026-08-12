using System.Text.RegularExpressions;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.QQ;

/// <summary>
/// 机器人身份识别：判断群消息是否 @ 了机器人。
/// 
/// 背景：GROUP_AT_MESSAGE_CREATE 事件仅在 @ 机器人/回复机器人时推送（无需判断）；
/// 但全量模式 GROUP_MESSAGE_CREATE 会推送所有消息，且官方新版 content 已去除 @ 机器人
/// 前缀、mentions 不含机器人自身，因此需用机器人自身 openid 精确比对
/// （兼容历史推送中保留的 &lt;@!{bot_openid}&gt; 标签）。
///
/// openid 来源（优先级）：
/// 1. 配置 Rain.BotOpenId（环境变量 RAIN__BOTOPENID）
/// 2. 自动学习：收到 GROUP_AT_MESSAGE_CREATE 时从其 content 中解析 &lt;@!xxx&gt; 标签并落库
/// 
/// openid 未知时不判定为 @（只统计，不误判）。
/// </summary>
public class BotIdentityResolver(RuntimeConfig config, Database db, ILogger<BotIdentityResolver> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly Database _db = db;
    private readonly ILogger<BotIdentityResolver> _logger = logger;

    /// <summary>匹配 <@!openid> 标签</summary>
    private static readonly Regex AtTagRegex = new(@"<@!([^>]+)>", RegexOptions.Compiled);

    private const string LearnedOpenIdSettingKey = "Bot.OpenId";
    private readonly Lock _lock = new();
    private string? _learnedOpenId;

    /// <summary>当前有效的机器人 openid（配置优先，其次自动学习值）</summary>
    public async Task<string?> GetBotOpenIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.Config.BotOpenId))
        {
            return _config.Config.BotOpenId;
        }
        lock (_lock)
        {
            if (_learnedOpenId != null)
            {
                return _learnedOpenId;
            }
        }
        string? learned = await _db.GetSettingAsync(LearnedOpenIdSettingKey);
        if (!string.IsNullOrWhiteSpace(learned))
        {
            lock (_lock)
            {
                _learnedOpenId = learned;
            }
        }
        return learned;
    }

    /// <summary>
    /// 从 @ 事件消息中自动学习机器人 openid。
    /// GROUP_AT_MESSAGE_CREATE 只在 @ 机器人时推送，其 content 中的 &lt;@!xxx&gt; 标签即机器人自身。
    /// </summary>
    public async Task ResolveFromAtContentAsync(string content)
    {
        Match match = AtTagRegex.Match(content);
        if (!match.Success)
        {
            return; // 新版 content 已去前缀，无标签可学，忽略
        }
        string openId = match.Groups[1].Value;
        lock (_lock)
        {
            if (openId == _learnedOpenId)
            {
                return;
            }
            _learnedOpenId = openId;
        }
        await _db.UpsertSettingAsync(LearnedOpenIdSettingKey, openId);
        _logger.LogInformation("已自动学习机器人群内 OpenID：{OpenId}", openId);
    }

    /// <summary>
    /// 判断全量消息是否 @ 了机器人（精确 openid 匹配）。
    /// openid 未知时返回 false（只统计不触发，避免误判 @ 他人/提到名字）。
    /// </summary>
    public async Task<bool> IsAtBotAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        string? botOpenId = await GetBotOpenIdAsync();
        if (string.IsNullOrEmpty(botOpenId))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("机器人 OpenID 未知，全量消息不做 @ 判定（仅统计）");
            return false;
        }
        return content.Contains($"<@!{botOpenId}>", StringComparison.Ordinal);
    }
}
