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
/// 静默跳过：任何触发类型（暖群/随机插嘴/被动 @/私聊）LLM 无内容可说时，
/// 都不应发送「嗯……我暂时想不出怎么接这个话题」这类兜底话术，保持安静
/// （暖群等冷却后重试）。
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
    public async Task 被动AT_LLM空输出_同样静默不发送()
    {
        // 被动 @ 场景同样不发兜底话术：无内容可说就静默略过
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

        Assert.False(sent);
    }

    [Fact]
    public async Task 暖群静默跳过_冷却期内不空转_期满可重试且不占频控()
    {
        // 组合路径（模拟 WarmupScheduler）：静默群触发暖群 → LLM 空输出静默跳过
        // → 尝试时间已记录 → 冷却期内 Evaluate 不再通过（防每 30s 空转）
        // → 16 分钟后可再次触发，且失败不占用每小时频控额度
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => LlmResponse(""));
        var states = sp.GetRequiredService<GroupStateManager>();
        var active = sp.GetRequiredService<ActiveTrigger>();
        var runner = sp.GetRequiredService<WorkflowRunner>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GroupState state = states.GetOrCreate(Group);
        state.LastMessageUtc = now.AddMinutes(-31); // 已静默超过阈值

        Assert.NotNull(active.Evaluate(Group, now)); // 可以触发
        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Warmup,
            Reason = "群聊已静默 30 分钟"
        }, null);
        Assert.False(sent);

        // WarmupScheduler 记录尝试时间（供冷却）
        state.LastWarmupAttemptUtc = now;

        // 冷却期内（5 分钟后）不再重复触发 → 不空转
        Assert.Null(active.Evaluate(Group, now.AddMinutes(5)));

        // 冷却期满（16 分钟后）可再次尝试
        Assert.NotNull(active.Evaluate(Group, now.AddMinutes(16)));

        // 失败静默未计入每小时频控（额度留给真正成功的暖群）
        Assert.Empty(state.ActiveWindow);
    }
}
