using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.WebUi;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 调试相关行为：
/// - 空内容哨兵 (empty)：模型按约定「接不上就输出 (empty)」→ 不发送任何消息；
/// - 思维显示（DebugMode + DebugShowReasoning）：思维链用 ``` 包起来与回复一起发出，需调试模式配合；
/// - 日志页把框架 HTTP 管线（System.Net.Http.HttpClient.*）的 Info 降为 Debug 展示。
/// </summary>
public class DebugFeaturesTests
{
    private const string Group = "group_debug_features";

    /// <summary>推理型模型响应：思维链 + 正文</summary>
    private static HttpResponseMessage LlmWithReasoning()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"role":"assistant","content":"今天有雨，记得带伞 ☔","reasoning_content":"用户在问天气，我先回忆最近的天气话题再答。"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":50,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10}}""",
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage LlmText(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(TestHelpers.LlmTextResponse(content), Encoding.UTF8, "application/json") };

    /// <summary>构建带发送捕获的宿主：deepseek 请求走 LLM 应答，其余（腾讯发送）捕获请求体</summary>
    private static async Task<(ServiceProvider Sp, List<string> Sent)> BuildWithCaptureAsync(Func<HttpRequestMessage, HttpResponseMessage> llmResponder)
    {
        List<string> sent = [];
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.Host.Contains("deepseek", StringComparison.Ordinal))
            {
                return llmResponder(req);
            }
            if (req.RequestUri.AbsolutePath.Contains("getAppAccessToken", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"fake-token\",\"expires_in\":\"7200\"}", Encoding.UTF8, "application/json")
                };
            }
            sent.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        await sp.GetRequiredService<SendQueue>().StartAsync(CancellationToken.None);
        return (sp, sent);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    private static TriggerContext PassiveCtx() => new()
    {
        BotId = Database.LegacyBotId,
        GroupOpenId = Group,
        Type = TriggerType.Passive,
        Reason = "被群友 @ 互动",
        SenderOpenId = "user_debug_0123"
    };

    /// <summary>从发送请求体里取出 content 字段（JSON 会转义中文与反引号，需解码后再断言）</summary>
    private static string ContentOf(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("content").GetString() ?? "";
    }

    // ---------- 空内容哨兵 ----------

    [Fact]
    public async Task 随机插嘴_模型输出空哨兵_不发送空消息()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => LlmText("(empty)"));
        var runner = sp.GetRequiredService<WorkflowRunner>();

        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.RandomChat,
            Reason = "随机搭话"
        }, null);

        Assert.False(sent);
    }

    // ---------- 思维显示 ----------

    [Fact]
    public async Task 思维显示_开启后_思维链与回复一起发送()
    {
        (ServiceProvider sp, List<string> sent) = await BuildWithCaptureAsync(_ => LlmWithReasoning());
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("DebugMode", "true");
        await config.SetAsync("DebugShowReasoning", "true");

        bool ok = await sp.GetRequiredService<WorkflowRunner>().RunAsync(PassiveCtx(), "msg_id_1");

        Assert.True(ok);
        Assert.True(await WaitForAsync(() => sent.Count > 0), "未捕获到发送请求");
        string content = ContentOf(sent[0]);
        Assert.Contains("```", content);                    // 思维链用代码块包裹
        Assert.Contains("用户在问天气", content);             // 思维内容
        Assert.Contains("记得带伞", content);                 // 回复内容一并发出
    }

    [Fact]
    public async Task 思维显示_未开启_只发回复不含思维()
    {
        (ServiceProvider sp, List<string> sent) = await BuildWithCaptureAsync(_ => LlmWithReasoning());
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("DebugMode", "true"); // 只开调试模式，不开思维显示

        bool ok = await sp.GetRequiredService<WorkflowRunner>().RunAsync(PassiveCtx(), "msg_id_2");

        Assert.True(ok);
        Assert.True(await WaitForAsync(() => sent.Count > 0), "未捕获到发送请求");
        string content = ContentOf(sent[0]);
        Assert.DoesNotContain("用户在问天气", content);
        Assert.DoesNotContain("```", content);
        Assert.Contains("记得带伞", content);
    }

    [Fact]
    public async Task 思维显示_调试模式未开_不生效()
    {
        (ServiceProvider sp, List<string> sent) = await BuildWithCaptureAsync(_ => LlmWithReasoning());
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("DebugShowReasoning", "true"); // 单开思维显示，DebugMode 关闭 → 不生效

        bool ok = await sp.GetRequiredService<WorkflowRunner>().RunAsync(PassiveCtx(), "msg_id_3");

        Assert.True(ok);
        Assert.True(await WaitForAsync(() => sent.Count > 0), "未捕获到发送请求");
        Assert.DoesNotContain("用户在问天气", ContentOf(sent[0]));
    }

    // ---------- 日志降级 ----------

    [Fact]
    public void 日志页_HTTP管线Info降级为Debug_其余保持Info()
    {
        var provider = new WebUiLogProvider();
        ILogger httpLogger = provider.CreateLogger("System.Net.Http.HttpClient.deepseek.LogicalHandler");
        ILogger sendLogger = provider.CreateLogger("System.Net.Http.HttpClient.Default.ClientHandler");
        ILogger appLogger = provider.CreateLogger("RainBot.Services.Workflow.WorkflowRunner");

        httpLogger.LogInformation("Start processing HTTP request POST https://api.deepseek.com/chat/completions");
        sendLogger.LogInformation("Received HTTP response headers after 455.0283ms - 200");
        appLogger.LogInformation("群 g 触发「Passive」已回复：今天有雨");

        IReadOnlyList<WebUiLogEntry> entries = provider.GetEntries(0, 10);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Debug", entries[0].Level);   // HTTP 管线降级
        Assert.Equal("Debug", entries[1].Level);
        Assert.Equal("Info", entries[2].Level);    // 业务日志保持 Info
        Assert.Equal("RainBot.Services.Workflow.WorkflowRunner", entries[2].Category);
    }
}
