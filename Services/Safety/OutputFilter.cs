using System.Text.RegularExpressions;
using RainBot.Services.Config;

namespace RainBot.Services.Safety;

/// <summary>
/// 输出风控：发送前检查。
/// - 隐私泄露：不得包含 openid（长串十六进制/短 ID 模式），命中整段替换。
/// - 兜底话术：命中「想不出怎么接话题」等敷衍句模式 → 不发送（静默跳过）。
/// - 超长控制：强制 ≤ MaxOutputLines 行、≤ MaxOutputChars 字符（截断兜底）——**仅对 LLM 对话回复生效**。
/// - 空文本/异常文本不发送。
///
/// 指令等硬编码回复与娱乐功能不触发 LLM，不应受 LLM 输出限制影响
/// （调用方传 applyLlmLimits: false，仅保留隐私替换与注入拒绝两道安全过滤）。
/// </summary>
public class OutputFilter(RuntimeConfig config, ILogger<OutputFilter> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<OutputFilter> _logger = logger;

    /// <summary>openid 形如 32 位十六进制长串</summary>
    private static readonly Regex OpenIdPattern = new(@"[0-9a-fA-F]{20,}", RegexOptions.Compiled);

    /// <summary>
    /// 兜底话术模式：「想不出怎么接话题/等我缓缓」这类敷衍句一律不发。
    /// 代码层已不再生成此类话术，该防线拦截模型自己从历史输出里模仿出来的变体。
    /// </summary>
    private static readonly Regex FallbackPhrasePattern = new(@"想不出.{0,8}(接|说)|(等我|让我)缓缓|接不上", RegexOptions.Compiled);

    /// <summary>
    /// 审核输出，返回可发送的文本；null 表示不应发送。
    /// </summary>
    /// <param name="text">待审核文本</param>
    /// <param name="applyLlmLimits">
    /// 是否应用 LLM 输出限制（MaxOutputLines/MaxOutputChars 截断）。
    /// 仅 LLM 对话回复应受该限制；指令等非 LLM 回复传 false。
    /// </param>
    public string? Filter(string? text, bool applyLlmLimits = true)
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

        // 非 LLM 对话回复：到此为止（不受 LLM 行数/字符配置影响，也不做兜底话术拦截——
        // 指令回复是代码硬编码、思维链查看是诊断内容，均不经过模型生成）
        if (!applyLlmLimits)
        {
            return result;
        }

        // 兜底话术防线：命中即静默跳过（宁可不回，不发敷衍空话）。
        // 仅对 LLM 生成的回复生效，拦截模型从历史输出里模仿出的敷衍句。
        if (FallbackPhrasePattern.IsMatch(result))
        {
            _logger.LogWarning("【输出风控】命中兜底话术模式，静默跳过不发送：{Text}", result);
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
