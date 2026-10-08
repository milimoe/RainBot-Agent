using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Commands;
using RainBot.Services.Config;
using RainBot.Services.Llm;
using RainBot.Services.Tools;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 调试模式思维链记录：ReAct 循环把最后一次 reasoning_content 记入 ReasoningRecorder（仅 DebugMode 开启时），
/// /admin reasoning 供管理员查看，用于定位推理型模型思考耗尽输出预算导致的空回复。
/// </summary>
public class ReasoningRecorderTests
{
    private const string Group = "group_reasoning";

    /// <summary>模拟推理型模型思考耗尽输出预算：content 为空、finish_reason=length、output=512</summary>
    private static HttpResponseMessage ReasoningResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"role":"assistant","content":"","reasoning_content":"用户让我解析文档，但上下文里没有文档内容，我需要先确认……"},"finish_reason":"length"}],"usage":{"prompt_tokens":100,"completion_tokens":512,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10}}""",
                Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task 调试模式_记录最后一次思维链()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => ReasoningResponse());
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("DebugMode", "true");
        var loop = sp.GetRequiredService<ReActLoop>();
        var recorder = sp.GetRequiredService<ReasoningRecorder>();

        ReActResult result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("帮我解析这篇文档")],
            new ToolExecutionContext { GroupOpenId = Group, IsAdmin = false, AllowProfileUpdate = false });

        Assert.Equal("", result.Text); // 思考耗尽输出预算 → 无文本，上层静默跳过
        ReasoningSnapshot? snapshot = recorder.GetLast(Group);
        Assert.NotNull(snapshot);
        Assert.Contains("解析文档", snapshot!.Text);
        Assert.Equal("length", snapshot.FinishReason);
        Assert.Equal(512, snapshot.CompletionTokens);
    }

    [Fact]
    public async Task 非调试模式_不记录()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => ReasoningResponse());
        var loop = sp.GetRequiredService<ReActLoop>();
        var recorder = sp.GetRequiredService<ReasoningRecorder>();

        await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = Group, IsAdmin = false, AllowProfileUpdate = false });

        Assert.Null(recorder.GetLast(Group));
    }

    [Fact]
    public async Task admin_reasoning_指令_权限与查看()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => ReasoningResponse());
        var parser = sp.GetRequiredService<CommandParser>();
        var config = sp.GetRequiredService<RuntimeConfig>();
        var recorder = sp.GetRequiredService<ReasoningRecorder>();

        // 解析（含中文别名）
        ParsedCommand? cmd = parser.Parse("/admin reasoning");
        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.AdminReasoning, cmd!.Kind);
        Assert.NotNull(parser.Parse("/admin 思考"));

        // 非管理员被拒绝
        string? denied = await parser.ExecuteAsync(cmd!, Group, "user_x", isAdmin: false);
        Assert.Contains("管理员", denied);

        // 调试模式未开启 → 提示开启
        string? hint = await parser.ExecuteAsync(cmd!, Group, "user_admin_0123", isAdmin: true);
        Assert.Contains("调试模式", hint);

        // 开启调试模式但没有记录 → 提示等待触发
        await config.SetAsync("DebugMode", "true");
        string? empty = await parser.ExecuteAsync(cmd!, Group, "user_admin_0123", isAdmin: true);
        Assert.Contains("还没有记录", empty);

        // 有记录后 → 查看内容与元信息
        recorder.Record(new ReasoningSnapshot
        {
            TimeUtc = DateTimeOffset.UtcNow,
            GroupOpenId = Group,
            FinishReason = "length",
            CompletionTokens = 512,
            Text = "用户让我解析文档，但上下文里没有文档内容……"
        });
        string? view = await parser.ExecuteAsync(cmd!, Group, "user_admin_0123", isAdmin: true);
        Assert.NotNull(view);
        Assert.Contains("最后一次思维链", view);
        Assert.Contains("解析文档", view);
        Assert.Contains("finish=length", view);
    }
}
