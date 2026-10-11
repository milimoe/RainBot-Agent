using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Page;
using RainBot.Services.Preprocessing;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class ParsedContentSenderTests
{
    private const string GitHub = "https://github.com/owner/repo/issues/7";
    private const string Video = "https://www.bilibili.com/video/BV16Faz6tE4F/";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 未命中插嘴或处于冷却仍发送解析且不调用LLM(bool video, bool coolingDown)
    {
        int llmCalls = 0;
        await using var provider = await TestHost.BuildReadyAsync(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return Json("""{"number":7,"title":"独立解析标题","body":"正文","state":"open"}""");
            if (request.RequestUri.Host == "api.bilibili.com")
                return Json("""{"code":0,"data":{"title":"独立解析标题","desc":"简介","owner":{"name":"UP"}}}""");
            if (request.RequestUri.AbsolutePath.Contains("chat/completions"))
                Interlocked.Increment(ref llmCalls);
            return Json("{}");
        });
        var config = provider.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(config.Fun, false);
        config.Trigger.RandomChatProbability = 0;
        config.Trigger.PassiveCooldownSeconds = 60;
        var states = provider.GetRequiredService<GroupStateManager>();
        var state = states.GetOrCreate("group");
        if (coolingDown)
            provider.GetRequiredService<PassiveTrigger>().MarkTriggered("group", DateTimeOffset.UtcNow);
        var passiveTime = state.LastPassiveTriggerUtc;
        var randomTime = state.LastRandomChatUtc;

        await provider.GetRequiredService<RainBot.Services.QQ.MessageProcessor>().ProcessAsync(
            TestHelpers.Msg("group", "alice", video ? Video : GitHub, isAt: coolingDown), default);

        var sent = Assert.Single(provider.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Contains("独立解析标题", sent.Content);
        Assert.True(sent.ExcludeFromContext);
        Assert.Equal(0, llmCalls);
        Assert.Equal(passiveTime, state.LastPassiveTriggerUtc);
        Assert.Equal(randomTime, state.LastRandomChatUtc);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 两个解析器完成后无需LLM直接调用QQ接口(bool video, bool markdown)
    {
        var sent = new TaskCompletionSource<(string Url, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int llmCalls = 0;
        await using var provider = await TestHost.BuildReadyAsync(request =>
        {
            var url = request.RequestUri!;
            if (url.Host == "api.github.com") return Json("""{"number":7,"title":"资料标题","body":"成功解析正文","state":"open"}""");
            if (url.Host == "api.bilibili.com") return Json("""{"code":0,"data":{"title":"资料标题","desc":"成功解析正文","owner":{"name":"UP 主"},"duration":123}}""");
            if (url.AbsolutePath.Contains("getAppAccessToken")) return Json("""{"access_token":"fake-token","expires_in":"7200"}""");
            if (url.AbsolutePath.Contains("chat/completions")) Interlocked.Increment(ref llmCalls);
            if (url.AbsolutePath.Contains("/messages")) sent.TrySetResult((url.AbsoluteUri, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Json("{}");
        });
        var config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.MarkdownReply = markdown;
        config.Config.Llm.MaxOutputLines = 1;
        config.Config.Llm.MaxOutputChars = 1;
        var queue = provider.GetRequiredService<SendQueue>();
        await queue.StartAsync(default);
        try
        {
            var results = await provider.GetRequiredService<MessagePreprocessorPipeline>().ProcessAsync(Context(video ? Video : GitHub), default);
            Assert.Equal("ok", Assert.Single(results).Content.Status);
            var delivery = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("/v2/groups/group/messages", delivery.Url);
            using var body = JsonDocument.Parse(delivery.Body);
            Assert.Equal(markdown ? 2 : 0, body.RootElement.GetProperty("msg_type").GetInt32());
            Assert.Equal("incoming-id", body.RootElement.GetProperty("msg_id").GetString());
            string text = markdown ? body.RootElement.GetProperty("markdown").GetProperty("content").GetString()!
                : body.RootElement.GetProperty("content").GetString()!;
            Assert.Contains("资料标题", text);
            if (video) Assert.Contains("成功解析正文", text);
            else
            {
                Assert.Contains("GitHub Issue", text);
                Assert.DoesNotContain("成功解析正文", text);
            }
            if (markdown) Assert.Contains(video ? "[立即播放]" : "[查看原链接]", text);
            else Assert.Contains("来源：", text);
            Assert.Equal(0, llmCalls);
        }
        finally { await queue.StopAsync(default); }
        Assert.DoesNotContain(provider.GetRequiredService<HistoryStore>().GetRecent("qq:group", 10000, out _),
            entry => entry.Content.Contains("成功解析正文"));
    }

    [Fact]
    public async Task 每个解析器合并成功结果且格式不受热切换影响()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.MarkdownReply = true;
        await provider.GetRequiredService<ParsedContentSender>().SendAsync(Context(GitHub), "GitHub 链接",
            [new(GitHub, Text: "标题：第一条\n正文1"), new(GitHub, Text: "重复"), new(GitHub + "8", Text: "标题：第二条\n正文2"), new("失败", "timeout", Error: "超时")], default);
        config.Config.MarkdownReply = false;
        var task = Assert.Single(provider.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.True(task.Markdown);
        Assert.True(task.ExcludeFromContext);
        Assert.Contains("第一条", task.Content);
        Assert.Contains("第二条", task.Content);
        Assert.DoesNotContain("超时", task.Content);
        Assert.DoesNotContain("重复", task.Content);
    }

    [Fact]
    public void B站卡片包含封面统计UP时间简介与播放入口()
    {
        var page = new BilibiliReader().Read(new Uri(Video),
            """{"code":0,"data":{"title":"职场精英","pic":"http://i0.hdslb.com/bfs/archive/cover.jpg","owner":{"name":"吃货治零"},"pubdate":1791692280,"duration":168,"desc":"实际视频简介","stat":{"view":648,"danmaku":3,"like":8}}}""");
        var result = new PreprocessedText(Video, Text: "供模型读取") { DisplayCard = page.DisplayCard };
        string text = ParsedContentSender.Format("视频链接", [result], true);
        Assert.Contains("![视频封面 #640px #360px](https://i0.hdslb.com/bfs/archive/cover.jpg)", text);
        Assert.Contains("▶ 648", text);
        Assert.Contains("💬 3", text);
        Assert.Contains("02:48", text);
        Assert.Contains("UP：吃货治零", text);
        Assert.Contains("2026/10/11 12:18", text);
        Assert.Contains("## 职场精英", text);
        Assert.Contains("简介：实际视频简介", text);
        Assert.Contains("[立即播放](https://player.bilibili.com/player.html?bvid=BV16Faz6tE4F&p=1&autoplay=true)", text);
        Assert.Contains($"[原网页]({Video})", text);
        Assert.DoesNotContain("AI 总结", text);
        string plain = ParsedContentSender.Format("视频链接", [result], false);
        Assert.DoesNotContain("![", plain);
        Assert.Contains("职场精英", plain);
    }

    [Fact]
    public async Task 实际发送保留封面哈希同时继续脱敏正文中的OpenID()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        provider.GetRequiredService<RuntimeConfig>().Config.MarkdownReply = true;
        const string cover = "https://i0.hdslb.com/bfs/archive/abcdef1234567890abcdef1234567890abcdef12.jpg";
        var result = new PreprocessedText(Video, Text: "正文")
        {
            DisplayCard = new("bilibili", "标题", "正文中的用户标识：11223344556677889900aabbccddeeff", [new("UP", "UP 主")], cover)
        };
        await provider.GetRequiredService<ParsedContentSender>().SendAsync(Context(Video), "视频链接", [result], default);
        string text = Assert.Single(provider.GetRequiredService<SendQueue>().PeekPendingForTest()).Content;
        Assert.Contains(cover, text);
        Assert.DoesNotContain("11223344556677889900aabbccddeeff", text);
        Assert.Contains("[ID]", text);
        Assert.DoesNotContain("RAINBOT_PUBLIC_URL", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitHub卡片只显示元信息且README仍提供给模型(bool markdown)
    {
        await using var provider = await TestHost.BuildReadyAsync(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/readme")
            ? JsonSerializer.Serialize(new { type = "file", encoding = "base64", size = 30, content = Convert.ToBase64String(Encoding.UTF8.GetBytes("唯一README内容")) })
            : """{"full_name":"owner/repo","description":"仓库简介","language":"C#","stargazers_count":100,"forks_count":5,"default_branch":"main","license":{"spdx_id":"MIT"},"updated_at":"2026-10-11T04:18:00Z"}"""));
        provider.GetRequiredService<RuntimeConfig>().Config.MarkdownReply = markdown;
        var result = Assert.Single(await provider.GetRequiredService<MessagePreprocessorPipeline>().ProcessAsync(Context("https://github.com/owner/repo"), default));
        Assert.Contains("唯一README内容", result.Content.Text);
        string text = Assert.Single(provider.GetRequiredService<SendQueue>().PeekPendingForTest()).Content;
        Assert.Contains("owner/repo", text);
        Assert.Contains("仓库简介", text);
        Assert.Contains("Star", text);
        Assert.Contains("100", text);
        Assert.Contains("MIT", text);
        Assert.Contains("2026/10/11 12:18", text);
        Assert.DoesNotContain("README", text);
        Assert.DoesNotContain("唯一README内容", text);
        Assert.DoesNotContain("![", text);
    }

    [Theory]
    [InlineData("https://example.com/cover.jpg")]
    [InlineData("https://i0.hdslb.com.evil.example/cover.jpg")]
    [InlineData("https://user:pass@i0.hdslb.com/cover.jpg")]
    [InlineData("https://i0.hdslb.com:8080/cover.jpg")]
    public void 封面只接受B站公网CDN(string url) => Assert.Empty(BilibiliReader.NormalizeCover(url));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 多张卡片也遵守总预算且数据不会生成额外Markdown图片(bool video)
    {
        var results = Enumerable.Range(0, 4).Select(index => new PreprocessedText("https://github.com/owner/" + new string('a', 100), Text: "正文")
        {
            DisplayCard = new(video ? "bilibili" : "GitHub 仓库", new string('*', 300), new string('文', 5000),
                [new("UP", new string('字', 100)), new("发布", "2026/10/11 12:18")], "https://i0.hdslb.com/cover.jpg")
        }).ToList();
        string text = ParsedContentSender.Format(video ? "视频链接" : "GitHub 链接", results, true);
        Assert.True(text.Length <= ParsedContentSender.MaxMessageChars);
        var dangerous = new PreprocessedText(Video, Text: "正文")
        { DisplayCard = new("bilibili", "![恶意](https://evil.example/pic)", "<@!user> <qqbot-image>", [], "https://evil.example/cover") };
        string escaped = ParsedContentSender.Format("视频链接", [dangerous], true);
        Assert.DoesNotContain("![恶意]", escaped);
        Assert.DoesNotContain("<@!", escaped);
        Assert.DoesNotContain("<qqbot-", escaped);
    }

    [Fact]
    public async Task 私聊与全量消息沿用回程规则()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var context = new MessagePreprocessContext(new IncomingMessage
        {
            BotId = "qq", GroupOpenId = "qq:puser", SenderOpenId = "user", MsgId = "private-id",
            Content = GitHub, IsPrivate = true, IsFullMessage = true
        }, [], "", [], [], DateTimeOffset.UtcNow);
        await provider.GetRequiredService<ParsedContentSender>().SendAsync(context, "GitHub 链接", [new(GitHub, Text: "正文")], default);
        var task = Assert.Single(provider.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Equal("qq", task.BotId);
        Assert.Equal("qq:puser", task.GroupOpenId);
        Assert.True(task.IsPrivate);
        Assert.Null(task.MsgId);
    }

    [Fact]
    public async Task 关闭开关保留解析资料并且失败空结果不额外发送()
    {
        await using var provider = await TestHost.BuildReadyAsync(request =>
            Json("""{"title":"标题","number":7,"body":"正文"}"""));
        var config = provider.GetRequiredService<RuntimeConfig>();
        Assert.Null(await config.SetAsync("GitHub.SendParsedContent", "false"));
        Assert.Null(await config.SetAsync("Page.SendParsedContent", "false"));
        var results = await provider.GetRequiredService<MessagePreprocessorPipeline>().ProcessAsync(Context(GitHub), default);
        Assert.Equal("ok", Assert.Single(results).Content.Status);
        var sender = provider.GetRequiredService<ParsedContentSender>();
        await sender.SendAsync(Context(GitHub), "GitHub 链接", [], default);
        await sender.SendAsync(Context(GitHub), "GitHub 链接", [new(GitHub, "timeout", Error: "未读取")], default);
        Assert.Empty(provider.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Null(await config.ResetAsync("GitHub.SendParsedContent"));
        Assert.Null(await config.ResetAsync("Page.SendParsedContent"));
        Assert.True(config.Config.GitHub.SendParsedContent);
        Assert.True(config.Config.Page.SendParsedContent);
    }

    [Fact]
    public async Task 回放跳过副作用和取消不发送资料()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var sender = provider.GetRequiredService<ParsedContentSender>();
        var context = new MessagePreprocessContext(new IncomingMessage
        { BotId = "qq", GroupOpenId = "qq:group", SenderOpenId = "user", MsgId = "replay-id", Content = GitHub, SkipSideEffects = true }, [], "", [], [], DateTimeOffset.UtcNow);
        await sender.SendAsync(context, "GitHub 链接", [new(GitHub, Text: "正文")], default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Context(GitHub), "GitHub 链接", [new(GitHub, Text: "正文")], cancellation.Token));
        Assert.Empty(provider.GetRequiredService<SendQueue>().PeekPendingForTest());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 格式化有总长度预算并且外部文本不能生成QQ标签(bool markdown)
    {
        var results = Enumerable.Range(1, 4).Select(index => new PreprocessedText(GitHub + index,
            Text: "标题：" + new string('*', 300) + "\n<@!other> <qqbot-image> " + new string('文', 5000))).ToList();
        string text = ParsedContentSender.Format("GitHub 链接", results, markdown);
        Assert.True(text.Length <= ParsedContentSender.MaxMessageChars);
        Assert.Contains("内容已截断", text);
        Assert.DoesNotContain("<@!", text);
        Assert.DoesNotContain("<qqbot-", text);
    }

    [Fact]
    public void 风控兜底格式包含播放与原网页链接且必须有BV号()
    {
        string? text = ParsedContentSender.RiskFallback(
        [
            new(Video, "risk", Error: "B 站风控拦截（HTTP 412），未取得视频信息。"),
            new("https://b23.tv/no-bv", "risk", Error: "B 站风控拦截"),
            new(Video, "ok", Text: "成功资料")
        ]);
        Assert.NotNull(text);
        Assert.Contains("## B 站链接解析失败", text);
        Assert.Contains("[立即播放](https://player.bilibili.com/player.html?bvid=BV16Faz6tE4F&p=1&autoplay=true)", text);
        Assert.Contains($"[原网页]({Video})", text);
        Assert.DoesNotContain("b23.tv", text);
        Assert.Null(ParsedContentSender.RiskFallback([new(GitHub, "risk", Error: "风控")]));
        Assert.Null(ParsedContentSender.RiskFallback([new(Video, "timeout", Error: "超时")]));
    }

    [Fact]
    public async Task B站风控兜底仅在Markdown模式发送且走输出过滤()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var sender = provider.GetRequiredService<ParsedContentSender>();
        var queue = provider.GetRequiredService<SendQueue>();
        var results = new List<PreprocessedText>
        {
            new(Video, "risk", Error: "B 站风控拦截（HTTP 412），未取得视频信息。"),
            new("https://b23.tv/no-bv", "risk", Error: "B 站风控拦截")
        };
        await sender.SendAsync(Context(Video), "视频链接", results, default);
        Assert.Empty(queue.PeekPendingForTest()); // MarkdownReply 默认关闭，纯文本发不了可点链接
        provider.GetRequiredService<RuntimeConfig>().Config.MarkdownReply = true;
        await sender.SendAsync(Context(Video), "视频链接", results, default);
        var task = Assert.Single(queue.PeekPendingForTest());
        Assert.True(task.Markdown);
        Assert.True(task.ExcludeFromContext);
        Assert.Contains("[立即播放](https://player.bilibili.com/player.html?bvid=BV16Faz6tE4F&p=1&autoplay=true)", task.Content);
        Assert.Contains($"[原网页]({Video})", task.Content);
        Assert.DoesNotContain("b23.tv", task.Content);
        Assert.DoesNotContain("RAINBOT_PUBLIC_URL", task.Content);
    }

    [Fact]
    public async Task 成功卡片与风控兜底互不挤占()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        provider.GetRequiredService<RuntimeConfig>().Config.MarkdownReply = true;
        await provider.GetRequiredService<ParsedContentSender>().SendAsync(Context(Video), "视频链接",
        [
            new(Video, Text: "标题：成功视频\n正文"),
            new("https://www.bilibili.com/video/BV1xx411c7mD/", "risk", Error: "B 站风控拦截（HTTP 412），未取得视频信息。")
        ], default);
        var tasks = provider.GetRequiredService<SendQueue>().PeekPendingForTest();
        Assert.Equal(2, tasks.Count);
        Assert.Contains("成功视频", tasks[0].Content);
        Assert.Contains("BV1xx411c7mD", tasks[1].Content);
    }

    private static MessagePreprocessContext Context(string url) => new(new IncomingMessage
    { BotId = "qq", GroupOpenId = "qq:group", SenderOpenId = "user", MsgId = "incoming-id", Content = url }, [], "", [], [], DateTimeOffset.UtcNow);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
