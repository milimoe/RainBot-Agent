using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class TokenContextGovernanceTests
{
    private const string Group = "qq:token-governance";
    private static TriggerContext Context() => new() { GroupOpenId = Group, Type = TriggerType.Passive, Reason = "测试" };
    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(TestHelpers.LlmTextResponse(content))
    };

    [Fact]
    public async Task 默认千条短历史仍保留且尾部追加不改变旧前缀()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        var composer = sp.GetRequiredService<BlockComposer>();
        for (int i = 0; i < 1100; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "消息" + i));
        var first = await composer.BuildAsync(Context());
        history.AppendBotReply(Group, "最新回复");
        var second = await composer.BuildAsync(Context());
        Assert.Equal(1101, history.Count(Group));
        Assert.Equal(0, second.HistoryDropped);
        Assert.StartsWith(first.Messages[2].Content!, second.Messages[2].Content);
    }

    [Fact]
    public async Task 存储兜底在两个追加入口均为超限减半而非逐条滑动()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        sp.GetRequiredService<RuntimeConfig>().Config.Context.MaxHistoryPerGroup = 10;
        var history = sp.GetRequiredService<HistoryStore>();
        for (int i = 0; i < 11; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "消息" + i));
        Assert.Equal(5, history.Count(Group));
        Assert.Equal("消息6", history.GetRecent(Group, int.MaxValue, out _)[0].Content);
        for (int i = 0; i < 5; i++) history.AppendBotReply(Group, "回复" + i);
        Assert.Equal(10, history.Count(Group));
        history.AppendBotReply(Group, "触发裁剪");
        Assert.Equal(5, history.Count(Group));
        Assert.Equal("触发裁剪", history.GetRecent(Group, int.MaxValue, out _)[^1].Content);
    }

    [Fact]
    public async Task 预算使用渲染文本且只保留连续尾部()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "旧"));
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", new string('长', 5000)));
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "新"));
        var all = history.GetRecent(Group, int.MaxValue, out _);
        int budget = TokenEstimator.Estimate(HistoryFormatter.Header) + TokenEstimator.Estimate(HistoryFormatter.Format(all[^1])) + 1;
        var recent = history.GetRecent(Group, budget, out int dropped);
        Assert.Single(recent);
        Assert.Equal("新", recent[0].Content);
        Assert.Equal(2, dropped);
        var renderedLong = HistoryFormatter.Format(all[1]);
        Assert.True(TokenEstimator.Estimate(renderedLong) < TokenEstimator.Estimate(all[1].Content));
    }

    [Fact]
    public async Task 昵称变化不改写历史但新消息使用新昵称()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        var composer = sp.GetRequiredService<BlockComposer>();
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "旧消息", username: "旧昵称"));
        var before = await composer.BuildAsync(Context());
        await sp.GetRequiredService<Database>().BumpUserActivityAsync(Group, "alice", "新昵称");
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "新消息", username: "新昵称"));
        var after = await composer.BuildAsync(Context());
        Assert.StartsWith(before.Messages[2].Content!, after.Messages[2].Content);
        Assert.Contains("新昵称", after.Messages[2].Content);
    }

    [Fact]
    public async Task 裁剪前历史触发正常压缩合并摘要且保留新到消息并继续回复()
    {
        HistoryStore? history = null;
        string body = "";
        await using var sp = await TestHost.BuildReadyAsync(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            history!.AppendBotReply(Group, "压缩期间新到的回复");
            return Reply("[\"合并后的长期记忆\"]");
        });
        history = sp.GetRequiredService<HistoryStore>();
        var cfg = sp.GetRequiredService<RuntimeConfig>().Config.Context;
        cfg.HistoryCompressAtTokens = 1000;
        cfg.HistoryTargetTokens = 500;
        cfg.HistoryAssembleCapTokens = 700;
        for (int i = 0; i < 30; i++) await history.AppendAsync(Group,
            TestHelpers.Msg(Group, "alice", "原文" + i + new string('甲', 40)));
        var db = sp.GetRequiredService<Database>();
        await db.SaveDistillSummaryAsync(Group, "[\"必须保留的旧记忆\"]");
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(Context());
        Assert.True(compose.UntrimmedHistoryTokens > cfg.HistoryAssembleCapTokens);
        Assert.True(compose.HistoryDropped > 0);
        var action = await sp.GetRequiredService<WatermarkManager>().EnforceAsync(compose);
        Assert.Equal(WatermarkAction.Compacted, action);
        Assert.False(sp.GetRequiredService<GroupStateManager>().GetOrCreate(Group).Degraded);
        var remaining = history.GetRecent(Group, int.MaxValue, out _);
        Assert.True(remaining.Count > 3);
        Assert.Equal("压缩期间新到的回复", remaining[^1].Content);
        Assert.Contains("原文29", remaining[^2].Content);
        using var json = JsonDocument.Parse(body);
        string input = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("必须保留的旧记忆", input);
        Assert.Contains("原文0", input);
        Assert.DoesNotContain("原文29", input);
        Assert.Single(await db.GetRecentDistillSummariesAsync(Group));
        var next = await sp.GetRequiredService<BlockComposer>().BuildAsync(Context());
        Assert.Contains("合并后的长期记忆", next.Messages[1].Content);
        Assert.Equal(WatermarkAction.None, await sp.GetRequiredService<WatermarkManager>().EnforceAsync(next));
    }

    [Fact]
    public async Task 压缩无效不删历史与旧记忆且冷却防止每轮重复调用()
    {
        int calls = 0;
        await using var sp = await TestHost.BuildReadyAsync(_ => { calls++; return Reply("无效摘要"); });
        var history = sp.GetRequiredService<HistoryStore>();
        var cfg = sp.GetRequiredService<RuntimeConfig>().Config.Context;
        cfg.HistoryCompressAtTokens = 500;
        cfg.HistoryTargetTokens = 200;
        for (int i = 0; i < 20; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", new string('甲', 40)));
        var db = sp.GetRequiredService<Database>();
        await db.SaveDistillSummaryAsync(Group, "旧记忆");
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(Context());
        var manager = sp.GetRequiredService<WatermarkManager>();
        Assert.Equal(WatermarkAction.None, await manager.EnforceAsync(compose));
        Assert.Equal(WatermarkAction.None, await manager.EnforceAsync(compose));
        Assert.Equal(1, calls);
        Assert.Equal(20, history.Count(Group));
        Assert.Equal("旧记忆", Assert.Single(await db.GetRecentDistillSummariesAsync(Group)));
        Assert.False(sp.GetRequiredService<GroupStateManager>().GetOrCreate(Group).Degraded);
    }

    [Fact]
    public async Task 重置竞争导致摘要拒绝写入时不报告压缩成功()
    {
        Database? db = null;
        await using var sp = await TestHost.BuildReadyAsync(_ =>
        {
            var profile = db!.GetGroupProfileAsync(Group).GetAwaiter().GetResult()!;
            profile.LastReset = "更新后的重置标记";
            db.UpsertGroupProfileAsync(profile).GetAwaiter().GetResult();
            return Reply("[\"过期摘要\"]");
        });
        db = sp.GetRequiredService<Database>();
        var history = sp.GetRequiredService<HistoryStore>();
        var cfg = sp.GetRequiredService<RuntimeConfig>().Config.Context;
        cfg.HistoryCompressAtTokens = 200;
        cfg.HistoryTargetTokens = 100;
        for (int i = 0; i < 10; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", new string('甲', 40)));
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(Context());
        Assert.Equal(WatermarkAction.None, await sp.GetRequiredService<WatermarkManager>().EnforceAsync(compose));
        Assert.Equal(10, history.Count(Group));
        Assert.Empty(await db.GetRecentDistillSummariesAsync(Group));
    }

    [Fact]
    public async Task 每次请求前检查文本水位工具结果增长也不能越过()
    {
        int requests = 0;
        await using var sp = await TestHost.BuildReadyAsync(_ => { requests++; return Reply("答复"); });
        sp.GetRequiredService<RuntimeConfig>().Config.Context.WatermarkTokens = 100;
        var llm = sp.GetRequiredService<DeepSeekClient>();
        await llm.ChatAsync([ChatMessage.User("短问题")]);
        await Assert.ThrowsAsync<LlmException>(() => llm.ChatAsync([
            ChatMessage.User("短问题"), ChatMessage.ToolResult("call-1", new string('甲', 200))]));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task 大量积压历史按有限批次压缩可持续推进()
    {
        int requests = 0;
        await using var sp = await TestHost.BuildReadyAsync(_ => { requests++; return Reply("[\"累积记忆\"]"); });
        var cfg = sp.GetRequiredService<RuntimeConfig>().Config.Context;
        cfg.WatermarkTokens = 20000;
        cfg.HistoryCompressAtTokens = 3000;
        cfg.HistoryTargetTokens = 1000;
        cfg.HistoryAssembleCapTokens = 6000;
        var history = sp.GetRequiredService<HistoryStore>();
        for (int i = 0; i < 200; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", i + new string('甲', 200)));
        var composer = sp.GetRequiredService<BlockComposer>();
        var manager = sp.GetRequiredService<WatermarkManager>();
        for (int i = 0; i < 10; i++)
        {
            var compose = await composer.BuildAsync(Context());
            var action = await manager.EnforceAsync(compose);
            if (action == WatermarkAction.None) break;
            Assert.Equal(WatermarkAction.Compacted, action);
        }
        Assert.True(requests > 1);
        Assert.True((await composer.BuildAsync(Context())).UntrimmedHistoryTokens < cfg.HistoryCompressAtTokens);
        Assert.StartsWith("199", history.GetRecent(Group, int.MaxValue, out _)[^1].Content);
        Assert.False(sp.GetRequiredService<GroupStateManager>().GetOrCreate(Group).Degraded);
    }

    [Theory]
    [InlineData("[\"\"]", "stop")]
    [InlineData("[\"部分摘要\"]", "length")]
    [InlineData("[\"超过长度限制的摘要\"]", "stop")]
    public async Task 空事实或截断或过长摘要不能替换历史(string summary, string finishReason)
    {
        await using var sp = await TestHost.BuildReadyAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = summary }, finish_reason = finishReason } }
            }))
        });
        var cfg = sp.GetRequiredService<RuntimeConfig>().Config.Context;
        cfg.HistoryCompressAtTokens = 200;
        cfg.HistoryTargetTokens = 100;
        cfg.DistillMaxChars = 12;
        var history = sp.GetRequiredService<HistoryStore>();
        for (int i = 0; i < 10; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", new string('甲', 40)));
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(Context());
        Assert.Equal(WatermarkAction.None, await sp.GetRequiredService<WatermarkManager>().EnforceAsync(compose));
        Assert.Equal(10, history.Count(Group));
        Assert.Empty(await sp.GetRequiredService<Database>().GetRecentDistillSummariesAsync(Group));
    }

    [Fact]
    public async Task 新水位配置支持热改恢复与持久化()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        Assert.Null(await config.SetAsync("Context.HistoryCompressAtTokens", "80000"));
        Assert.Null(await config.SetAsync("Context.HistoryTargetTokens", "40000"));
        await config.InitializeAsync();
        Assert.Equal(80000, config.Config.Context.HistoryCompressAtTokens);
        Assert.Equal(40000, config.Config.Context.HistoryTargetTokens);
        Assert.NotNull(await config.SetAsync("Context.HistoryTargetTokens", "0"));
        Assert.Null(await config.ResetAsync("Context.HistoryTargetTokens"));
        Assert.Equal(38400, config.Config.Context.HistoryTargetTokens);
    }
}
