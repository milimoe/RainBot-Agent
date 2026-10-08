using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// @ 提及渲染：官方 mentions 携带 username，但此前只用于判断「是否 @ 了机器人」，
/// 进上下文时标签被统一替换成「@用户」——模型不知道被 @ 的是谁，多人 @ 时无法区分。
/// 现在渲染成 @用户名（@ 机器人自己为「@你」），历史与上下文都用渲染后的文本。
/// </summary>
public class MentionRenderTests
{
    private const string Group = "group_mention";
    private const string Sender = "member_sender";
    private const string Alice = "F5C504D92B2B4EB947FC5398B26B3FB0";
    private const string Bob = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4";
    private const string BotId2 = "E746CB4D12DB40E960D92CDB91C4FB1D";

    private static List<Mention> Mentions() =>
    [
        new() { Id = Alice, MemberOpenId = Alice, Username = "小明", IsBot = false },
        new() { Id = Bob, MemberOpenId = Bob, Username = "小红", IsBot = false },
        new() { Id = BotId2, MemberOpenId = BotId2, Username = "雨", IsBot = true, IsYou = true }
    ];

    [Fact]
    public void 渲染_命中mentioned用户名_自己为你_未知回退占位()
    {
        string content = $" <@!{BotId2}> 帮我看看 <@{Alice}> 说的 <@!{Bob}> 那条，还有 <@UNKNOWN> 这个";

        string rendered = MentionRenderer.RenderAtTags(content, Mentions());

        Assert.StartsWith("@你 帮我看看", rendered);      // 首尾空白去掉，@ 自己 = 「@你」
        Assert.Contains("@小明 说的", rendered);
        Assert.Contains("@小红 那条", rendered);
        Assert.Contains("@用户 这个", rendered);          // 未命中 mentions：沿用原有占位，不瞎猜名字
        Assert.DoesNotContain("<@!", rendered);
    }

    [Fact]
    public void 渲染_无标签原样返回_有标签但无mentions时回退占位()
    {
        // 没有 @ 标签：原样返回（不做任何处理）
        Assert.Equal("今天天气不错", MentionRenderer.RenderAtTags("今天天气不错", Mentions()));

        // 有标签但拿不到 mentions（异常推送）：退回改造前的「@用户」占位，行为不劣化
        Assert.Equal("@用户 在吗", MentionRenderer.RenderAtTags($"<@{Alice}> 在吗", []));
        Assert.Equal("@用户 在吗", MentionRenderer.RenderAtTags($"<@{Alice}> 在吗", null));
    }

    [Fact]
    public async Task QQ官方_At事件_ContextText带用户名()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();
        string payload = $$"""
        {"id":"msg_mention_1","author":{"member_openid":"{{Sender}}","username":"我"},"content":" <@!{{BotId2}}> 看看 <@{{Alice}}> 说的",
         "group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00",
         "mentions":[{"bot":true,"id":"{{BotId2}}","is_you":true,"member_openid":"{{BotId2}}","username":"雨"},
                     {"bot":false,"id":"{{Alice}}","is_you":false,"member_openid":"{{Alice}}","username":"小明"}]}
        """;

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_AT_MESSAGE_CREATE", JsonDocument.Parse(payload).RootElement);

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.Equal("@你 看看 @小明 说的", message.ContextText);
        Assert.Equal(message.ContextText, message.DisplayContent); // 入库/进上下文用渲染后的文本
        Assert.Contains($"<@!{BotId2}>", message.Content);          // 原始标签保留（判定 @ 语义用）
    }

    [Fact]
    public async Task OneBot_at段_渲染成QQ号()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();
        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE", JsonDocument.Parse("""
        {"id":"msg_noop","author":{"member_openid":"x"},"content":"","group_openid":"group_noop","timestamp":"2026-01-01T00:00:00+00:00"}
        """).RootElement);
        queue.DrainForTest(); // 清空上面这条占位消息

        // OneBot 侧的渲染由 OneBotMessage.ExtractContextText 负责（事件里没有被 @ 者昵称，只有 QQ 号）
        JsonElement oneBotMessage = JsonDocument.Parse("""
        [{"type":"at","data":{"qq":"999"}},{"type":"text","data":{"text":" 看看 "}},{"type":"at","data":{"qq":"123456"}},{"type":"at","data":{"qq":"all"}}]
        """).RootElement;

        string contextText = Services.OneBot.OneBotMessage.ExtractContextText(oneBotMessage);
        Assert.Equal("@999 看看 @123456@全体成员", contextText);
        Assert.Equal("看看", Services.OneBot.OneBotMessage.ExtractText(oneBotMessage)); // 纯文本内容不变
    }

    /// <summary>端到端：模型收到的上下文里确实是「@用户名」而不是「@用户」</summary>
    [Fact]
    public async Task 端到端_LLM上下文含mentioned用户名()
    {
        List<string> bodies = [];
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(TestHelpers.LlmTextResponse("哦哦 🌧️"), System.Text.Encoding.UTF8, "application/json")
            };
        });
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Trigger.RandomChatProbability", "0");

        var history = sp.GetRequiredService<HistoryStore>();
        await history.AppendAsync(Group, new IncomingMessage
        {
            BotId = Database.LegacyBotId,
            MsgId = "m_mention",
            GroupOpenId = Group,
            SenderOpenId = Sender,
            Content = $" <@!{BotId2}> 看看 <@{Alice}> 说的",
            ContextText = MentionRenderer.RenderAtTags($" <@!{BotId2}> 看看 <@{Alice}> 说的", Mentions()),
            IsAtRobot = true,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        bool sent = await sp.GetRequiredService<WorkflowRunner>().RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = Sender
        }, "m_mention");

        Assert.True(sent);
        // 请求体里非 ASCII 会被 JSON 转义（\uXXXX），解码后再断言
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(bodies));
        string context = string.Join("\n", doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Select(m => m.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : ""));
        Assert.Contains("@你 看看 @小明 说的", context);   // 历史块里带用户名
        Assert.DoesNotContain("@用户", context);
    }

    private static async Task<(MessageDispatcher Dispatcher, MessageQueue Queue)> BuildDispatcherAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, loggerFactory.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<Services.QQ.BotIdentityResolver>(),
            sp.GetRequiredService<Services.Bots.BotInstanceStore>(),
            loggerFactory.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }
}
