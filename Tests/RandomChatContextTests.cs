using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using Xunit;

namespace RainBot.Tests;

public class RandomChatContextTests
{
    [Fact]
    public async Task 插嘴围绕多人讨论_保留引用与自己的回复_不突出个人画像()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        await history.AppendAsync("discussion", new IncomingMessage
        {
            MsgId = "first", MsgIdx = "idx1", GroupOpenId = "discussion", SenderOpenId = "alice",
            Username = "小甲", Content = "我们下周去露营，帐篷谁带？", ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-30)
        });
        await history.AppendAsync("discussion", TestHelpers.Msg("discussion", "bob", "我带帐篷，吃的你准备", username: "小乙", msgTime: DateTimeOffset.UtcNow.AddSeconds(-20)));
        history.AppendBotReply("discussion", "记得看天气预报");
        await history.AppendAsync("discussion", new IncomingMessage
        {
            MsgId = "last", RefMsgIdx = "idx1", GroupOpenId = "discussion", SenderOpenId = "charlie", Username = "小丙", Content = "那就这么定了"
        });
        var composed = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = "discussion", Type = TriggerType.RandomChat, Reason = "旁听", SenderOpenId = "charlie",
            CurrentContent = "那就这么定了", RecalledProfile = "旧兴趣：显卡，过往：热衷硬件"
        });
        string current = composed.Messages[3].Content!;
        Assert.Contains("近期群聊讨论", current);
        Assert.Contains("小甲", current);
        Assert.Contains("小乙", current);
        Assert.Contains("小丙", current);
        Assert.Contains("我带帐篷，吃的你准备", current);
        Assert.Contains("你此前发送的回复: 记得看天气预报", current);
        Assert.Contains("引用消息：我们下周去露营，帐篷谁带？", current);
        Assert.DoesNotContain("旧兴趣：显卡", current);
        Assert.Contains("最后一句在接谁的话", current);
        Assert.Contains("(empty)", current);
        Assert.True(current.IndexOf("我们下周去露营", StringComparison.Ordinal) < current.IndexOf("我带帐篷", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 讨论窗口隔离长间隔旧话题和触发之后的消息()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        var at = DateTimeOffset.UtcNow;
        await history.AppendAsync("window", TestHelpers.Msg("window", "alice", "昨天的游戏话题", msgTime: at.AddMinutes(-4)));
        await history.AppendAsync("window", TestHelpers.Msg("window", "bob", "现在聊露营", msgTime: at.AddSeconds(-30)));
        await history.AppendAsync("window", TestHelpers.Msg("window", "alice", "现在聊烧烤", msgTime: at.AddSeconds(-10)));
        await history.AppendAsync("window", TestHelpers.Msg("window", "charlie", "未来才到的消息", msgTime: at.AddSeconds(10)));
        Assert.Equal(new[] { "现在聊露营", "现在聊烧烤" }, history.GetDiscussion("window", at).Select(e => e.Content));
    }

    [Fact]
    public async Task 高频长消息讨论片段有独立预算_保留最新发言()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        var at = DateTimeOffset.UtcNow;
        for (int i = 0; i < 30; i++)
            await history.AppendAsync("busy", TestHelpers.Msg("busy", "user" + i % 3, $"消息{i:D2}" + new string('长', 700), msgTime: at.AddSeconds(i - 30)));
        Assert.Equal(20, history.GetDiscussion("busy", at).Count);
        var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = "busy", Type = TriggerType.RandomChat, Reason = "旁听", CreatedAt = at
        });
        string current = compose.Messages[3].Content!;
        int begin = current.IndexOf("[近期群聊讨论：", StringComparison.Ordinal);
        int end = current.IndexOf("[近期群聊讨论结束]", StringComparison.Ordinal);
        string discussion = current[begin..end];
        Assert.True(discussion.Length < 5000);
        Assert.Contains("消息29", discussion);
        Assert.DoesNotContain("消息10", discussion);
    }

    [Fact]
    public async Task 实际插嘴链路不召回触发者个人过往_被艾特仍保留()
    {
        List<string> currents = [];
        await using var sp = await TestHost.BuildReadyAsync(req =>
        {
            using var json = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            currents.Add(json.RootElement.GetProperty("messages")[3].GetProperty("content").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent(TestHelpers.LlmTextResponse("(empty)"), Encoding.UTF8, "application/json") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable"))) property.SetValue(config.Fun, false);
        config.Trigger.RandomChatProbability = 100;
        config.Trigger.PassiveCooldownSeconds = 0;
        config.Trigger.RandomChatCooldownSeconds = 0;
        await sp.GetRequiredService<Database>().UpsertUserProfileAsync(new UserProfile
        {
            GroupOpenId = "chain", UserOpenId = "alice", Summary = "过往热衷硬件评测"
        });
        await sp.GetRequiredService<HistoryStore>().AppendAsync("chain", TestHelpers.Msg("chain", "bob", "露营要带什么吃的？"));
        var processor = sp.GetRequiredService<MessageProcessor>();
        await processor.ProcessAsync(TestHelpers.Msg("chain", "alice", "我来准备烧烤"), default);
        await processor.ProcessAsync(TestHelpers.Msg("chain", "alice", "你知道我的兴趣吗", isAt: true), default);
        Assert.Equal(2, currents.Count);
        Assert.Contains("露营要带什么吃的", currents[0]);
        Assert.DoesNotContain("过往热衷硬件评测", currents[0]);
        Assert.Contains("过往热衷硬件评测", currents[1]);
        Assert.DoesNotContain("[近期群聊讨论：", currents[1]);
    }
}
