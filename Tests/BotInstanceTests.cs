using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using RainBot.Services.Bots;
using RainBot.Services.OneBot;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 多机器人键规则、OneBot 私聊事件解析与发送统计的单元测试（不依赖真实连接）。
/// </summary>
public class BotInstanceTests
{
    // ---------- 键规则 ----------

    [Fact]
    public void 群键与私聊键_格式正确且可区分()
    {
        Assert.Equal("ob-1:555111", BotKeys.Group("ob-1", "555111"));
        Assert.Equal("ob-1:p20002", BotKeys.Private("ob-1", "20002"));

        Assert.Equal("ob-1", BotKeys.GetBotId("ob-1:555111"));
        Assert.Equal("555111", BotKeys.GetRawPeerId("ob-1:555111"));

        Assert.True(BotKeys.IsPrivateKey("ob-1:p20002"));
        Assert.False(BotKeys.IsPrivateKey("ob-1:555111"));
        Assert.Equal("20002", BotKeys.GetRawPeerId("ob-1:p20002"));
    }

    [Fact]
    public void 老数据无前缀_拆分失败但不抛异常()
    {
        Assert.False(BotKeys.TrySplit("group_dual", out _, out string raw));
        Assert.Equal("group_dual", raw);
        Assert.Equal("", BotKeys.GetBotId("group_dual"));
        Assert.False(BotKeys.IsPrivateKey("group_dual"));
    }

    // ---------- OneBot 消息段解析 ----------

    [Fact]
    public void 解析_at段判定与文本提取()
    {
        string json = """
        {"message":[{"type":"at","data":{"qq":"10001"}},{"type":"text","data":{"text":" 在吗"}},{"type":"image","data":{"file":"http://x/a.jpg"}}]}
        """;
        JsonElement message = JsonDocument.Parse(json).RootElement.GetProperty("message");

        Assert.True(OneBotMessage.IsAt(message, "10001"));
        Assert.False(OneBotMessage.IsAt(message, "99999"));
        Assert.Equal("在吗", OneBotMessage.ExtractText(message));
    }

    [Fact]
    public void 解析_at全体成员_视为艾特()
    {
        string json = """{"message":[{"type":"at","data":{"qq":"all"}},{"type":"text","data":{"text":"开会"}}]}""";
        JsonElement message = JsonDocument.Parse(json).RootElement.GetProperty("message");
        Assert.True(OneBotMessage.IsAt(message, "10001"));
    }

    [Fact]
    public void 字符串形态消息_按纯文本处理()
    {
        JsonElement message = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.Equal("hello", OneBotMessage.ExtractText(message));
        Assert.False(OneBotMessage.IsAt(message, "10001"));
    }

    [Fact]
    public void 构造消息体_艾特在前文本在后_可选引用回复()
    {
        List<object> plain = OneBotMessage.Build("你好");
        Assert.Single(plain);

        List<object> withAt = OneBotMessage.Build("你好", "20002");
        Assert.Equal(2, withAt.Count);

        List<object> withReply = OneBotMessage.Build("你好", "20002", "7788");
        Assert.Equal(3, withReply.Count);
    }

    // ---------- 私聊入站 ----------

    [Fact]
    public async Task 私聊事件_进管道且标记为私聊与已艾特()
    {
        string payload = """
        {"time":1780000000,"self_id":10001,"post_type":"message","message_type":"private",
         "message_id":7788,"user_id":20002,"message":[{"type":"text","data":{"text":"你好"}}],
         "raw_message":"你好","sender":{"user_id":20002,"nickname":"张三"}}
        """;
        IncomingMessage message = await ParseAsync(payload, "ob-1");

        Assert.True(message.IsPrivate);
        Assert.True(message.IsAtRobot); // 私聊一律视为对机器人发言
        Assert.Equal("ob-1:p20002", message.GroupOpenId);
        Assert.Equal("ob-1", message.BotId);
        Assert.Equal("20002", message.SenderOpenId);
        Assert.Equal("你好", message.Content);
    }

    [Fact]
    public async Task 群聊事件_保持群键且艾特由at段决定()
    {
        string payload = """
        {"time":1780000000,"self_id":10001,"post_type":"message","message_type":"group",
         "message_id":7788,"group_id":555111,"user_id":20002,
         "message":[{"type":"at","data":{"qq":"10001"}},{"type":"text","data":{"text":" 在吗"}}],
         "raw_message":"[at] 在吗","sender":{"user_id":20002,"nickname":"张三","card":"群名片"}}
        """;
        IncomingMessage message = await ParseAsync(payload, "ob-1");

        Assert.False(message.IsPrivate);
        Assert.True(message.IsAtRobot);
        Assert.Equal("ob-1:555111", message.GroupOpenId);
        Assert.Equal("群名片", message.Username);
    }

