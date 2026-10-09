using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Tools;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

public class UserIdentityTests
{
    private const string Group = "qq:identity-test";
    private const string Alice = "12345678aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "12345678bbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task 群消息建档_昵称空值不覆盖_排除私聊和机器人()
    {
        using var sp = await TestHost.BuildReadyAsync();
        var states = sp.GetRequiredService<GroupStateManager>();
        var db = sp.GetRequiredService<Database>();
        await states.OnMessageAsync(TestHelpers.Msg(Group, Alice, "你好", username: "小明"));
        await states.OnMessageAsync(TestHelpers.Msg(Group, Alice, "继续"));
        await states.OnMessageAsync(new IncomingMessage { GroupOpenId = Group, MsgId = "bot", SenderOpenId = Bob, Content = "hi", IsFromBot = true });
        await states.OnMessageAsync(new IncomingMessage { GroupOpenId = "qq:private", MsgId = "private", SenderOpenId = Bob, Content = "hi", IsPrivate = true });
        var profile = await db.GetUserProfileAsync(Group, Alice);
        Assert.Equal("小明", profile!.Nickname);
        Assert.Equal(2, profile.InteractionCount);
        Assert.Null(await db.GetUserProfileAsync(Group, Bob));
        Assert.Null(await db.GetUserProfileAsync("qq:private", Bob));
    }

    [Fact]
    public async Task 身份解析_处理昵称与短标识歧义_隔离群缓存()
    {
        using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        var identities = sp.GetRequiredService<UserIdentityResolver>();
        await db.BumpUserActivityAsync(Group, Alice, "同名");
        await db.BumpUserActivityAsync(Group, Bob, "同名");
        Assert.Equal(Alice, (await identities.ResolveAsync(Group, Alice)).User!.OpenId);
        Assert.Equal(2, (await identities.ResolveAsync(Group, "u12345678")).Candidates.Count);
        Assert.Null((await identities.ResolveAsync(Group, "同名")).User);
        Assert.Equal("同名", await identities.GetNicknameAsync(Group, Alice));
        identities.LearnNickname(Group, Alice, "新昵称");
        identities.LearnNickname(Group, Alice, " ");
        Assert.Equal("新昵称", await identities.GetNicknameAsync(Group, Alice));
        Assert.Null(await identities.GetNicknameAsync("qq:other", Alice));
        Assert.Empty((await identities.ResolveAsync("qq:other", Alice)).Candidates);
    }

    [Fact]
    public async Task 画像写入_配额原子限制_暖群可绕过_互动计数保持()
    {
        using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        await db.BumpUserActivityAsync(Group, Alice, "小明");
        var profile = (await db.GetUserProfileAsync(Group, Alice))!;
        profile.Interests = "摄影";
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.TryUpdateUserProfileAsync(profile, 1, false)));
        Assert.Single(results, r => r);
        await db.BumpUserActivityAsync(Group, Alice);
        Assert.True(await db.TryUpdateUserProfileAsync(profile, 1, true));
        var after = (await db.GetUserProfileAsync(Group, Alice))!;
        Assert.Equal(2, after.InteractionCount);
        Assert.Equal("摄影", after.Interests);
        Assert.NotEmpty(after.ProfileUpdatedAt);
    }

    [Fact]
    public async Task 艾特工具_活跃白名单_歧义不锁定_私聊不艾特()
    {
        using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        var registry = sp.GetRequiredService<ToolRegistry>();
        new InteractionTools(sp.GetRequiredService<UserIdentityResolver>(), db, sp.GetRequiredService<RuntimeConfig>()).Register(registry);
        await db.BumpUserActivityAsync(Group, Alice, "小明");
        await db.BumpUserActivityAsync(Group, Bob, "小明");
        var ctx = new ToolExecutionContext { GroupOpenId = Group, IsAdmin = false, AllowProfileUpdate = false };
        Assert.Contains("多位", await registry.ExecuteAsync("at_user", "{\"user\":\"小明\"}", ctx));
        Assert.Null(ctx.RequestedAtUserId);
        await registry.ExecuteAsync("at_user", "{\"user\":\"" + Alice + "\"}", ctx);
        Assert.Equal(Alice, ctx.RequestedAtUserId);
        var privateCtx = new ToolExecutionContext { GroupOpenId = Group, IsPrivate = true, IsAdmin = false, AllowProfileUpdate = false };
        await registry.ExecuteAsync("at_user", "{\"user\":\"" + Alice + "\"}", privateCtx);
        Assert.Null(privateCtx.RequestedAtUserId);
        const string old = "old-member";
        await db.BumpUserActivityAsync(Group, old, "离线", DateTimeOffset.UtcNow.AddDays(-20));
        Assert.Contains("不能艾特", await registry.ExecuteAsync("at_user", "{\"user\":\"离线\"}", ctx));
    }

    [Fact]
    public async Task 上下文_昵称仅在历史当前块_完整标识仅当前块_锚点无双冒号()
    {
        using var sp = await TestHost.BuildReadyAsync();
        var db = sp.GetRequiredService<Database>();
        await db.BumpUserActivityAsync(Group, Alice, "小明");
        var profile = (await db.GetUserProfileAsync(Group, Alice))!;
        profile.Tags = ["摄影"];
        await db.TryUpdateUserProfileAsync(profile, 1, false);
        await sp.GetRequiredService<HistoryStore>().AppendAsync(Group, TestHelpers.Msg(Group, Alice, "聊摄影", username: "旧昵称"));
        var result = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = Group, Type = TriggerType.Passive, Reason = "问候", SenderOpenId = Alice
        });
        Assert.Contains("小明(u12345678)", result.Messages[2].Content);
        Assert.DoesNotContain(Alice, result.Messages[2].Content);
        Assert.Contains(Alice, result.Messages[3].Content);
        Assert.DoesNotContain("小明", result.Messages[1].Content);
        Assert.DoesNotContain("::", result.Messages[1].Content);
    }
}
