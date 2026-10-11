using System.Text;
using System.Text.RegularExpressions;
using RainBot.Services.Config;
using RainBot.Services.Safety;
using RainBot.Services.Trigger;
using RainBot.Services.Page;

namespace RainBot.Services.Preprocessing;

/// <summary>解析完毕立即入队，由现有发送器调用 QQBot API；无需模型生成或调用工具。</summary>
public sealed partial class ParsedContentSender(
    SendQueue queue, RuntimeConfig config, OutputFilter filter, ILogger<ParsedContentSender> logger)
{
    // 每个解析器每轮只发一条；多来源均分正文预算，避免 README 等大文本刷屏。
    internal const int MaxMessageChars = 1800;

    public async Task SendAsync(MessagePreprocessContext context, string processor,
        IReadOnlyList<PreprocessedText> results, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Current.SkipSideEffects) return;
        var successful = results.Where(result => result.Status == "ok" && !string.IsNullOrWhiteSpace(result.Text))
            .DistinctBy(result => result.Source).Take(4).ToList();
        try
        {
            bool markdown = config.Config.MarkdownReply;
            if (successful.Count > 0)
            {
                string? content = FilterContent(Format(processor, successful, markdown), successful);
                if (content != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await queue.EnqueueAsync(new SendTask
                    {
                        BotId = context.Current.BotId,
                        GroupOpenId = context.Current.GroupOpenId,
                        IsPrivate = context.Current.IsPrivate,
                        MsgId = context.Current.IsFullMessage ? null : context.Current.MsgId,
                        Markdown = markdown,
                        Content = content,
                        ExcludeFromContext = true
                    });
                    logger.LogDebug("解析资料已入发送队列：{Processor} / {Count} 条", processor, successful.Count);
                }
            }
            // 风控兜底：B 站 412/-352 时资料缺失，但来源仍是可点的 BV 页面，
            // Markdown 模式下直接给出播放/原网页链接，群友不必等重试或自己翻聊天记录。
            if (markdown && RiskFallback(results) is { } riskDraft && FilterContent(riskDraft, results) is { } risk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await queue.EnqueueAsync(new SendTask
                {
                    BotId = context.Current.BotId,
                    GroupOpenId = context.Current.GroupOpenId,
                    IsPrivate = context.Current.IsPrivate,
                    MsgId = context.Current.IsFullMessage ? null : context.Current.MsgId,
                    Markdown = true,
                    Content = risk,
                    ExcludeFromContext = true
                });
                logger.LogDebug("B 站风控兜底提示已入发送队列");
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 直接发送失败不能把已读到的资料变成解析失败，也不能阻断 LLM 回复。
            logger.LogWarning(exception, "解析资料发送入队失败：{Processor}", processor);
        }
    }

    private string? FilterContent(string content, IReadOnlyList<PreprocessedText> results)
    {
        // 公共封面/Commit/文件链接经常含 32–40 位哈希，不能被 OpenID 脱敏规则改坏。
        // 只保护已验证的 B 站 CDN 和支持的公开来源地址；正文继续应用现有输出过滤。
        List<(string Token, string Url)> protectedUrls = [];
        foreach (var result in results)
        {
            var urls = new List<string>();
            if (GitHubLink.Parse(result.Source) != null || LinkPrefetcher.NormalizeVideoUrl(result.Source) != null)
                urls.Add(result.Source);
            if (result.DisplayCard?.Kind == "bilibili")
            {
                string cover = BilibiliReader.NormalizeCover(result.DisplayCard.ImageUrl);
                if (cover.Length > 0) urls.Add(cover);
            }
            foreach (string url in urls.SelectMany(url => new[] { url, url.Replace("(", "%28").Replace(")", "%29") }).Distinct())
            {
                if (!content.Contains(url, StringComparison.Ordinal)) continue;
                string token = "RAINBOT_PUBLIC_URL_" + Guid.NewGuid().ToString("N")[..12] + "_END";
                content = content.Replace(url, token, StringComparison.Ordinal);
                protectedUrls.Add((token, url));
            }
        }
        string? filtered = filter.Filter(content, applyLlmLimits: false);
        if (filtered == null) return null;
        foreach (var (token, url) in protectedUrls) filtered = filtered.Replace(token, url, StringComparison.Ordinal);
        return filtered;
    }

    /// <summary>B 站风控兜底消息（Markdown 链接）；没有可播放的 BV 号时返回 null。</summary>
    internal static string? RiskFallback(IReadOnlyList<PreprocessedText> results)
    {
        List<string> lines = [];
        foreach (PreprocessedText result in results)
        {
            if (result.Status != "risk" || !Uri.TryCreate(result.Source, UriKind.Absolute, out Uri? url)) continue;
            if (BilibiliReader.TryGetBvid(url) is not { } bvid) continue;
            lines.Add($"[立即播放](https://player.bilibili.com/player.html?bvid={bvid}&p=1&autoplay=true)  [原网页]({BilibiliReader.VideoUrl(bvid).AbsoluteUri})");
        }
        if (lines.Count == 0) return null;
        return "## B 站链接解析失败\n\n> B 站风控拦截了本次解析，暂时拿不到视频信息，可以直接点开：\n\n" + string.Join("\n\n", lines);
    }

    internal static string Format(string processor, IReadOnlyList<PreprocessedText> results, bool markdown)
    {
        string name = processor == "视频链接" ? "B 站链接" : processor;
        var builder = new StringBuilder(results.All(result => result.DisplayCard != null) ? ""
            : markdown ? $"## {Escape(name)}解析\n\n" : $"【{Plain(name)}解析】\n");
        int sectionBudget = (MaxMessageChars - builder.Length) / results.Count;
        foreach (var result in results)
        {
            if (result.DisplayCard is { } card)
            {
                builder.Append(ParsedContentCardFormatter.Format(card, result.Source, markdown, sectionBudget));
                continue;
            }
            // 来源链接由解析器提供；只生成安全的 HTTP(S) 链接，其余来源显示为文字。
            string source = Plain(result.Source).Replace('\n', ' ');
            string footer = "\n来源：" + Clip(source, 240) + "\n";
            if (markdown && source.Length <= 240 && Uri.TryCreate(source, UriKind.Absolute, out var url)
                && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo) && url.AbsoluteUri.Length <= 240)
                footer = "\n\n[查看原链接](" + url.AbsoluteUri.Replace("(", "%28").Replace(")", "%29") + ")\n\n";
            string title = result.Text.Split('\n').FirstOrDefault(line => line.StartsWith("标题：", StringComparison.Ordinal)) ?? "解析资料";
            string heading = markdown ? $"**{Clip(Escape(title), 100)}**\n\n" : Clip(Plain(title), 100) + "\n";
            string body = string.Join('\n', result.Text.Replace("\r", "").Split('\n')
                .Where(line => !line.StartsWith("标题：", StringComparison.Ordinal))
                .Select(line => markdown ? "> " + Escape(line) : Plain(line)));
            int bodyBudget = Math.Max(0, sectionBudget - heading.Length - footer.Length);
            builder.Append(heading).Append(Clip(body, bodyBudget)).Append(footer);
        }
        return builder.ToString().Trim();
    }

    internal static string Plain(string value) => value.Replace('<', '＜').Replace('>', '＞')
        .Replace("\r", "").Replace("\0", "");
    internal static string Escape(string value) => MarkdownCharacters().Replace(Plain(value), @"\$0");
    internal static string Clip(string value, int max)
    {
        if (value.Length <= max) return value;
        const string suffix = "…（内容已截断）";
        if (max < suffix.Length) return "";
        string prefix = value[..(max - suffix.Length)].TrimEnd('\\');
        if (prefix.Length > 0 && char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
        return prefix + suffix;
    }

    [GeneratedRegex(@"[\\`*_{}\[\]()#+.!|~-]", RegexOptions.None, 100)] private static partial Regex MarkdownCharacters();
}
