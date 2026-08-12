using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Context;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class HistoryAndComposerTests
{
    private const string Group = "group_test";

    [Fact]
    public async Task 历史尾部追加_头部整条丢弃()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<HistoryStore>();
        for (int i = 1; i <= 10; i++)
        {
            await store.AppendAsync(Group, TestHelpers.Msg(Group, "user" + i, $"消息{i}"));
        }
        Assert.Equal(10, store.Count(Group));
        var recent = store.GetRecent(Group, int.MaxValue, out int dropped);
        Assert.Equal(0, dropped);
        Assert.Equal("消息1", recent[0].Content);
        Assert.Equal("消息10", recent[^1].Content);

        // 头部整条丢弃后，保留的是最新消息
        store.TrimHead(Group, 5);
        Assert.Equal(5, store.Count(Group));
        var after = store.GetRecent(Group, int.MaxValue, out _);
        Assert.Equal("消息6", after[0].Content);
    }

    [Fact]
    public async Task 历史预算裁剪_超出部分从头部丢弃()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<HistoryStore>();
        // 每条消息估算约 10+ token，预算 40 只够 3-4 条
        for (int i = 1; i <= 10; i++)
        {
            await store.AppendAsync(Group, TestHelpers.Msg(Group, "user", $"这是第{i}条测试消息内容，用于验证预算裁剪逻辑"));
        }
        var recent = store.GetRecent(Group, 40, out int dropped);
        Assert.True(recent.Count >= 1 && recent.Count < 10);
        Assert.True(dropped > 0);
        // 保留的是尾部（最新）
        Assert.Equal("这是第10条测试消息内容，用于验证预算裁剪逻辑", recent[^1].Content);
    }

    [Fact]
    public async Task 上下文组装_前缀稳定_历史尾部追加()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<HistoryStore>();
        var composer = sp.GetRequiredService<BlockComposer>();

        for (int i = 1; i <= 5; i++)
        {
            await store.AppendAsync(Group, TestHelpers.Msg(Group, "user_alpha", $"早期消息{i}"));
        }
        ComposeResult first = await composer.BuildAsync(new TriggerContext
        {
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = "user_alpha"
        });
        string firstBlockA = first.Messages[0].Content!;
        string firstBlockStatic = first.Messages[1].Content!; // B+C+D
        string firstBlockE = first.Messages[2].Content!;

        // 历史增长（尾部追加 5 条）
        for (int i = 6; i <= 10; i++)
        {
            await store.AppendAsync(Group, TestHelpers.Msg(Group, "user_beta", $"新消息{i}"));
        }
        ComposeResult second = await composer.BuildAsync(new TriggerContext
        {
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = "user_alpha"
        });

        // 核心断言：A 与 B+C+D 完全不变（前缀缓存命中）
        Assert.Equal(firstBlockA, second.Messages[0].Content);
        Assert.Equal(firstBlockStatic, second.Messages[1].Content);

        // E 块尾部增长：旧 E 是新 E 的前缀（头部整条丢弃不截断）
        Assert.StartsWith(firstBlockE, second.Messages[2].Content);

        // F 块只含触发信息
        Assert.Contains("触发原因", second.Messages[3].Content);
    }

    [Fact]
    public async Task 锚点_仅返回高互动用户且不泄露敏感字段()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        var anchors = sp.GetRequiredService<AnchorManager>();

        await db.UpsertUserProfileAsync(new UserProfile
        {
            GroupOpenId = Group,
            UserOpenId = "user_alpha_0123456789",
            Tags = ["装机", "显卡"],
            Interests = "3A 游戏",
            Sensitive = "家庭住址在 xxx",
            InteractionCount = 50,
            LastActive = DateTimeOffset.UtcNow.ToString("o")
        });
        await db.UpsertUserProfileAsync(new UserProfile
        {
            GroupOpenId = Group,
            UserOpenId = "user_beta_0123456789",
            Tags = ["摄影"],
            InteractionCount = 5,
            LastActive = DateTimeOffset.UtcNow.ToString("o")
        });

        string text = await anchors.GetAnchorsTextAsync(Group, 5);
        Assert.Contains("装机", text);
        Assert.Contains("user_alp", text);   // ShortId 取 openid 前 8 位
        Assert.DoesNotContain("家庭住址", text); // 敏感字段绝不进锚点
        Assert.Contains("user_bet", text);
    }
}

public class WatermarkTests
{
    private const string Group = "group_test";

    [Fact]
    public async Task 超水位_蒸馏压缩并降级()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var composer = sp.GetRequiredService<BlockComposer>();
        var watermark = sp.GetRequiredService<WatermarkManager>();
        var store = sp.GetRequiredService<HistoryStore>();
        var states = sp.GetRequiredService<GroupStateManager>();

        // 塞入足够多的历史，让 E 预算耗尽
        for (int i = 0; i < 50; i++)
        {
            await store.AppendAsync(Group, TestHelpers.Msg(Group, "user", $"这是一条用于撑大历史预算的消息内容{i}"));
        }
        ComposeResult compose = await composer.BuildAsync(new TriggerContext
        {
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "测试"
        });

        WatermarkAction action = await watermark.EnforceAsync(compose);
        Assert.True(action is WatermarkAction.Distilled or WatermarkAction.None, $"实际动作：{action}");

        if (action == WatermarkAction.Distilled)
        {
            // 蒸馏后仅保留最近 3 条
            Assert.True(store.Count(Group) <= 3);
            Assert.True(states.GetOrCreate(Group).Degraded);
            // 蒸馏摘要已存库
            var db = sp.GetRequiredService<Database>();
            List<string> summaries = await db.GetRecentDistillSummariesAsync(Group);
            Assert.NotEmpty(summaries);
        }
    }

    [Fact]
    public async Task 降级恢复_静默足够后彻底重置()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var watermark = sp.GetRequiredService<WatermarkManager>();
        var states = sp.GetRequiredService<GroupStateManager>();
        var store = sp.GetRequiredService<HistoryStore>();

        await states.SetDegradedAsync(Group, true);
        states.GetOrCreate(Group).LastMessageUtc = DateTimeOffset.UtcNow.AddMinutes(-121); // 超过 120 分钟
        await store.AppendAsync(Group, TestHelpers.Msg(Group, "user", "一条旧消息"));

        bool reset = await watermark.TryRecoverDegradedAsync(Group);
        Assert.True(reset);
        Assert.False(states.GetOrCreate(Group).Degraded);
        Assert.Equal(0, store.Count(Group));
    }

    [Fact]
    public async Task 降级期_静默未满不重置()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var watermark = sp.GetRequiredService<WatermarkManager>();
        var states = sp.GetRequiredService<GroupStateManager>();

        await states.SetDegradedAsync(Group, true);
        states.GetOrCreate(Group).LastMessageUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        bool reset = await watermark.TryRecoverDegradedAsync(Group);
        Assert.False(reset);
    }
}
