using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class TriggerTests
{
    private const string Group = "group_test";
    private const string User = "user_alpha";

    [Fact]
    public async Task 被动触发_非AT消息不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        var msg = TestHelpers.Msg(Group, User, "普通聊天");
        Assert.False(trigger.ShouldTrigger(msg, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 被动触发_冷却期内忽略()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        trigger.MarkTriggered(Group, now);
        var msg = TestHelpers.Msg(Group, User, "在吗", isAt: true);
        Assert.False(trigger.ShouldTrigger(msg, now.AddSeconds(10)));
        // 30 秒冷却过后恢复
        Assert.True(trigger.ShouldTrigger(msg, now.AddSeconds(31)));
    }

    [Fact]
    public async Task 被动触发_管理员无视冷却()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        trigger.MarkTriggered(Group, now);
        var msg = TestHelpers.Msg(Group, User, "在吗", isAt: true, isAdmin: true);
        Assert.True(trigger.ShouldTrigger(msg, now.AddSeconds(5)));
    }

    [Fact]
    public async Task 密度唤醒_窗口内消息达标触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 1 分钟内 10 条消息
        for (int i = 0; i < 10; i++)
        {
            states.GetOrCreate(Group).DensityWindow.Add(now.AddSeconds(-5));
            states.GetOrCreate(Group).LastMessageUtc = now.AddSeconds(-5);
        }
        string? reason = active.Evaluate(Group, now);
        Assert.NotNull(reason);
        Assert.Contains("消息", reason);
    }

    [Fact]
    public async Task 密度唤醒_消息不足不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 3; i++)
        {
            states.GetOrCreate(Group).DensityWindow.Add(now);
        }
        states.GetOrCreate(Group).LastMessageUtc = now;
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 沉默唤醒_静默超时且无管理员发言()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        string? reason = active.Evaluate(Group, now);
        Assert.NotNull(reason);
        Assert.Contains("静默", reason);
    }

    [Fact]
    public async Task 沉默唤醒_窗口内有管理员发言不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        states.GetOrCreate(Group).LastAdminMessageUtc = now.AddMinutes(-5);
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 频控_每小时超过上限不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        active.MarkActive(Group, now.AddMinutes(-10)); // 默认每小时 1 次，已用掉
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 静默群不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        await states.SetMutedAsync(Group, true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 全新群不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var active = sp.GetRequiredService<ActiveTrigger>();
        Assert.Null(active.Evaluate("brand_new_group", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 暖群尝试后冷却期内不重复触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        // 5 分钟前刚尝试过暖群（无内容静默跳过也算尝试），15 分钟冷却期内不再空转
        states.GetOrCreate(Group).LastWarmupAttemptUtc = now.AddMinutes(-5);
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 暖群尝试冷却期满后可再次触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        // 16 分钟前尝试过，冷却（15 分钟）已过 → 允许再次暖群
        states.GetOrCreate(Group).LastWarmupAttemptUtc = now.AddMinutes(-16);
        Assert.NotNull(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 暖群_机器人发言后无人接话不触发()
    {
        // 场景：网关不回显机器人消息（SendQueue 本地跟踪路径）
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GroupState state = states.GetOrCreate(Group);
        state.LastMessageUtc = now.AddMinutes(-31); // 群友最后发言 31 分钟前（满足沉默唤醒）
        // 机器人 10 分钟前发过言（被动回复/暖群），之后无人接话 → 不暖群
        states.MarkBotSpeak(Group, now.AddMinutes(-10));
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 暖群_机器人消息回显后不触发()
    {
        // 场景：网关回显机器人消息（author.bot=true），经 OnMessageAsync 识别标记
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 群友 32 分钟前发言
        await states.OnMessageAsync(TestHelpers.Msg(Group, "user_a", "大家好",
            msgTime: now.AddMinutes(-32)));
        // 机器人 31 分钟前发言且回显（ReceivedAt 即回显时间）
        await states.OnMessageAsync(new IncomingMessage
        {
            MsgId = "echo-1",
            GroupOpenId = Group,
            SenderOpenId = "bot_self_openid",
            Content = "我说完啦",
            IsAtRobot = false,
            IsFromBot = true,
            ReceivedAt = now.AddMinutes(-31)
        });
        // 最后一条消息是机器人自己 → 即使满足沉默唤醒也不触发
        Assert.Null(active.Evaluate(Group, now));
    }

    [Fact]
    public async Task 暖群_机器人发言后群友接话正常触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 机器人 32 分钟前发言
        states.MarkBotSpeak(Group, now.AddMinutes(-32));
        // 群友 31 分钟前接话（晚于机器人）→ 最后一条不是机器人，沉默唤醒照常
        states.GetOrCreate(Group).LastMessageUtc = now.AddMinutes(-31);
        string? reason = active.Evaluate(Group, now);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task 发言时间标记单调递增_不被旧值覆盖()
    {
        // 回显先到（T2）后 SendQueue 才标记（T1 < T2）：T2 不应被 T1 回退
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        DateTimeOffset t1 = DateTimeOffset.UtcNow.AddSeconds(-5);
        DateTimeOffset t2 = DateTimeOffset.UtcNow;
        states.MarkBotSpeak(Group, t2);
        states.MarkBotSpeak(Group, t1); // 旧的发送时间，不应覆盖
        Assert.Equal(t2, states.GetOrCreate(Group).LastBotSpeakUtc);
    }

    // ---------- 随机插嘴 ----------

    [Fact]
    public async Task 插嘴_概率命中且条件满足时触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100; // 必中
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        Assert.True(trigger.ShouldRandomChat(msg, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 插嘴_概率关闭不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 0; // 0 = 关闭
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        Assert.False(trigger.ShouldRandomChat(msg, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 插嘴_AT消息不参与()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        var msg = TestHelpers.Msg(Group, User, "在吗", isAt: true);
        Assert.False(trigger.ShouldRandomChat(msg, DateTimeOffset.UtcNow)); // @ 走被动路径
        Assert.True(trigger.ShouldTrigger(msg, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 插嘴_冷却期内不触发_期满恢复()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        trigger.MarkRandomChat(Group, now);
        Assert.False(trigger.ShouldRandomChat(msg, now.AddSeconds(10)));
        // 默认插嘴冷却 600 秒，期满恢复
        Assert.True(trigger.ShouldRandomChat(msg, now.AddSeconds(601)));
    }

    [Fact]
    public async Task 插嘴_被动冷却期内不触发()
    {
        // 刚 @ 回复过 → 被动冷却互斥，插嘴闭嘴（@ 响应优先，且避免连说两句）
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        trigger.MarkTriggered(Group, now.AddSeconds(-5));
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        Assert.False(trigger.ShouldRandomChat(msg, now));
        // 被动冷却（30s）过后恢复
        Assert.True(trigger.ShouldRandomChat(msg, now.AddSeconds(31)));
    }

    [Fact]
    public async Task 插嘴_机器人刚发言不触发()
    {
        // 指令回复/随机互动/暖群等发送成功会标记 MarkBotSpeak，刚说过话不接自己的话
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        var states = sp.GetRequiredService<GroupStateManager>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        states.MarkBotSpeak(Group, now.AddSeconds(-5));
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        Assert.False(trigger.ShouldRandomChat(msg, now));
        Assert.True(trigger.ShouldRandomChat(msg, now.AddSeconds(31)));
    }

    [Fact]
    public async Task 插嘴_静默群不触发()
    {
        // 管理员静默 = 只保留 @ 响应，插嘴闭嘴
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        var states = sp.GetRequiredService<GroupStateManager>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        await states.SetMutedAsync(Group, true);
        var msg = TestHelpers.Msg(Group, User, "今天好热啊");
        Assert.False(trigger.ShouldRandomChat(msg, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task 插嘴_空内容与敏感内容不触发()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        sp.GetRequiredService<RuntimeConfig>().Config.Trigger.RandomChatProbability = 100;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.False(trigger.ShouldRandomChat(TestHelpers.Msg(Group, User, "  "), now)); // 空消息没有话头
        Assert.False(trigger.ShouldRandomChat(TestHelpers.Msg(Group, User, "正常内容"), now, sensitiveContent: true)); // 敏感词不接
    }
}
