using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.WebUi;
using Xunit;

namespace RainBot.Tests;

public class WebUiNicknameTests
{
    [Fact]
    public async Task 私聊昵称随历史持久化_无需创建群友画像()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var message = new IncomingMessage { MsgId = "private", SenderOpenId = "alice", GroupOpenId = "bot:palice", Content = "hi", Username = "私聊小明", IsPrivate = true };
        await provider.GetRequiredService<Services.Trigger.GroupStateManager>().OnMessageAsync(message);
        await provider.GetRequiredService<Services.Context.HistoryStore>().AppendAsync(message.GroupOpenId, message);
        var db = provider.GetRequiredService<Database>();
        Assert.Null(await db.GetUserProfileAsync(message.GroupOpenId, "alice"));
        Assert.Equal("私聊小明", Assert.Single(await db.GetRecentMessagesAsync(message.GroupOpenId, 1)).Nickname);
        Assert.Equal("私聊小明", await provider.GetRequiredService<UserIdentityResolver>().GetNicknameAsync(message.GroupOpenId, "alice"));
        await db.InitializeAsync(); // 新列迁移重复运行不会失败。
    }

    [Fact]
    public async Task 历史和提及共用当前昵称_群与实例隔离_机器人不作群友解析()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var db = provider.GetRequiredService<Database>();
        var identities = provider.GetRequiredService<UserIdentityResolver>();
        await db.BumpUserActivityAsync("bot1:g", "alice", "小明");
        await db.BumpUserActivityAsync("bot1:g", "bob", "小红");
        await db.BumpUserActivityAsync("bot2:g", "alice", "另一个群的名字");
        var names = await identities.GetDisplayNamesAsync("bot1:g", ["alice", WebUiBridge.BotMarker, "unknown"], ["hi <@!bob>"]);
        Assert.Equal("小明", names["alice"]);
        Assert.Equal("小红", names["bob"]);
        Assert.False(names.ContainsKey(WebUiBridge.BotMarker));
        Assert.False(names.ContainsKey("unknown"));
        identities.LearnNickname("bot1:g", "alice", "新昵称");
        names = await identities.GetDisplayNamesAsync("bot1:g", ["alice"], []);
        Assert.Equal("新昵称", names["alice"]);
        Assert.Equal("另一个群的名字", (await identities.GetDisplayNamesAsync("bot2:g", ["alice"], []))["alice"]);
    }

    [Fact]
    public async Task 实时事件缺昵称时回退数据库_内容使用渲染文本_统一短标识()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        var db = provider.GetRequiredService<Database>();
        var identities = provider.GetRequiredService<UserIdentityResolver>();
        await db.BumpUserActivityAsync("bot:g", "alice123456", "已有昵称");
        var bus = new WebUiEventBus();
        var bridge = new WebUiBridge(bus, db, identities, NullLogger<WebUiBridge>.Instance);
        await bridge.PublishMemberMessageAsync(new IncomingMessage
        {
            GroupOpenId = "bot:g", SenderOpenId = "alice123456", MsgId = "test", Content = "<@bob>", ContextText = "@小红 你好"
        });
        var data = Assert.Single(bus.Recent()).Data!;
        Assert.Equal("已有昵称", data["username"]!.GetValue<string>());
        Assert.Equal(UserIdentityResolver.ShortId("alice123456"), data["shortId"]!.GetValue<string>());
        Assert.Equal("@小红 你好", data["content"]!.GetValue<string>());
        Assert.Equal("已有昵称", data["names"]!["alice123456"]!.GetValue<string>());
    }
}
