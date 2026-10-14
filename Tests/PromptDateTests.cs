using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Context;
using Xunit;

namespace RainBot.Tests;

public class PromptDateTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData(TriggerType.Passive)]
    [InlineData(TriggerType.RandomChat)]
    [InlineData(TriggerType.Warmup)]
    public async Task 各种触发包含完整北京时间且跨日只改变尾部(TriggerType type)
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var utc = new DateTimeOffset(2026, 10, 13, 16, 1, 2, TimeSpan.Zero);
        var composer = ActivatorUtilities.CreateInstance<BlockComposer>(sp, new FixedClock(utc));
        var ctx = new TriggerContext
        {
            GroupOpenId = "prompt-date", Type = type, Reason = "测试日期",
            CreatedAt = utc.AddMinutes(-3), CurrentContent = "今天有什么新闻？"
        };
        var first = await composer.BuildAsync(ctx);
        Assert.Contains("当前北京时间：2026-10-14 00:01:02 星期三", first.Messages[3].Content);
        Assert.Contains("本轮触发时间（北京时间）：2026-10-13 23:58:02", first.Messages[3].Content);
        Assert.Contains("Asia/Shanghai，UTC+08:00", first.Messages[3].Content);
        Assert.Contains("搜索关键词中明确日期或年份", first.Messages[3].Content);
        var next = await ActivatorUtilities.CreateInstance<BlockComposer>(sp, new FixedClock(utc.AddDays(1))).BuildAsync(ctx);
        for (int i = 0; i < 3; i++) Assert.Equal(first.Messages[i].Content, next.Messages[i].Content);
        Assert.Contains("当前北京时间：2026-10-15", next.Messages[3].Content);
    }

    [Fact]
    public async Task 历史消息时间包含日期且统一转换为北京时间()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        await sp.GetRequiredService<HistoryStore>().AppendAsync("prompt-date", TestHelpers.Msg("prompt-date", "alice", "昨天的新闻",
            msgTime: new DateTimeOffset(2026, 10, 13, 16, 10, 0, TimeSpan.Zero)));
        var result = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = "prompt-date", Type = TriggerType.Passive, Reason = "测试"
        });
        Assert.Contains("[2026-10-14 00:10:00]", result.Messages[2].Content);
    }
}
