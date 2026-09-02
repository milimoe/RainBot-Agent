using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 暖群静默：群静默期触发暖群但 LLM 无内容可说时，不应发送
/// 「嗯……我暂时想不出怎么接这个话题」这类被动兜底话术（保持安静，
/// 等冷却后重试）；被动 @/私聊场景保留兜底（保证有回应）。
/// </summary>
public class WarmupSilenceTests
{
    private const string Group = "group_warmup_silence";

    private static HttpResponseMessage LlmResponse(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(TestHelpers.LlmTextResponse(content), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 暖群_LLM空输出_静默不发送()
    {
        // LLM 对暖群提示没有内容可说（返回空）→ 不发兜底话术
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => LlmResponse(""));
        var runner = sp.GetRequiredService<WorkflowRunner>();

        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Warmup,
            Reason = "群聊已静默 30 分钟"
        }, null);

        Assert.False(sent);
    }

    [Fact]
    public async Task 暖群_LLM异常_同样静默不发送()
    {
        // LLM 调用失败（500）→ 暖群不应发任何话术
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        var runner = sp.GetRequiredService<WorkflowRunner>();

        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Warmup,
            Reason = "群聊已静默 30 分钟"
        }, null);

        Assert.False(sent);
    }

    [Fact]
    public async Task 暖群_LLM正常说话_正常发送()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => LlmResponse("今天群里好安静呀，大家在忙什么呢 🌧️"));
        var runner = sp.GetRequiredService<WorkflowRunner>();

        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Warmup,
            Reason = "群聊已静默 30 分钟"
        }, null);

        Assert.True(sent);
    }

    [Fact]
    public async Task 被动AT_LLM空输出_仍发默认兜底()
    {
        // 被动 @ 场景保持原行为：无内容可说时发「想不出怎么接话题」兜底，保证用户有回应
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => LlmResponse(""));
        var runner = sp.GetRequiredService<WorkflowRunner>();

        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = "ABCDEF1234567890ABCDEF1234567890"
        }, "msg_id_1");

        Assert.True(sent);
    }
}
