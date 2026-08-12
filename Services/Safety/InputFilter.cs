using System.Text.RegularExpressions;

namespace RainBot.Services.Safety;

/// <summary>
/// 输入风控：识别广告、涉政、引流等敏感内容，命中则不回应（仅标记为观察）。
/// 采用关键词规则优先（成本可控），命中结果进入观察日志。
/// </summary>
public class InputFilter(ILogger<InputFilter> logger)
{
    private readonly ILogger<InputFilter> _logger = logger;

    /// <summary>敏感类别 → 关键词（词库克制，避免误伤正常聊天）</summary>
    private static readonly Dictionary<string, string[]> Rules = new()
    {
        ["广告"] = ["加v", "加我v", "扫码", "点击链接", "点击下方链接", "低价出", "出闲置低价", "日赚", "躺赚", "稳赚", "刷单", "代购加", "推广加"],
        ["引流"] = ["私聊我", "私信我", "加群号", "拉你进群", "vx号", "微信号是", "qq号是", "加我好友", "引流"],
        ["博彩"] = ["棋牌游戏", "博彩", "彩票投注", "赌博平台", "百家乐"],
        ["涉政"] = ["翻墙教程", "翻墙软件", "政治敏感词" ]
    };

    private static readonly Dictionary<string, Regex> Compiled = Rules.ToDictionary(
        kv => kv.Key,
        kv => new Regex(string.Join("|", kv.Value.Select(Regex.Escape)), RegexOptions.IgnoreCase));

    /// <summary>
    /// 检查消息是否命中敏感规则。
    /// </summary>
    /// <returns>命中类别；未命中返回 null</returns>
    public string? Check(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        foreach ((string category, Regex regex) in Compiled)
        {
            if (regex.IsMatch(content))
            {
                _logger.LogInformation("【输入风控】命中「{Category}」，仅标记观察不回应", category);
                return category;
            }
        }
        return null;
    }
}
