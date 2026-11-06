using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.OneBot;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 机器人昵称：配置串解析、「独立完整词」匹配，以及两条消息入口（QQ 官方全量 / OneBot11）
/// 命中后 IsAtRobot=true 的端到端断言。
/// </summary>
public class BotNicknameMatcherTests
{
    private const string Nicknames = "小雨，雨";

    // ---------- 配置串解析 ----------

    [Fact]
    public void 解析_全角半角分隔符与去重()
    {
        Assert.Equal(["小雨", "雨", "Rain"],
            BotNicknameMatcher.Parse("小雨，雨; 小雨　Rain、rain"));
        Assert.Empty(BotNicknameMatcher.Parse(""));
        Assert.Empty(BotNicknameMatcher.Parse(null));
        Assert.Empty(BotNicknameMatcher.Parse("  , ;；、　"));
    }

    // ---------- 独立完整词匹配 ----------

    [Theory]
    [InlineData("小雨，帮我查一下……")]
    [InlineData("雨 早上好！")]
    [InlineData("小雨; 帮我查一下")]
    [InlineData("雨；早上好")]
    [InlineData("雨,早上好")]
    [InlineData("小雨　在吗")]
    [InlineData("雨")]
    [InlineData("@小雨 帮我看看")]
    [InlineData("<@!ABCD1234>小雨，帮我")]
    public void 需求示例与边界标点_独立完整出现即命中(string content)
        => Assert.True(BotNicknameMatcher.Matches(content, Nicknames));

    [Theory]
    [InlineData("雨伞")]
    [InlineData("下雨了")]
    [InlineData("小雨伞")]
    [InlineData("小雨点")]
    [InlineData("今天天气怎么样")]
    [InlineData("")]
    [InlineData("   ")]
    public void 昵称嵌在其他词里或未出现_不命中(string content)
        => Assert.False(BotNicknameMatcher.Matches(content, Nicknames));

    [Fact]
    public void 空配置_任何消息都不命中()
    {
        Assert.False(BotNicknameMatcher.Matches("小雨 在吗", ""));
        Assert.False(BotNicknameMatcher.Matches("小雨 在吗", null));
        Assert.False(BotNicknameMatcher.Matches("小雨 在吗", "  , ;； "));
    }

    [Fact]
    public void 英文昵称大小写不敏感_正则元字符按字面匹配()
    {
        Assert.True(BotNicknameMatcher.Matches("rain 早上好", "Rain"));
        Assert.False(BotNicknameMatcher.Matches("Rainly 早上好", "Rain"));
        Assert.True(BotNicknameMatcher.Matches("C++ 在吗", "C++,Bot"));
        Assert.False(BotNicknameMatcher.Matches("C++ABC 在吗", "C++,Bot"));
    }

    // ---------- 入口：QQ 官方全量群消息 ----------

    [Fact]
    public async Task QQ官方全量群消息_昵称命中视为艾特()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE",
            Parse(FullPayload("msg_nick_hit", "小雨，帮我查一下……")));

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.True(message.IsAtRobot);
    }

    [Fact]
    public async Task QQ官方全量群消息_昵称是别的词一部分时不视为艾特()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE",
            Parse(FullPayload("msg_nick_miss", "下雨了，雨伞收好")));

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.False(message.IsAtRobot);
    }

    // ---------- 入口：OneBot11 群消息 ----------

    [Fact]
    public async Task OneBot群消息_昵称命中即视为艾特_无需at段()
    {
        (OneBotManager manager, MessageQueue queue, BotInstanceStore store) = await BuildOneBotAsync();
        await store.UpsertAsync(new BotInstance
        {
            Id = "ob-nick",
            Name = "昵称测试",
            Platform = BotPlatform.OneBot11,
            Enabled = true,
            Nicknames = Nicknames,
            OneBot = new OneBotConfig
            {
                SelfQq = "10001",
                Http = new OneBotHttpConfig { Enabled = true, ApiUrl = "http://127.0.0.1:1" }
            }
        });

        // 消息里没有 at 段，仅文本含昵称
        await manager.HandleEventAsync("ob-nick", GroupEvent("雨 早上好！"));

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.True(message.IsAtRobot);
        Assert.Equal("雨 早上好！", message.Content);
    }

    [Fact]
    public async Task OneBot群消息_未命中昵称仍按普通消息处理()
    {
        (OneBotManager manager, MessageQueue queue, BotInstanceStore store) = await BuildOneBotAsync();
        await store.UpsertAsync(new BotInstance
        {
            Id = "ob-nick",
            Name = "昵称测试",
            Platform = BotPlatform.OneBot11,
            Enabled = true,
            Nicknames = Nicknames,
            OneBot = new OneBotConfig
            {
                SelfQq = "10001",
                Http = new OneBotHttpConfig { Enabled = true, ApiUrl = "http://127.0.0.1:1" }
            }
        });

        await manager.HandleEventAsync("ob-nick", GroupEvent("今天天气怎么样"));

        Assert.False(Assert.Single(queue.DrainForTest()).IsAtRobot);
    }

    // ---------- 辅助 ----------

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string FullPayload(string id, string content) =>
        $$"""{"id":"{{id}}","author":{"member_openid":"member_nick","username":"张三"},"content":"{{content}}","group_openid":"group_nick","timestamp":"2026-01-01T00:00:00+00:00","msg_seq":1}""";

    private static string GroupEvent(string text) =>
        "{\"time\":1780000000,\"self_id\":10001,\"post_type\":\"message\",\"message_type\":\"group\","
        + "\"message_id\":9901,\"group_id\":555111,\"user_id\":20002,"
        + "\"message\":[{\"type\":\"text\",\"data\":{\"text\":\"" + text + "\"}}],"
        + "\"raw_message\":\"" + text + "\"}";

    private static async Task<(MessageDispatcher, MessageQueue)> BuildDispatcherAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        BotInstanceStore store = sp.GetRequiredService<BotInstanceStore>();
        BotInstance instance = store.Get(Database.LegacyBotId)!;
        instance.Nicknames = Nicknames;
        Assert.Null(await store.UpsertAsync(instance));

        ILoggerFactory logs = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, logs.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<BotIdentityResolver>(),
            store,
            logs.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }

    private static async Task<(OneBotManager, MessageQueue, BotInstanceStore)> BuildOneBotAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory logs = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, logs.CreateLogger<MessageQueue>());
        BotInstanceStore store = sp.GetRequiredService<BotInstanceStore>();
        OneBotManager manager = new(
            store,
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            logs.CreateLogger<OneBotManager>());
        return (manager, queue, store);
    }
}
