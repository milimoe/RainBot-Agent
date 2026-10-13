using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Commands;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Profile;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class ContextMaintenanceTests
{
    private const string Group = "qq:maintenance";

    [Fact]
    public async Task 压缩快照仅移除旧消息不误删压缩期间到达的消息()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        sp.GetRequiredService<RuntimeConfig>().Config.Context.MaxHistoryPerGroup = 6;
        for (int i = 0; i < 6; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "旧" + i));
        var snapshot = history.GetRecent(Group, int.MaxValue, out _);
        for (int i = 0; i < 4; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "bob", "新" + i));
        history.CompactSnapshot(Group, snapshot, 1);
        var remaining = history.GetRecent(Group, int.MaxValue, out _);
        Assert.Equal(4, remaining.Count(m => m.UserOpenId == "bob"));
        Assert.Single(remaining, m => m.UserOpenId == "alice");
    }

    [Fact]
    public async Task 重置与工作流共享锁避免进行中请求写回()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var gate = states.ContextLock(Group);
        await gate.WaitAsync();
        Task reset = states.ResetContextAsync(Group);
        Assert.False(reset.IsCompleted);
        gate.Release();
        await reset;
        Assert.Equal(1, states.GetOrCreate(Group).ContextVersion);
    }

    [Fact]
    public async Task 后台画像旧快照不覆盖前台新更新()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        await db.BumpUserActivityAsync(Group, "alice");
        var old = (await db.GetUserProfileAsync(Group, "alice"))!;
        string version = old.ProfileUpdatedAt;
        var fresh = (await db.GetUserProfileAsync(Group, "alice"))!;
        fresh.Interests = "前台刚确认的新兴趣";
        Assert.True(await db.TryUpdateUserProfileAsync(fresh, 10, false));
        old.Interests = "后台旧兴趣";
        Assert.False(await db.TryUpdateUserProfileAsync(old, 10, false, version));
        Assert.Equal("前台刚确认的新兴趣", (await db.GetUserProfileAsync(Group, "alice"))!.Interests);
    }

    [Fact]
    public async Task 重置清空模型历史和记忆但保留聊天归档与用户画像()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        var states = sp.GetRequiredService<GroupStateManager>();
        var history = sp.GetRequiredService<HistoryStore>();
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "旧对话"));
        var group = await states.GetProfileAsync(Group);
        group.Summary = "旧群记忆";
        await db.UpsertGroupProfileAsync(group);
        await db.SaveDistillSummaryAsync(Group, "旧摘要");
        await db.BumpUserActivityAsync(Group, "alice", "Alice");
        await history.AppendAsync("qq:other", TestHelpers.Msg("qq:other", "bob", "其他群对话"));
        await states.ResetContextAsync(Group);
        Assert.Equal(0, history.Count(Group));
        Assert.Equal(1, history.Count("qq:other"));
        Assert.Empty(await db.GetRecentDistillSummariesAsync(Group));
        Assert.Empty((await db.GetGroupProfileAsync(Group))!.Summary);
        Assert.Single(await db.GetRecentMessagesAsync(Group, 10));
        Assert.NotNull(await db.GetUserProfileAsync(Group, "alice"));
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext { GroupOpenId = Group, Type = TriggerType.Passive, Reason = "测试" });
        Assert.DoesNotContain("旧对话", compose.Messages[2].Content);
        Assert.DoesNotContain("旧摘要", compose.Messages[1].Content);
    }

    [Fact]
    public async Task 重置后的后台旧摘要无法写回()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        await sp.GetRequiredService<GroupStateManager>().GetProfileAsync(Group);
        string marker = (await db.GetGroupProfileAsync(Group))!.LastReset;
        await sp.GetRequiredService<GroupStateManager>().ResetContextAsync(Group);
        await db.SaveDistillSummaryAsync(Group, "重置前的后台结果", marker);
        Assert.Empty(await db.GetRecentDistillSummariesAsync(Group));
    }

    [Fact]
    public async Task compact仅管理员可执行成功后摘要可用且保留尾部()
    {
        await using var sp = await TestHost.BuildReadyAsync(_ => FakeHttpClientFactoryResponse("[\"ualice 喜欢摄影\"]"));
        var history = sp.GetRequiredService<HistoryStore>();
        sp.GetRequiredService<RuntimeConfig>().Config.Context.DistillKeepMessages = 2;
        for (int i = 0; i < 6; i++) await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "摄影" + i));
        var commands = sp.GetRequiredService<CommandParser>();
        var command = commands.Parse("/compact")!;
        Assert.Equal(CommandKind.Compact, command.Kind);
        Assert.Contains("管理员", await commands.ExecuteAsync(command, Group, "alice", false));
        Assert.Equal(6, history.Count(Group));
        Assert.Contains("已压缩", await commands.ExecuteAsync(command, Group, "alice", true));
        Assert.Equal(2, history.Count(Group));
        Assert.Single(await sp.GetRequiredService<Database>().GetRecentDistillSummariesAsync(Group));
        Assert.False(sp.GetRequiredService<GroupStateManager>().GetOrCreate(Group).Degraded);
    }

    [Fact]
    public async Task compact模型返回无效内容时不删历史()
    {
        await using var sp = await TestHost.BuildReadyAsync(_ => FakeHttpClientFactoryResponse("这不是有效摘要"));
        var history = sp.GetRequiredService<HistoryStore>();
        await history.AppendAsync(Group, TestHelpers.Msg(Group, "alice", "应保留"));
        Assert.Contains("失败", await sp.GetRequiredService<ContextCompactor>().CompactAsync(Group));
        Assert.Equal(1, history.Count(Group));
        Assert.Empty(await sp.GetRequiredService<Database>().GetRecentDistillSummariesAsync(Group));
    }

    [Fact]
    public async Task 暂停持久化且不重连仍记录发言但不生成回复()
    {
        int modelCalls = 0;
        await using var sp = await TestHost.BuildReadyAsync(_ => { modelCalls++; return FakeHttpClientFactoryResponse("回复"); });
        var bots = sp.GetRequiredService<BotInstanceStore>();
        int changes = 0;
        bots.InstancesChanged += () => changes++;
        Assert.Null(await bots.SetPausedAsync("qq", true));
        Assert.Equal(0, changes);
        await sp.GetRequiredService<MessageProcessor>().ProcessAsync(TestHelpers.Msg(Group, "alice", "你好", isAt: true), default);
        Assert.Equal(0, modelCalls);
        Assert.Equal(1, sp.GetRequiredService<HistoryStore>().Count(Group));
        Assert.Empty(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        await bots.InitializeAsync();
        Assert.True(bots.Get("qq")!.Paused);
        await bots.SetPausedAsync("qq", false);
        Assert.False(bots.Get("qq")!.Paused);
    }

    [Fact]
    public async Task 同前缀用户的新标识不同旧标识歧义不乱选()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        const string one = "12345678-alice", two = "12345678-bob";
        var db = sp.GetRequiredService<Database>();
        await db.BumpUserActivityAsync(Group, one, "同名");
        await db.BumpUserActivityAsync(Group, two, "同名");
        Assert.NotEqual(UserIdentityResolver.ShortId(one), UserIdentityResolver.ShortId(two));
        var ids = sp.GetRequiredService<UserIdentityResolver>();
        Assert.Equal(one, (await ids.ResolveAsync(Group, UserIdentityResolver.ShortId(one))).User!.OpenId);
        Assert.Null((await ids.ResolveAsync(Group, "u12345678")).User);
    }

    [Fact]
    public async Task 画像后台从本人发言补建并避免频繁重写与混入他人()
    {
        string body = "";
        int calls = 0;
        await using var sp = await TestHost.BuildReadyAsync(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            calls++;
            return FakeHttpClientFactoryResponse("{\"tags\":[\"摄影\"],\"interests\":\"喜欢摄影\"}");
        });
        var db = sp.GetRequiredService<Database>();
        for (int i = 0; i < 8; i++) await db.BumpUserActivityAsync(Group, "alice", "Alice");
        for (int i = 0; i < 3; i++) await db.InsertMessageAsync("own-" + i, Group, "alice", "我经常摄影拍鸟", false, DateTimeOffset.UtcNow);
        await db.InsertMessageAsync("other", Group, "bob", "他人独有秘密话题", false, DateTimeOffset.UtcNow);
        var service = new ProfileMaintenanceService(db, sp.GetRequiredService<DeepSeekClient>(), sp.GetRequiredService<AnchorManager>(), sp.GetRequiredService<RuntimeConfig>(), sp.GetRequiredService<BotInstanceStore>(), NullLogger<ProfileMaintenanceService>.Instance);
        Assert.Equal(1, await service.RefreshGroupAsync(Group));
        Assert.DoesNotContain("他人独有秘密话题", body);
        Assert.Contains("摄影", (await db.GetUserProfileAsync(Group, "alice"))!.Interests);
        Assert.Equal(0, await service.RefreshGroupAsync(Group));
        Assert.Equal(1, calls);
        Assert.Contains("摄影", await sp.GetRequiredService<ProfileRecaller>().RecallAsync(Group, "alice"));
    }

    [Fact]
    public async Task 回显与他人引用保留身份信息()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        var botMessage = new IncomingMessage { MsgId = "bot-echo", GroupOpenId = Group, SenderOpenId = "$bot", Content = "机器人的原话", IsFromBot = true };
        await history.AppendAsync(Group, botMessage);
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = Group, Type = TriggerType.Passive, Reason = "测试", SenderOpenId = "alice", CurrentContent = "<@bob> 说得对",
            PendingMessages = [new("bob", "Bob", "我引用Alice", "Alice的原文")], RecalledProfile = "Alice喜欢摄影"
        });
        Assert.Contains("你此前发送的回复", compose.Messages[2].Content);
        Assert.Contains("@ubob", compose.Messages[3].Content);
        Assert.Contains("此人引用他人的消息", compose.Messages[3].Content);
        Assert.Contains("当前触发者 ualice 的画像", compose.Messages[3].Content);
    }

    private static System.Net.Http.HttpResponseMessage FakeHttpClientFactoryResponse(string content) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(TestHelpers.LlmTextResponse(content), System.Text.Encoding.UTF8, "application/json")
    };
}
