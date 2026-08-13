using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;

namespace RainBot.Services.QQ;

/// <summary>
/// 机器人身份识别：判断群消息是否 @ 了机器人。
///
/// 背景：开启「接收所有消息」后，群消息统一经 GROUP_MESSAGE_CREATE 推送
/// （GROUP_AT_MESSAGE_CREATE 在部分环境下不再单独推送）。新版 payload 提供两类信号：
/// 1. mentions 数组：@ 机器人时对应项携带 is_you=true（"是否机器人自己"）——权威信号；
/// 2. content 中的 @ 标签：新格式为 &lt;@openid&gt;、历史格式为 &lt;@!openid&gt;（兼容两种）。
///
/// openid 来源（优先级）：
/// 1. 配置 Rain.BotOpenId（环境变量 RAIN__BOTOPENID / /admin set Bot.OpenId）
/// 2. 自动学习：@ 事件 content 标签或 mentions 中的机器人 openid，落库 settings 表
///
/// openid 未知且无 mentions 信号时不判定为 @（只统计，不误判）。
/// </summary>
public class BotIdentityResolver(RuntimeConfig config, Database db, ILogger<BotIdentityResolver> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly Database _db = db;
    private readonly ILogger<BotIdentityResolver> _logger = logger;

    /// <summary>匹配 <@openid> 或历史格式 <@!openid> 标签</summary>
    private static readonly Regex AtTagRegex = new(@"<@!?([^>]+)>", RegexOptions.Compiled);

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
    /// GROUP_AT_MESSAGE_CREATE 只在 @ 机器人时推送，其 content 中的 @ 标签即机器人自身。
    /// </summary>
    public async Task ResolveFromAtContentAsync(string content)
    {
        Match match = AtTagRegex.Match(content);
        if (!match.Success)
        {
            return; // 新版 content 已去前缀，无标签可学，忽略
        }
        await LearnOpenIdAsync(match.Groups[1].Value);
    }

    /// <summary>
    /// 判断消息是否 @ 了机器人。
    /// 优先用 mentions 数组（is_you / openid 匹配，权威信号）；
    /// 其次兼容 content 标签精确匹配（新格式 &lt;@xxx&gt; 与历史格式 &lt;@!xxx&gt;）。
    /// </summary>
    public async Task<bool> IsAtBotAsync(string content, IReadOnlyList<Mention>? mentions = null)
    {
        if (mentions is { Count: > 0 })
        {
            string? botOpenId = await GetBotOpenIdAsync();
            foreach (Mention mention in mentions)
            {
                // 官方标记"是否机器人自己"：最权威的 @ 信号
                if (mention.IsYou)
                {
                    await LearnFromMentionAsync(mention);
                    return true;
                }
                // openid 精确匹配兜底（部分 payload 无 is_you 字段）
                if (!string.IsNullOrEmpty(botOpenId)
                    && (string.Equals(mention.Id, botOpenId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mention.MemberOpenId, botOpenId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mention.UserOpenId, botOpenId, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        // content 标签兜底（历史 payload 或 mentions 缺失的环境）
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        string? openId = await GetBotOpenIdAsync();
        if (string.IsNullOrEmpty(openId))
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("机器人 OpenID 未知且无 mentions 信号，不做 @ 判定（仅统计）");
            return false;
        }
        return content.Contains($"<@!{openId}>", StringComparison.Ordinal)
            || content.Contains($"<@{openId}>", StringComparison.Ordinal);
    }

    /// <summary>从 mentions 中学习机器人 openid（is_you=true 的那一项即机器人自己）</summary>
    private async Task LearnFromMentionAsync(Mention mention)
    {
        string openId = !string.IsNullOrEmpty(mention.MemberOpenId) ? mention.MemberOpenId
            : !string.IsNullOrEmpty(mention.Id) ? mention.Id
            : mention.UserOpenId;
        if (!string.IsNullOrEmpty(openId))
        {
            await LearnOpenIdAsync(openId);
        }
    }

    private async Task LearnOpenIdAsync(string openId)
    {
        if (string.IsNullOrWhiteSpace(openId))
        {
            return;
        }
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
}
