using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Commands;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Tools;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

public class ContextCommandTests
{
    [Fact]
    public async Task 管理员查看实际快照_概览和全文_不再次调用模型()
    {
        int calls = 0;
        await using ServiceProvider provider = await TestHost.BuildReadyAsync(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestHelpers.LlmTextResponse("收到"), Encoding.UTF8, "application/json")
            };
        });
        await provider.GetRequiredService<WorkflowRunner>().RunAsync(new TriggerContext
        {
            GroupOpenId = "qq:test", BotId = "qq", Type = TriggerType.Passive, Reason = "snapshot request"
        }, null);
        Assert.Equal(1, calls);
        CommandParser parser = provider.GetRequiredService<CommandParser>();
        var summary = parser.Parse("/context")!;
        Assert.Equal(CommandKind.Context, summary.Kind);
        string? denied = await parser.ExecuteAsync(summary, "qq:test", "someone", false);
        Assert.Contains("只有管理员", denied);
        string? fullDenied = await parser.ExecuteAsync(parser.Parse("/context full")!, "qq:test", "someone", false);
        Assert.Contains("只有管理员", fullDenied);
        string? view = await parser.ExecuteAsync(summary, "qq:test", "admin", true);
        Assert.Contains("估算输入", view);
        Assert.Contains("占用", view);
        Assert.Contains("A 人设", view);
        Assert.Contains("F 当前触发", view);
        Assert.Contains("纳入", view);
        Assert.Contains("第 1/", await parser.ExecuteAsync(parser.Parse("/context full")!, "qq:test", "admin", true));
        Assert.Equal(1, calls);
        Assert.Contains("尚无上下文快照", await parser.ExecuteAsync(summary, "other:test", "admin", true));
        await provider.GetRequiredService<Services.QQ.MessageProcessor>().ProcessAsync(
            TestHelpers.Msg("qq:test", "admin", "/context", isAdmin: true), CancellationToken.None);
        var responses = provider.GetRequiredService<Services.Trigger.SendQueue>().PeekPendingForTest();
        Assert.Contains(responses, response => response.Content.Contains("F 当前触发"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 最后一轮快照包含工具结果()
    {
        int calls = 0;
        await using ServiceProvider provider = await TestHost.BuildReadyAsync(_ =>
        {
            string body = ++calls == 1
                ? """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"tool1","type":"function","function":{"name":"get_user_profile","arguments":"{\"user\":\"alice\"}"}}]}}]}"""
                : TestHelpers.LlmTextResponse("收到");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        provider.GetRequiredService<ToolRegistry>().RegisterExecutor("get_user_profile", (_, _) => Task.FromResult("profile tool result"));
        await provider.GetRequiredService<WorkflowRunner>().RunAsync(new TriggerContext
        {
            GroupOpenId = "qq:tools", BotId = "qq", Type = TriggerType.Passive, Reason = "tools test"
        }, null);
        var snapshot = provider.GetRequiredService<ContextRecorder>().GetLast("qq:tools")!;
        Assert.Equal(2, calls);
        Assert.Contains("profile tool result", snapshot.Text);
        Assert.Contains("get_user_profile", snapshot.Text);
        Assert.True(snapshot.ExtraTokens > 0);
    }

    [Fact]
    public void 全文脱敏后分页_图片不保留base64_页码校验()
    {
        var recorder = new ContextRecorder();
        string sensitive = new('a', 32);
        recorder.Record("bot:puser", [ChatMessage.UserWithParts([
            ContentPart.TextPart(new string('中', 1190) + sensitive + " end <qqbot-test>"),
            ContentPart.ImagePart("data:image/png;base64,IMAGE_SECRET")
        ])], [], null, 10000);
        ContextSnapshot snapshot = recorder.GetLast("bot:puser")!;
        Assert.Equal(1, snapshot.ImageCount);
        Assert.DoesNotContain(sensitive, snapshot.Text);
        Assert.DoesNotContain("IMAGE_SECRET", snapshot.Text);
        Assert.Contains("[ID]", snapshot.Text);
        Assert.Contains("&lt;qqbot-test>", snapshot.Text);
        Assert.Contains("第 1/2", recorder.View("bot:puser", "full", null));
        Assert.Contains("全文结束", recorder.View("bot:puser", "full", "2"));
        foreach (string invalid in new[] { "0", "-1", "3", "oops", "1 extra" })
            Assert.Contains("页码应为", recorder.View("bot:puser", "full", invalid));
        Assert.Contains("用法", recorder.View("bot:puser", "wrong", null));
    }

    [Fact]
    public void 快照不持有可变请求_会话隔离并限制保留数量()
    {
        var recorder = new ContextRecorder();
        ChatMessage message = ChatMessage.User("original snapshot");
        recorder.Record("bot1:g", [message], [], null, 1000);
        message.Content = "modified";
        Assert.Contains("original snapshot", recorder.GetLast("bot1:g")!.Text);
        Assert.Null(recorder.GetLast("bot2:g"));
        for (int i = 0; i < 64; i++) recorder.Record("new" + i, [message], [], null, 1000);
        Assert.Null(recorder.GetLast("bot1:g"));
        Assert.NotNull(recorder.GetLast("new63"));
    }
}
