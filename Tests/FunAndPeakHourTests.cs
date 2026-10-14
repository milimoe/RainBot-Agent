using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Config;
using RainBot.Services.Fun;
using RainBot.Services.QQ;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class FunAndPeakHourTests
{
    [Theory]
    [InlineData("作品相关素材均来源于网络。。", "络网于源来均材素关相品作。")]
    [InlineData("甲乙丙。", "丙乙甲。")]
    [InlineData("甲😀乙。。", "乙😀甲。")]
    [InlineData("甲é乙。", "乙é甲。")]
    [InlineData("甲👩‍💻乙。", "乙👩‍💻甲。")]
    [InlineData("。。。", "。")]
    public void 反转句号保留完整字符并只保留一个末尾句号(string input, string expected)
    {
        Assert.True(FunService.TryReversePeriod(input, out string result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("普通消息")]
    [InlineData("半角句号.")]
    [InlineData("句号。在中间")]
    public void 非全角句号结尾不触发(string text) => Assert.False(FunService.TryReversePeriod(text, out _));

    [Fact]
    public void 超长内容只反转句号前最后五十个字符()
    {
        string body = "丢弃前缀" + new string('甲', 49) + "乙";
        Assert.True(FunService.TryReversePeriod(body + "。。", out var result));
        Assert.Equal("乙" + new string('甲', 49) + "。", result);
        Assert.Equal(51, StringInfo.ParseCombiningCharacters(result).Length);
    }

    [Fact]
    public async Task 反转句号开启后优先于其他娱乐功能且开关可热改()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        Assert.Null(await config.SetAsync("Fun.EnableReversePeriod", "true"));
        config.Config.Fun.ReplyNoProbability = 100;
        config.Config.Fun.RepeatProbability = 100;
        config.Config.Fun.ReverseAtProbability = 100;
        var result = await sp.GetRequiredService<FunService>().TryRespondAsync(TestHelpers.Msg("group", "alice", "不玩了。。", isAt: true));
        Assert.True(result.Blocked);
        Assert.Equal("了玩不。", Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest()).Content);
        Assert.Null(await config.ResetAsync("Fun.EnableReversePeriod"));
        Assert.False(config.Config.Fun.EnableReversePeriod);
    }

    [Theory]
    [InlineData("2026-10-14T08:59:59+08:00", false)]
    [InlineData("2026-10-14T09:00:00+08:00", true)]
    [InlineData("2026-10-14T11:59:59+08:00", true)]
    [InlineData("2026-10-14T12:00:00+08:00", false)]
    [InlineData("2026-10-14T13:59:59+08:00", false)]
    [InlineData("2026-10-14T14:00:00+08:00", true)]
    [InlineData("2026-10-14T17:59:59+08:00", true)]
    [InlineData("2026-10-14T18:00:00+08:00", false)]
    [InlineData("2026-10-14T01:00:00Z", true)]
    public async Task 高峰不插按北京时间生效但不影响艾特(string time, bool peak)
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        config.Config.Trigger.RandomChatProbability = 100;
        Assert.Null(await config.SetAsync("Trigger.RandomChatSkipPeakHours", "true"));
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        var now = DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
        Assert.Equal(!peak, trigger.ShouldRandomChat(TestHelpers.Msg("group", "alice", "聊聊"), now));
        Assert.True(trigger.ShouldTrigger(TestHelpers.Msg("group", "alice", "你好", isAt: true), now));
        config.Config.Trigger.RandomChatSkipPeakHours = false;
        Assert.True(trigger.ShouldRandomChat(TestHelpers.Msg("group", "alice", "聊聊"), now));
    }

    [Fact]
    public async Task 链接资料先发送即使命中阻断型娱乐功能也会提取()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        config.Config.Fun.EnableRepeat = true;
        config.Config.Fun.RepeatProbability = 100;
        config.Config.Trigger.RandomChatProbability = 0;
        var processor = new MessageProcessor(
            sp.GetRequiredService<GroupStateManager>(), sp.GetRequiredService<RainBot.Services.Safety.InputFilter>(),
            sp.GetRequiredService<RainBot.Services.Context.HistoryStore>(), sp.GetRequiredService<RainBot.Services.Commands.CommandParser>(),
            sp.GetRequiredService<PassiveTrigger>(), sp.GetRequiredService<RainBot.Services.Profile.ProfileRecaller>(),
            sp.GetRequiredService<RainBot.Services.Workflow.WorkflowRunner>(), sp.GetRequiredService<RainBot.Services.Safety.OutputFilter>(),
            sp.GetRequiredService<SendQueue>(), sp.GetRequiredService<FunService>(), config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageProcessor>.Instance,
            preprocessors: new RainBot.Services.Preprocessing.MessagePreprocessorPipeline([new TestLinkProcessor()],
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RainBot.Services.Preprocessing.MessagePreprocessorPipeline>.Instance,
                new RainBot.Services.Preprocessing.ParsedContentSender(sp.GetRequiredService<SendQueue>(), config,
                    sp.GetRequiredService<RainBot.Services.Safety.OutputFilter>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<RainBot.Services.Preprocessing.ParsedContentSender>.Instance)));
        await processor.ProcessAsync(TestHelpers.Msg("group", "alice", "https://example.com/article"), default);
        var pending = sp.GetRequiredService<SendQueue>().PeekPendingForTest();
        Assert.Equal(2, pending.Count);
        Assert.Contains("已提取的链接正文", pending[0].Content);
        Assert.Contains("https://example.com/article", pending[1].Content);
    }

    private sealed class TestLinkProcessor : RainBot.Services.Preprocessing.IMessagePreprocessor
    {
        public string Name => "test-link";
        public bool SendParsedContent => true;
        public Task<IReadOnlyList<RainBot.Services.Preprocessing.PreprocessedText>> ProcessAsync(
            RainBot.Services.Preprocessing.MessagePreprocessContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RainBot.Services.Preprocessing.PreprocessedText>>([
                new("https://example.com/article", Text: "已提取的链接正文")]);
    }
}
