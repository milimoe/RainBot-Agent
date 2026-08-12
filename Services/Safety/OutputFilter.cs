using System.Text.RegularExpressions;
using RainBot.Services.Config;

namespace RainBot.Services.Safety;

/// <summary>
/// 输出风控：发送前检查。
/// - 隐私泄露：不得包含 openid（长串十六进制/短 ID 模式），命中整段替换。
/// - 超长控制：强制 ≤ MaxOutputLines 行、≤ MaxOutputChars 字符（截断兜底）。
/// - 空文本/异常文本不发送。
/// </summary>
public class OutputFilter(RuntimeConfig config, ILogger<OutputFilter> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<OutputFilter> _logger = logger;

    /// <summary>openid 形如 32 位十六进制长串</summary>
    private static readonly Regex OpenIdPattern = new(@"[0-9a-fA-F]{20,}", RegexOptions.Compiled);

    /// <summary>审核输出，返回可发送的文本；null 表示不应发送</summary>
    public string? Filter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string result = text.Trim();

        // 隐私：替换疑似 openid 的长串
        if (OpenIdPattern.IsMatch(result))
        {
            _logger.LogWarning("【输出风控】检测到疑似 openid 泄露，已替换");
            result = OpenIdPattern.Replace(result, "[ID]");
        }

        // 拒绝包含富媒体标签的异常输出（防止注入 <qqbot-*> 指令）
        if (Regex.IsMatch(result, @"<qqbot-[^>]+>"))
        {
            _logger.LogWarning("【输出风控】检测到疑似指令注入，已拒绝发送");
            return null;
        }

        int maxLines = _config.Config.Llm.MaxOutputLines;
        int maxChars = _config.Config.Llm.MaxOutputChars;

        // 超长截断：先按行，再按字符
        if (result.Length > maxChars)
        {
            result = result[..maxChars] + "…";
            _logger.LogInformation("【输出风控】超长输出已截断至 {Max} 字符", maxChars);
        }
        string[] lines = result.Split('\n');
        if (lines.Length > maxLines)
        {
            result = string.Join('\n', lines.Take(maxLines)) + "…";
            _logger.LogInformation("【输出风控】超长输出已截断至 {Max} 行", maxLines);
        }

        return result;
    }
}
