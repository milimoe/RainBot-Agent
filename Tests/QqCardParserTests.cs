using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Page;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

public class QqCardParserTests
{
    [Fact]
    public void 图文卡片只提取目标而不读取描述封面和图标()
    {
        using JsonDocument data = JsonDocument.Parse("""
            {"ark_data":{"ark_type":"tuwen","fields":{"jump_url":"https://b23.tv/NlM29C2","title":"视频标题",
            "preview":"https://b23.tv/fakeCover","source_logo":"https://b23.tv/fakeLogo","desc":"https://b23.tv/fakeDescription"}}}
            """);
        Assert.Equal(new[] { "https://b23.tv/NlM29C2" }, QqCardParser.ExtractVideoLinks(data.RootElement));
    }

    [Theory]
    [InlineData("miniapp", "https://b23.tv/miniapp", false)]
    [InlineData("tuwen", "https://www.bilibili.com/video/BV16Faz6tE4F/?share_source=copy_web", true)]
    [InlineData("tuwen", "https://www.zhihu.com/question/1", false)]
    [InlineData("tuwen", "http://127.0.0.1/", false)]
    [InlineData("tuwen", "https://b23.tv.evil.example/abc", false)]
    [InlineData("tuwen", "https://user:pass@b23.tv/abc", false)]
    [InlineData("tuwen", "https://b23.tv:8080/abc", false)]
    [InlineData("tuwen", "这是链接 https://b23.tv/abc", false)]
    public void 类型和目标地址必须符合限定范围(string type, string url, bool expected)
    {
        using JsonDocument data = JsonDocument.Parse(JsonSerializer.Serialize(new { ark_data = new { ark_type = type, fields = new { jump_url = url } } }));
        Assert.Equal(expected, QqCardParser.ExtractVideoLinks(data.RootElement).Count == 1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ark_data\":null}")]
    [InlineData("{\"ark_data\":{\"ark_type\":123}}")]
    [InlineData("{\"ark_data\":{\"ark_type\":\"tuwen\",\"fields\":null}}")]
    [InlineData("{\"ark_data\":{\"ark_type\":\"tuwen\",\"fields\":{\"jump_url\":123}}}")]
    public void 缺失或异常字段不影响消息处理(string json)
    {
        using JsonDocument data = JsonDocument.Parse(json);
        Assert.Empty(QqCardParser.ExtractVideoLinks(data.RootElement));
    }

    [Fact]
    public async Task 图文卡片可预取并通过历史及引用回溯()
    {
        using ServiceProvider provider = await TestHost.BuildReadyAsync(request => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("""{"code":0,"data":{"title":"图文卡片视频","desc":"实际简介","owner":{"name":"UP"}}}""", System.Text.Encoding.UTF8, "application/json") });
        HistoryStore history = provider.GetRequiredService<HistoryStore>();
        IncomingMessage card = new()
        {
            MsgId = "card", GroupOpenId = "group", SenderOpenId = "alice", MsgIdx = "card-index",
            Content = "[卡片消息] 图文\npreview: https://b23.tv/wrongCover", CardVideoUrls = ["https://www.bilibili.com/video/BV16Faz6tE4F/"]
        };
        await history.AppendAsync("group", card);
        LinkPrefetcher prefetch = provider.GetRequiredService<LinkPrefetcher>();
        Assert.Equal("图文卡片视频", Assert.Single(await prefetch.PrefetchAsync(card, [card], "", DateTimeOffset.UtcNow, default)).Title);
        var followup = TestHelpers.Msg("group", "bob", "这个怎么样");
        Assert.Equal("ok", Assert.Single(await prefetch.PrefetchAsync(followup, [followup], "", DateTimeOffset.UtcNow, default)).Status);
        var entry = history.FindByMsgIdx("group", "card-index")!;
        Assert.Equal(card.CardVideoUrls, entry.VideoUrls);
        var quote = TestHelpers.Msg("group", "bob", "分析一下");
        Assert.Single(await prefetch.PrefetchAsync(quote, [quote], card.Content, DateTimeOffset.UtcNow, default, entry.VideoUrls));
        provider.GetRequiredService<RuntimeConfig>().Config.Page.LinkLookbackSeconds = 0;
        Assert.Empty(await prefetch.PrefetchAsync(followup, [followup], "", DateTimeOffset.UtcNow, default));
    }
}
