using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 消息分发去重测试：开启「接收所有消息」后，@ 消息会同时推送
/// GROUP_AT_MESSAGE_CREATE 与 GROUP_MESSAGE_CREATE（同 msg_id）。
/// 无论哪条先到，@ 语义（指令/触发）都不得丢失，且不重复统计/入库。
/// </summary>
public class DispatcherDedupeTests
{
    private const string MsgId = "msg_dual_001";
    private const string Group = "group_dual";
    private const string Sender = "member_abcdef";

    private static string FullPayload(string id = MsgId) =>
        $$"""{"id":"{{id}}","author":{"member_openid":"{{Sender}}","username":"张三"},"content":"/admin list","group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00","msg_seq":1}""";

    private static string AtPayload(string id = MsgId) =>
        $$"""{"id":"{{id}}","author":{"member_openid":"{{Sender}}","username":"张三"},"content":"/admin list","group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00"}""";

    private static async Task<(MessageDispatcher Dispatcher, MessageQueue Queue)> BuildAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, loggerFactory.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<Services.Config.RuntimeConfig>(),
            sp.GetRequiredService<BotIdentityResolver>(),
            loggerFactory.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task 全量先到_AT后到_AT补执行且跳过副作用()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildAsync();

        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload()));
        await dispatcher.HandleDispatchAsync("GROUP_AT_MESSAGE_CREATE", Parse(AtPayload()));

        List<IncomingMessage> messages = queue.DrainForTest();
        Assert.Equal(2, messages.Count);

        IncomingMessage full = messages[0];
        Assert.False(full.IsAtRobot);
        Assert.False(full.SkipSideEffects);
        Assert.Equal(MsgId, full.MsgId);

        IncomingMessage at = messages[1];
        Assert.True(at.IsAtRobot);
        Assert.True(at.SkipSideEffects); // 副作用已由全量事件完成，只补 @ 语义
        Assert.Equal(Sender, at.SenderOpenId);
    }

    [Fact]
    public async Task AT先到_全量后到_全量跳过()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildAsync();

        await dispatcher.HandleDispatchAsync("GROUP_AT_MESSAGE_CREATE", Parse(AtPayload()));
        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload()));

        List<IncomingMessage> messages = queue.DrainForTest();
        IncomingMessage at = Assert.Single(messages);
        Assert.True(at.IsAtRobot);
        Assert.False(at.SkipSideEffects);
    }

    [Fact]
    public async Task 同类型事件重复推送_忽略()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildAsync();

        await dispatcher.HandleDispatchAsync("GROUP_AT_MESSAGE_CREATE", Parse(AtPayload()));
        await dispatcher.HandleDispatchAsync("GROUP_AT_MESSAGE_CREATE", Parse(AtPayload()));
        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload()));
        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload()));

        List<IncomingMessage> messages = queue.DrainForTest();
        IncomingMessage at = Assert.Single(messages);
        Assert.True(at.IsAtRobot);
    }

    [Fact]
    public async Task 不同msgid互不影响()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildAsync();

        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload("msg_a")));
        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(FullPayload("msg_b")));
        await dispatcher.HandleDispatchAsync("GROUP_AT_MESSAGE_CREATE", Parse(AtPayload("msg_c")));

        List<IncomingMessage> messages = queue.DrainForTest();
        Assert.Equal(3, messages.Count);
        Assert.False(messages[0].IsAtRobot);
        Assert.False(messages[1].IsAtRobot);
        Assert.True(messages[2].IsAtRobot);
        Assert.False(messages[2].SkipSideEffects);
    }

    [Fact]
    public async Task 全量事件带mentions_is_you_判定为艾特()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildAsync();

        // 真实新版 payload：@ 机器人时 mentions 携带 is_you=true（无 @ 事件推送）
        string payload = $$"""
            {"id":"msg_mentions","author":{"member_openid":"{{Sender}}","username":"张三"},
             "content":"<@044E0000000000000000000000000000> /status",
             "group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00","msg_seq":2,
             "mentions":[{"bot":true,"is_you":true,"id":"044E0000000000000000000000000000",
                          "member_openid":"044E0000000000000000000000000000","member_role":"member","username":"雨"}]}
            """;
        await dispatcher.HandleDispatchAsync("GROUP_MESSAGE_CREATE", Parse(payload));

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.True(message.IsAtRobot);
        Assert.False(message.SkipSideEffects);
    }
}
