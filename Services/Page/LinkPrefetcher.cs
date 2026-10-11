using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Preprocessing;

namespace RainBot.Services.Page;

public sealed partial class LinkPrefetcher(PageService pages, RuntimeConfig config, HistoryStore history, ILogger<LinkPrefetcher>? logger = null) : IMessagePreprocessor
{
    public string Name => "视频链接";
    public bool SendParsedContent => config.Config.Page.SendParsedContent;
    // 网络预算最多 10 秒，管线额外留出取消和结果整理时间。
    public TimeSpan Timeout => TimeSpan.FromSeconds(11);

    public async Task<IReadOnlyList<PreprocessedText>> ProcessAsync(MessagePreprocessContext context, CancellationToken cancellationToken)
    {
        var results = await PrefetchAsync(context.Current, context.Batch, context.QuotedText,
            context.Now, cancellationToken, context.QuotedVideoUrls);
        int max = Math.Clamp(config.Config.Page.MaxChars, 100, 800);
        return results.Select(result =>
        {
            string text = $"标题：{result.Title}\n{result.Summary}";
            if (text.Length > max) text = text[..max] + "…";
            return new PreprocessedText(result.Url, result.Status,
                result.Status == "ok" ? "只提供视频元信息，未观看视频。\n" + text : "", result.Error) { DisplayCard = result.DisplayCard };
        }).ToList();
    }

    public static List<string> Extract(string content)
    {
        // 卡片正文只用于展示；目标地址由协议层从 jump_url 提取。
        if (content.StartsWith("[卡片消息]", StringComparison.Ordinal)) return [];
        return Urls().Matches(content).Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '，', '。', '；', '）', '】'))
            .Select(NormalizeVideoUrl).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
    }

    public static string? NormalizeVideoUrl(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
            || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo) || url.Port is not (80 or 443)) return null;
        if (BilibiliReader.TryGetBvid(url) is { } bvid) return BilibiliReader.VideoUrl(bvid).AbsoluteUri;
        return BilibiliReader.IsShortLink(url) ? new UriBuilder(url) { Fragment = "" }.Uri.AbsoluteUri : null;
    }

    public static List<string> Extract(IncomingMessage message) => Extract(message.Content)
        .Concat(message.CardVideoUrls.Select(NormalizeVideoUrl).OfType<string>()).Distinct(StringComparer.Ordinal).ToList();

    public async Task<List<PageResult>> PrefetchAsync(IncomingMessage current, IReadOnlyList<IncomingMessage> batch,
        string quotedText, DateTimeOffset now, CancellationToken cancellationToken, IReadOnlyList<string>? quotedUrls = null)
    {
        if (!config.Config.Page.PrefetchEnabled) return [];
        List<string> links = Extract(current);
        if (links.Count == 0)
        {
            links.AddRange(Extract(quotedText));
            if (quotedUrls != null) links.AddRange(quotedUrls.Select(NormalizeVideoUrl).OfType<string>());
        }
        links.AddRange(batch.Reverse().SelectMany(Extract));
        if (links.Count == 0 && !current.Content.StartsWith("[卡片消息]", StringComparison.Ordinal) && Followup().IsMatch(current.Content))
            links.AddRange(history.FindRecentVideoUrls(current.GroupOpenId, now.AddSeconds(-Math.Clamp(config.Config.Page.LinkLookbackSeconds, 0, 3600))));
        links = links.Distinct(StringComparer.Ordinal).Take(Math.Clamp(config.Config.Page.MaxLinksPerTurn, 1, 4)).ToList();
        if (links.Count == 0) return [];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Config.Page.BudgetSeconds, 1, 10)));
        List<PageResult> results = (await Task.WhenAll(links.Select(async link =>
        {
            try { return await pages.ReadAsync(link, budget.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new PageResult("timeout", link, "bilibili", Error: "本轮读取预算已用尽，未取得视频信息。"); }
        }))).ToList();
        logger?.LogDebug("视频链接预取完成：{Count} 条，耗时 {ElapsedMs}ms，结果：{Results}", results.Count,
            timer.ElapsedMilliseconds, string.Join("；", results.Select(result => $"{result.Status} / {result.Title}")));
        return results;
    }

    [GeneratedRegex(@"https?://[^\s<>""'【】，。；！？（）]+", RegexOptions.IgnoreCase, 100)] private static partial Regex Urls();
    [GeneratedRegex(@"这个|这条|这视频|刚才.*(?:链接|视频)|(?:链接|视频).*(?:咋样|怎么样|说什么|讲什么|分析|看看)", RegexOptions.None, 100)] private static partial Regex Followup();
}
