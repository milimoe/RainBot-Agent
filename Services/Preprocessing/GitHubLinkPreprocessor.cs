using System.Text.RegularExpressions;
using RainBot.Services.Config;
using RainBot.Services.Page;

namespace RainBot.Services.Preprocessing;

public sealed partial class GitHubLinkPreprocessor(PageService pages, RuntimeConfig config) : IMessagePreprocessor
{
    public string Name => "GitHub 链接";
    public bool SendParsedContent => config.Config.GitHub.SendParsedContent;
    public TimeSpan Timeout => TimeSpan.FromSeconds(11);

    public static List<string> Extract(string content)
    {
        if (content.StartsWith("[卡片消息]", StringComparison.Ordinal)) return [];
        return Urls().Matches(content)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '，', '。', '；', '）', '】'))
            .Select(value => GitHubLink.Parse(value)?.Url.AbsoluteUri).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<PreprocessedText>> ProcessAsync(MessagePreprocessContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = config.Config.GitHub;
        if (!settings.Enabled) return [];
        var directSources = context.GetDirectLinkSources(message => Extract(message.Content));
        var links = Extract(context.Current.Content).Concat(Extract(context.QuotedText))
            .Concat(context.Batch.Reverse().SelectMany(message => Extract(message.Content)))
            .Distinct(StringComparer.Ordinal).Take(Math.Clamp(settings.MaxLinksPerTurn, 1, 4)).ToList();
        if (links.Count == 0) return [];
        int maxChars = Math.Clamp(settings.MaxChars, 200, 4000);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.BudgetSeconds, 1, 10)));
        return await Task.WhenAll(links.Select(async link =>
        {
            directSources.TryGetValue(link, out var directMessage);
            try
            {
                var result = await pages.ReadAsync(link, budget.Token);
                string text = $"公开 GitHub 资料片段，未读取整个仓库、评论、评审或完整 diff。\n标题：{result.Title}\n{result.Summary}";
                if (text.Length > maxChars) text = text[..Math.Max(0, maxChars - 12)] + "…（内容已截断）";
                return new PreprocessedText(result.Url.Length > 0 ? result.Url : link, result.Status,
                    result.Status == "ok" ? text : "", result.Error)
                {
                    DisplayCard = result.DisplayCard,
                    AllowDirectSend = directMessage != null,
                    DirectSendMessage = directMessage
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new PreprocessedText(link, "timeout", Error: "本轮 GitHub 读取预算已用尽，未取得内容。")
                { AllowDirectSend = directMessage != null, DirectSendMessage = directMessage }; }
        }));
    }

    [GeneratedRegex(@"https?://[^\s<>""'【】，。；！？（）]+", RegexOptions.IgnoreCase, 100)] private static partial Regex Urls();
}

