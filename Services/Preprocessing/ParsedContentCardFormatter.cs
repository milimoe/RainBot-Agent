using System.Text;
using RainBot.Services.Page;
using static RainBot.Services.Preprocessing.ParsedContentSender;

namespace RainBot.Services.Preprocessing;

internal static class ParsedContentCardFormatter
{
    internal static string Format(ParsedContentCard card, string source, bool markdown, int budget)
    {
        bool video = card.Kind == "bilibili";
        string Value(string label) => card.Fields.FirstOrDefault(field => field.Label == label)?.Value ?? "";
        string Render(string value, int max = 150) => Clip(markdown ? Escape(value) : Plain(value), max);
        string footer = "\n\n来源：" + Clip(Plain(source).Replace('\n', ' '), 240) + "\n\n";
        if (markdown && Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            && string.IsNullOrEmpty(url.UserInfo) && url.AbsoluteUri.Length <= 240)
        {
            string address = url.AbsoluteUri.Replace("(", "%28").Replace(")", "%29");
            string playAddress = video && BilibiliReader.TryGetBvid(url) is { } bvid
                ? $"https://player.bilibili.com/player.html?bvid={bvid}&p=1&autoplay=true" : address;
            footer = video ? $"\n\n[立即播放]({playAddress})  [原网页]({address})\n\n"
                : $"\n\n[查看原链接]({address})\n\n";
        }
        var prefix = new StringBuilder();
        string title = Render(card.Title, Math.Clamp(budget / 5, 40, video ? 180 : 160));
        var body = new StringBuilder();
        if (video)
        {
            string cover = BilibiliReader.NormalizeCover(card.ImageUrl);
            // 优先保留标题和来源，多链接预算不足时省略封面，而不是截断图片语法。
            if (markdown && cover.Length is > 0 and <= 360 && budget - footer.Length - title.Length - cover.Length > 220)
                prefix.Append($"![视频封面 #640px #360px]({cover.Replace("(", "%28").Replace(")", "%29")})\n\n");
            var stats = new List<string>();
            if (Value("播放").Length > 0) stats.Add("▶ " + Render(Value("播放"), 20));
            if (Value("弹幕").Length > 0) stats.Add("💬 " + Render(Value("弹幕"), 20));
            if (Value("点赞").Length > 0) stats.Add("♡ " + Render(Value("点赞"), 20));
            if (Value("时长").Length > 0) stats.Add("⏱ " + Render(Value("时长"), 20));
            if (stats.Count > 0) body.Append(string.Join("  ·  ", stats)).Append("\n\n");
            body.Append("UP：").Append(Render(Value("UP"), 100));
            if (Value("发布").Length > 0) body.Append("  ·  ").Append(Render(Value("发布"), 30));
            body.Append("\n\n").Append(markdown ? "## " : "").Append(title).Append("\n\n")
                .Append("简介：").Append(Render(string.IsNullOrWhiteSpace(card.Description) ? "-" : card.Description, 1000));
        }
        else
        {
            prefix.Append(markdown ? "## " : "【").Append(title).Append(markdown ? "\n\n" : "】\n")
                .Append(Render(card.Kind, 40)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(card.Description))
                body.Append(markdown ? "> " : "简介：").Append(Render(card.Description, 300).Replace("\n", markdown ? "\n> " : "\n")).Append("\n\n");
            foreach (var pair in card.Fields.Take(10).Chunk(2))
            {
                body.Append(string.Join("  ·  ", pair.Select(field => markdown
                    ? $"**{Render(field.Label, 30)}**：{Render(field.Value, 100)}"
                    : $"{Render(field.Label, 30)}：{Render(field.Value, 100)}"))).Append("\n\n");
            }
        }
        int bodyBudget = Math.Max(0, budget - prefix.Length - footer.Length);
        return prefix + Clip(body.ToString().TrimEnd(), bodyBudget) + footer;
    }
}