    [Fact]
    public async Task 群聊未艾特_IsAtRobot为假()
    {
        string payload = """
        {"time":1780000000,"self_id":10001,"post_type":"message","message_type":"group",
         "message_id":7789,"group_id":555111,"user_id":20002,
         "message":[{"type":"text","data":{"text":"大家在聊啥"}}],"raw_message":"大家在聊啥"}
        """;
        IncomingMessage message = await ParseAsync(payload, "ob-1");
        Assert.False(message.IsAtRobot);
    }

    [Fact]
    public async Task 机器人自己的消息被忽略()
    {
        string payload = """
        {"time":1780000000,"self_id":10001,"post_type":"message","message_type":"group",
         "message_id":7790,"group_id":555111,"user_id":10001,
         "message":[{"type":"text","data":{"text":"我发的"}}],"raw_message":"我发的"}
        """;
        IncomingMessage? message = await TryParseAsync(payload, "ob-1");
        Assert.Null(message);
    }

    [Fact]
    public async Task 心跳等非消息事件被忽略()
    {
        string payload = """{"time":1780000000,"self_id":10001,"post_type":"meta_event","meta_event_type":"heartbeat"}""";
        Assert.Null(await TryParseAsync(payload, "ob-1"));
    }

    // ---------- 发送统计 ----------

    [Fact]
    public async Task 发送统计_按实例累计成功与失败()
    {
        BotSendStats stats = new(TestDb.Instance, NullLogger<BotSendStats>.Instance);
        await stats.RecordAsync("ob-1", true);
        await stats.RecordAsync("ob-1", true);
        await stats.RecordAsync("ob-1", false, "retcode=1200");
        await stats.RecordAsync("ob-2", false, "无通道");

        BotSendStatSnapshot s1 = stats.Get("ob-1");
        Assert.Equal(2, s1.Sent);
        Assert.Equal(1, s1.Failed);
        Assert.Equal("retcode=1200", s1.LastError);

        BotSendStatSnapshot s2 = stats.Get("ob-2");
        Assert.Equal(0, s2.Sent);
        Assert.Equal(1, s2.Failed);

        // 未知实例返回 0（不抛异常）
        Assert.Equal(0, stats.Get("nope").Sent);

        // 快照按实例 Id 排序
        IReadOnlyList<BotSendStatSnapshot> all = stats.Snapshot();
        Assert.Equal("ob-1", all[0].BotId);
        Assert.Equal("ob-2", all[1].BotId);
    }

    // ---------- 辅助：把事件 JSON 送进 OneBotManager 并取出入站消息 ----------

    private static async Task<IncomingMessage> ParseAsync(string payload, string botId)
        => (await TryParseAsync(payload, botId))!;

    private static async Task<IncomingMessage?> TryParseAsync(string payload, string botId)
    {
        (OneBotManager manager, MessageQueue queue, BotInstanceStore store) = await BuildManagerAsync();
        await store.UpsertAsync(new BotInstance
        {
            Id = botId,
            Name = "测试",
            Platform = BotPlatform.OneBot11,
            Enabled = true,
            OneBot = new OneBotConfig
            {
                SelfQq = "10001",
                // 启用实例至少要开一条通道（与生产校验一致），这里给个不可达地址，测试不发真实请求
                Http = new OneBotHttpConfig { Enabled = true, ApiUrl = "http://127.0.0.1:1" }
            }
        });
        await manager.HandleEventAsync(botId, payload);
        return queue.DrainForTest().FirstOrDefault();
    }

    private static async Task<(OneBotManager, MessageQueue, BotInstanceStore)> BuildManagerAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var queue = new MessageQueue(sp, loggerFactory.CreateLogger<MessageQueue>());
        BotInstanceStore store = sp.GetRequiredService<BotInstanceStore>();
        OneBotManager manager = new(
            store,
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            loggerFactory.CreateLogger<OneBotManager>());
        return (manager, queue, store);
    }

}

/// <summary>测试用真实临时数据库（BotSendStats 会落库，失败只记 debug 日志）</summary>
internal static class TestDb
{
    private static RainBot.Services.Storage.Database? _instance;

    internal static RainBot.Services.Storage.Database Instance
    {
        get
        {
            if (_instance != null) return _instance;
            string path = Path.Combine(Path.GetTempPath(), $"rainbot-stats-{Guid.NewGuid():N}.db");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Rain:Storage:SqlitePath"] = path })
                .Build();
            var db = new Database(configuration, NullLogger<Database>.Instance);
            db.InitializeAsync().GetAwaiter().GetResult();
            _instance = db;
            return db;
        }
    }
}
