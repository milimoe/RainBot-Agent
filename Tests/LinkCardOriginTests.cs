using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Preprocessing;
using RainBot.Services.QQ;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class LinkCardOriginTests
{
    private const string Video = "https://www.bilibili.com/video/BV16Faz6tE4F/";
    private const string GitHub = "https://github.com/owner/repo/issues/7";
    private const string Group = "qq:link-origin";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host == "b23.tv")
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(Video);
            return redirect;
        }
        if (request.RequestUri.Host == "api.bilibili.com")
            return Json("""{"code":0,"data":{"title":"来源视频标题","desc":"来源视频简介","owner":{"name":"UP"}}}""");
        if (request.RequestUri.Host == "api.github.com")
            return Json("""{"number":7,"title":"来源Issue标题","body":"来源Issue正文","state":"open"}""");
        return Json(TestHelpers.LlmTextResponse("回答正文"));
    }

    private static void Configure(ServiceProvider provider)
    {
        var config = provider.GetRequiredService<RuntimeConfig>().Config;
        config.Page.BilibiliCookies = "buvid3=test; buvid4=test";
        config.Trigger.RandomChatProbability = 0;
        config.Trigger.PassiveCooldownSeconds = 0;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(config.Fun, false);
    }

    private static MessagePreprocessContext Context(IncomingMessage current, string quoted = "",
        IReadOnlyList<IncomingMessage>? batch = null, IReadOnlyList<string>? quotedVideos = null) =>
        new(current, batch ?? [current], quoted, [], quotedVideos ?? [], DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 发链接出卡片随后单独追问和引用回复均不重复发卡(bool video)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        string url = video ? Video : GitHub;
        var processor = sp.GetRequiredService<MessageProcessor>();
        var queue = sp.GetRequiredService<SendQueue>();
        await processor.ProcessAsync(TestHelpers.Msg(Group, "alice", "分享一下 " + url, msgId: "link-message"), default);
        var initial = Assert.Single(queue.PeekPendingForTest());
        Assert.True(initial.ExcludeFromContext);
        Assert.Equal("link-message", initial.MsgId);
        await processor.ProcessAsync(TestHelpers.Msg(Group, "alice", "这个怎么样", isAt: true), default);
        var followup = Assert.Single(queue.PeekPendingForTest());
        Assert.False(followup.ExcludeFromContext);
        await processor.ProcessAsync(new IncomingMessage
        {
            BotId = "qq", GroupOpenId = Group, SenderOpenId = "alice", MsgId = "quoted-message",
            Content = "再解释一下", QuotedContent = url, IsAtRobot = true
        }, default);
        var quoted = Assert.Single(queue.PeekPendingForTest());
        Assert.False(quoted.ExcludeFromContext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 引用解析保留模型资料但没有直发资格(bool video)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var current = TestHelpers.Msg(Group, "alice", "分析引用内容");
        var result = Assert.Single(await sp.GetRequiredService<MessagePreprocessorPipeline>()
            .ProcessAsync(Context(current, video ? Video : GitHub), default));
        Assert.Equal("ok", result.Content.Status);
        Assert.False(result.Content.AllowDirectSend);
        Assert.Null(result.Content.DirectSendMessage);
        var composed = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = Group, Type = TriggerType.Passive, Reason = "测试", PreprocessedResults = [result]
        });
        Assert.Contains(video ? "来源视频标题" : "来源Issue标题", composed.Messages[3].Content);
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
    }

    [Fact]
    public async Task 历史回溯与引用卡片地址只供模型使用且不污染缓存来源()
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var pipeline = sp.GetRequiredService<MessagePreprocessorPipeline>();
        var history = sp.GetRequiredService<HistoryStore>();
        var link = TestHelpers.Msg(Group, "alice", Video);
        await history.AppendAsync(Group, link);
        await pipeline.ProcessAsync(Context(link), default);
        Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        var followup = TestHelpers.Msg(Group, "bob", "这个视频怎么样");
        var recalled = Assert.Single(await pipeline.ProcessAsync(Context(followup), default));
        Assert.False(recalled.Content.AllowDirectSend);
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        var quoted = Assert.Single(await pipeline.ProcessAsync(Context(followup, quotedVideos: [Video]), default));
        Assert.False(quoted.Content.AllowDirectSend);
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        // 后续主动重新分享同链接仍正常出卡，不使用全局 URL 冷却掩盖来源问题。
        await pipeline.ProcessAsync(Context(TestHelpers.Msg(Group, "bob", Video, msgId: "reshare")), default);
        Assert.Equal("reshare", Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest()).MsgId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 合并批次卡片关联实际含链接消息而非末尾追问(bool video)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var link = TestHelpers.Msg(Group, "alice", video ? Video : GitHub, msgId: "original-link");
        var followup = TestHelpers.Msg(Group, "bob", "这个怎么样", msgId: "later-question");
        await sp.GetRequiredService<MessagePreprocessorPipeline>().ProcessAsync(Context(followup, batch: [link, followup]), default);
        var card = Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Equal("original-link", card.MsgId);
    }

    [Fact]
    public async Task 短链跳转和QQ分享卡片均属于本条消息的链接()
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var pipeline = sp.GetRequiredService<MessagePreprocessorPipeline>();
        var shortLink = TestHelpers.Msg(Group, "alice", "https://b23.tv/share", msgId: "short-link");
        var result = Assert.Single(await pipeline.ProcessAsync(Context(shortLink), default));
        Assert.Equal(Video, result.Content.Source);
        Assert.True(result.Content.AllowDirectSend);
        Assert.Equal("short-link", Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest()).MsgId);
        var cardMessage = new IncomingMessage
        {
            GroupOpenId = Group, SenderOpenId = "alice", MsgId = "share-card",
            Content = "[卡片消息] 视频分享", CardVideoUrls = [Video]
        };
        await pipeline.ProcessAsync(Context(cardMessage), default);
        Assert.Equal("share-card", Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest()).MsgId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 仅引用索引回溯原消息或分享卡片也不重复发卡(bool shareCard)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var processor = sp.GetRequiredService<MessageProcessor>();
        var original = new IncomingMessage
        {
            BotId = "qq", GroupOpenId = Group, SenderOpenId = "alice", MsgId = "original", MsgIdx = "original-index",
            Content = shareCard ? "[卡片消息] 视频分享" : Video,
            CardVideoUrls = shareCard ? [Video] : []
        };
        await processor.ProcessAsync(original, default);
        Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        await processor.ProcessAsync(new IncomingMessage
        {
            BotId = "qq", GroupOpenId = Group, SenderOpenId = "bob", MsgId = "reply",
            RefMsgIdx = "original-index", Content = "解释一下", IsAtRobot = true
        }, default);
        var reply = Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.False(reply.ExcludeFromContext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 回放和机器人回显不能作为新卡片来源(bool replay)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        var echo = new IncomingMessage
        {
            GroupOpenId = Group, SenderOpenId = "alice", MsgId = "already-handled", Content = Video,
            SkipSideEffects = replay, IsFromBot = !replay
        };
        var current = TestHelpers.Msg(Group, "alice", "再看看");
        var results = await sp.GetRequiredService<MessagePreprocessorPipeline>()
            .ProcessAsync(Context(current, batch: [echo, current]), default);
        Assert.False(Assert.Single(results).Content.AllowDirectSend);
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
    }

    [Fact]
    public async Task 风控提示同样不因追问或引用而重复发送()
    {
        await using var sp = await TestHost.BuildReadyAsync(_ => Json("""{"code":-352,"message":"风控"}"""));
        Configure(sp);
        sp.GetRequiredService<RuntimeConfig>().Config.MarkdownReply = true;
        var pipeline = sp.GetRequiredService<MessagePreprocessorPipeline>();
        var link = TestHelpers.Msg(Group, "alice", Video);
        await sp.GetRequiredService<HistoryStore>().AppendAsync(Group, link);
        await pipeline.ProcessAsync(Context(link), default);
        Assert.Contains("风控", Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest()).Content);
        var question = TestHelpers.Msg(Group, "alice", "这个怎么样");
        var recalled = Assert.Single(await pipeline.ProcessAsync(Context(question), default));
        Assert.Equal("risk", recalled.Content.Status);
        await pipeline.ProcessAsync(Context(question, Video), default);
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 混合本条链接与引用链接只发送本条链接卡片(bool video)
    {
        await using var sp = await TestHost.BuildReadyAsync(Respond);
        Configure(sp);
        string direct = video ? Video : GitHub;
        string quoted = video ? "https://www.bilibili.com/video/BV1xx411c7mD/" : "https://github.com/owner/repo/issues/8";
        // 视频预取有本条地址时按现有规则不另取引用；GitHub 会同时提供引用资料。
        var results = await sp.GetRequiredService<MessagePreprocessorPipeline>()
            .ProcessAsync(Context(TestHelpers.Msg(Group, "alice", direct), quoted), default);
        Assert.Single(results, r => r.Content.AllowDirectSend);
        var card = Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Contains(direct, card.Content);
        Assert.DoesNotContain(quoted, card.Content);
    }
}
