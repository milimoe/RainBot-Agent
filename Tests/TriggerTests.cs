using Microsoft.Extensions.DependencyInjection;
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
}
