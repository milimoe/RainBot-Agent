using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.QQ;
using RainBot.Services.Trigger;
using RainBot.Services.WebUi;
using RainBot.Services.Storage;
using RainBot.Services.Profile;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RainBot.Tests;

public class ConversationContextTests
{
    [Fact]
    public async Task 单条提问正文明确放入当前块_旧视频仅作背景()
    {
        string? current = null;
        await using var sp = await TestHost.BuildReadyAsync(req =>
        {
            using var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            current = body.RootElement.GetProperty("messages")[3].GetProperty("content").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent(TestHelpers.LlmTextResponse("这是地区设定页"), Encoding.UTF8, "application/json") };
        });
        var config = sp.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(config.Fun, false);
        config.Trigger.PassiveCooldownSeconds = 0;
        await sp.GetRequiredService<HistoryStore>().AppendAsync("focus", TestHelpers.Msg("focus", "alice", "42中队预告片"));
        await sp.GetRequiredService<MessageProcessor>().ProcessAsync(
            TestHelpers.Msg("focus", "alice", "这个网站有什么内容 https://milimoe.com/regions", isAt: true, isAdmin: true), default);
        Assert.Contains("当前触发消息正文：\n这个网站有什么内容 https://milimoe.com/regions", current);
        Assert.Contains("后端确认的触发者权限：管理员", current);
        Assert.Contains("当前正文明确给出的链接或引用优先", current);
        Assert.DoesNotContain("42中队", current);
    }

    [Fact]
    public async Task 已处理指令_不进入历史_响应也标记排除()
    {
        int modelCalls = 0;
        await using var sp = await TestHost.BuildReadyAsync(_ => { modelCalls++; throw new InvalidOperationException("指令不应调用模型"); });
        await sp.GetRequiredService<MessageProcessor>().ProcessAsync(TestHelpers.Msg("commands", "alice", "/admin help", isAt: true), default);
        Assert.Equal(0, modelCalls);
        Assert.Equal(0, sp.GetRequiredService<HistoryStore>().Count("commands"));
        var tasks = sp.GetRequiredService<SendQueue>().PeekPendingForTest();
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.True(t.ExcludeFromContext));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 仿真发送成功_只记回复正文_指令响应不入模型(bool command)
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var bridge = new WebUiBridge(new WebUiEventBus(), sp.GetRequiredService<Database>(), sp.GetRequiredService<UserIdentityResolver>(), sp.GetRequiredService<ILogger<WebUiBridge>>());
        var queue = ActivatorUtilities.CreateInstance<SendQueue>(sp, bridge);
        var history = sp.GetRequiredService<HistoryStore>();
        await queue.StartAsync(default);
        try
        {
            await queue.EnqueueAsync(new SendTask
            {
                GroupOpenId = WebUiBridge.SimGroupId,
                Content = "```调试推理```\n网站是地区设定页\n8822 tokens",
                ContextContent = "网站是地区设定页",
                ExcludeFromContext = command
            });
            // 发送成功标记在历史追加之后执行。
            var state = sp.GetRequiredService<GroupStateManager>().GetOrCreate(WebUiBridge.SimGroupId);
            for (int i = 0; i < 100 && state.LastBotSpeakUtc == DateTimeOffset.MinValue; i++) await Task.Delay(20);
            Assert.NotEqual(DateTimeOffset.MinValue, state.LastBotSpeakUtc);
            var entries = history.GetRecent(WebUiBridge.SimGroupId, int.MaxValue, out _);
            if (command) Assert.Empty(entries);
            else
            {
                Assert.Equal("网站是地区设定页", Assert.Single(entries).Content);
                var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
                {
                    GroupOpenId = WebUiBridge.SimGroupId, Type = TriggerType.Passive, Reason = "test", CurrentContent = "你刚才说什么？"
                });
                Assert.Contains("你此前发送的回复: 网站是地区设定页", compose.Messages[2].Content);
                Assert.DoesNotContain("调试推理", compose.Messages[2].Content);
                Assert.DoesNotContain("8822", compose.Messages[2].Content);
            }
        }
        finally { await queue.StopAsync(default); }
    }

    [Fact]
    public async Task 平台回显不重复记录_指令响应回显不漏入历史()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        history.RegisterOutgoing("echo", "普通回复");
        history.RegisterOutgoing("echo", "参数已更新");
        foreach (string text in new[] { "普通回复", "参数已更新" })
            await history.AppendAsync("echo", new IncomingMessage
            {
                MsgId = Guid.NewGuid().ToString(), GroupOpenId = "echo", SenderOpenId = "bot", Content = "\r\n" + text, IsFromBot = true
            });
        history.AppendBotReply("echo", "普通回复");
        Assert.Equal("普通回复", Assert.Single(history.GetRecent("echo", int.MaxValue, out _)).Content);
    }

    [Fact]
    public async Task 平台发送失败_不进入机器人历史()
    {
        await using var sp = await TestHost.BuildReadyAsync();
        var queue = sp.GetRequiredService<SendQueue>();
        await queue.StartAsync(default);
        try
        {
            await queue.EnqueueAsync(new SendTask { BotId = "missing", GroupOpenId = "missing:peer", Content = "未发送的回复" });
            await Task.Delay(150);
            Assert.Equal(0, sp.GetRequiredService<HistoryStore>().Count("missing:peer"));
        }
        finally { await queue.StopAsync(default); }
    }
}
