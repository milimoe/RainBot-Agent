using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// QQ 官方 C2C 私聊测试：
/// 入站（C2C_MESSAGE_CREATE 解析 → 会话键 {实例Id}:p{用户OpenID}、私聊标记、去重）；
/// 出站（QqOfficialSender 私聊走 /v2/users/{openid}/messages，图片自动降级纯文本）。
/// </summary>
public class C2CPrivateMessageTests
{
    private const string BotId = "qq";

    private static string C2CPayload(string id = "c2c_001", string user = "user_abc") =>
        $$"""{"id":"{{id}}","author":{"user_openid":"{{user}}","username":"李四"},"content":"你好呀","timestamp":"2026-01-01T00:00:00+00:00"}""";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---------- 入站 ----------

    [Fact]
    public async Task C2C事件_入队且标记私聊与会话键()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await DispatcherTestHost.BuildAsync();

        await dispatcher.HandleDispatchAsync(BotId, "C2C_MESSAGE_CREATE", Parse(C2CPayload()));

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.True(message.IsPrivate);
        Assert.True(message.IsAtRobot); // 私聊一律视为对机器人发言
        Assert.False(message.IsFullMessage); // 官方 C2C 支持被动回复，可引用 msg_id
        Assert.Equal("qq:puser_abc", message.GroupOpenId);
        Assert.Equal(BotId, message.BotId);
        Assert.Equal("user_abc", message.SenderOpenId);
        Assert.Equal("李四", message.Username);
        Assert.Equal("c2c_001", message.MsgId); // 保留原始 msg_id 供被动回复引用
        Assert.Equal("你好呀", message.Content);
    }

    [Fact]
    public async Task C2C重复推送_忽略()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await DispatcherTestHost.BuildAsync();

        await dispatcher.HandleDispatchAsync(BotId, "C2C_MESSAGE_CREATE", Parse(C2CPayload()));
        await dispatcher.HandleDispatchAsync(BotId, "C2C_MESSAGE_CREATE", Parse(C2CPayload()));

        Assert.Single(queue.DrainForTest());
    }

    [Fact]
    public async Task C2C与群事件_msg_id不冲突()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await DispatcherTestHost.BuildAsync();

        // 同 msg_id 分别从 C2C 与群事件到达（不同实例间序列可能重叠，去重键按事件类型隔离）
        await dispatcher.HandleDispatchAsync(BotId, "C2C_MESSAGE_CREATE", Parse(C2CPayload("dup_id")));
        await dispatcher.HandleDispatchAsync(BotId, "GROUP_AT_MESSAGE_CREATE",
            Parse($$"""{"id":"dup_id","author":{"member_openid":"m1","username":"张三"},"content":"嗨","group_openid":"g1","timestamp":"2026-01-01T00:00:00+00:00"}"""));

        List<IncomingMessage> messages = queue.DrainForTest();
        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].IsPrivate);
        Assert.False(messages[1].IsPrivate);
    }

    [Fact]
    public async Task C2C缺user_openid_忽略()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await DispatcherTestHost.BuildAsync();

        await dispatcher.HandleDispatchAsync(BotId, "C2C_MESSAGE_CREATE",
            Parse("""{"id":"c2c_bad","author":{"username":"无名"},"content":"?","timestamp":"2026-01-01T00:00:00+00:00"}"""));

        Assert.Empty(queue.DrainForTest());
    }

    // ---------- 出站 ----------

    [Fact]
    public async Task 私聊发送_走C2C接口并引用被动msg_id()
    {
        RecordingHandler handler = new();
        QqOfficialSender sender = await BuildSenderAsync(handler);

        SendResult sendResult = await sender.SendAsync(new BotSendRequest
        {
            BotId = BotId,
            RawGroupId = "user_abc",
            Content = "你好，我是雨",
            ReplyMsgId = "c2c_001",
            MsgSeq = 3,
            IsPrivate = true
        });

        Assert.True(sendResult.Success);
        (string url, string body) = Assert.Single(handler.Requests, r => !r.Url.Contains("getAppAccessToken"));
        Assert.StartsWith("https://api.bot.qq.com/v2/users/user_abc/messages", url);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("c2c_001", doc.RootElement.GetProperty("msg_id").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("msg_seq").GetInt32());
        Assert.Equal("\r\n你好，我是雨", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task 私聊Markdown_消息体正确()
    {
        RecordingHandler handler = new();
        QqOfficialSender sender = await BuildSenderAsync(handler);

        SendResult sendResult = await sender.SendAsync(new BotSendRequest
        {
            BotId = BotId,
            RawGroupId = "user_abc",
            Content = "**你好**",
            Markdown = true,
            IsPrivate = true
        });

        Assert.True(sendResult.Success);
        (string url, string body) = Assert.Single(handler.Requests, r => !r.Url.Contains("getAppAccessToken"));
        Assert.StartsWith("https://api.bot.qq.com/v2/users/user_abc/messages", url);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("**你好**", doc.RootElement.GetProperty("markdown").GetProperty("content").GetString());
    }

    [Fact]
    public async Task 私聊图片_降级纯文本不发富媒体()
    {
        RecordingHandler handler = new();
        QqOfficialSender sender = await BuildSenderAsync(handler);

        // 官方 C2C 不支持发送图片：应直接发文本，不出现 /files 上传请求
        SendResult sendResult = await sender.SendAsync(new BotSendRequest
        {
            BotId = BotId,
            RawGroupId = "user_abc",
            Content = "看图",
            ImageUrl = "http://example.com/osm/meme.jpg",
            IsPrivate = true
        });

        Assert.True(sendResult.Success);
        Assert.All(handler.Requests, r => Assert.DoesNotContain("/files", r.Url));
        (string url, string body) = Assert.Single(handler.Requests, r => !r.Url.Contains("getAppAccessToken"));
        Assert.StartsWith("https://api.bot.qq.com/v2/users/user_abc/messages", url);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("\r\n看图", doc.RootElement.GetProperty("content").GetString());
    }

    // ---------- 辅助 ----------

    private static async Task<(MessageDispatcher, MessageQueue)> BuildDispatcherAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, loggerFactory.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<BotIdentityResolver>(),
            sp.GetRequiredService<BotInstanceStore>(),
            loggerFactory.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }

    private static async Task<QqOfficialSender> BuildSenderAsync(RecordingHandler handler)
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        QQBotService qqBotService = new(
            sp.GetRequiredService<BotConfigService>(),
            NullLogger<QQBotService>.Instance,
            new SingleClientFactory(new HttpClient(handler)),
            new MemoryCache(new MemoryCacheOptions()));
        return new QqOfficialSender(
            qqBotService,
            sp.GetRequiredService<BotInstanceStore>(),
            NullLogger<QqOfficialSender>.Instance);
    }

    /// <summary>记录所有请求的 HttpMessageHandler（Token 接口返回固定值）</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.ToString() ?? "", body));
            if (request.RequestUri?.ToString().Contains("getAppAccessToken") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"test-token","expires_in":"7200"}""")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

/// <summary>分发器测试共用宿主（与 DispatcherDedupeTests 相同的构建方式）</summary>
internal static class DispatcherTestHost
{
    internal static async Task<(MessageDispatcher Dispatcher, MessageQueue Queue)> BuildAsync()
    {
        Microsoft.Extensions.DependencyInjection.ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, loggerFactory.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<BotIdentityResolver>(),
            sp.GetRequiredService<BotInstanceStore>(),
            loggerFactory.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }
}
