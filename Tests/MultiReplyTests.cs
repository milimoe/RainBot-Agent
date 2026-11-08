using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Persona;
using RainBot.Services.QQ;
using RainBot.Services.Safety;
using RainBot.Services.Tools;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 分步答复 / 调研子代理：配置权威性、模型自决分隔符、系统硬上限、
/// 人设与系统能力段的优先级、段组发送顺序、子代理工具白名单。
/// </summary>
public class MultiReplyTests
{
    private static (OutputFilter Filter, ILogger Logger, RainConfig Config) Parts()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        return (sp.GetRequiredService<OutputFilter>(),
                sp.GetRequiredService<ILogger<WorkflowRunner>>(),
                sp.GetRequiredService<RuntimeConfig>().Config);
    }

    private static ToolExecutionContext ToolCtx() => new()
    {
        GroupOpenId = "sub-agent-test", IsAdmin = false, AllowProfileUpdate = false
    };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ToolCallResponse(string content, string toolName, string arguments = "{}") =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant", content,
                        tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = toolName, arguments } } }
                    }
                }
            },
            usage = new { prompt_tokens = 20, completion_tokens = 8, prompt_cache_hit_tokens = 15, prompt_cache_miss_tokens = 5 }
        });

    // ---------------- 配置权威性 / 人设优先级 ----------------

    [Fact]
    public void 配置关闭_剥离分隔符强制单段_并忽略工具轮中间发言()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        cfg.MultiReply.Enabled = false;
        ReActResult result = new() { Text = "第一段<seg/>第二段", Interim = ["我去查一下"] };

        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);

        Assert.Single(plan.Segments);
        Assert.Contains("第一段", plan.Segments[0]);
        Assert.Contains("第二段", plan.Segments[0]);
        Assert.DoesNotContain("我去查一下", plan.Segments[0]);
        Assert.DoesNotContain("<seg/>", plan.Segments[0]);
        Assert.False(plan.MultiSegment);
    }

    [Fact]
    public void 人设写只回一次_系统能力段仍在最末尾且声明优先()
    {
        string prompt = PersonaLoader.BuildSystemPrompt("只能回一次，绝不连续发言两次", PromptSettings.Default);

        Assert.Contains("只能回一次，绝不连续发言两次", prompt); // 人设保留，系统不删用户内容
        Assert.Contains("<seg/>", prompt);
        Assert.Contains("以本节为准", prompt);
        Assert.Contains("不需要你自行禁用", prompt);
        Assert.True(prompt.LastIndexOf("【系统能力", StringComparison.Ordinal) >
                    prompt.LastIndexOf("只能回一次", StringComparison.Ordinal),
            "系统能力段必须排在人设之后才能覆盖风格措辞");
    }

    [Fact]
    public async Task 配置热改不改变BlockA前缀()
    {
        await using ServiceProvider sp = await TestHost.BuildReadyAsync();
        BlockComposer composer = sp.GetRequiredService<BlockComposer>();
        TriggerContext ctx = new() { GroupOpenId = "cap", Type = TriggerType.Passive, Reason = "test", CurrentContent = "hi" };

        string before = (await composer.BuildAsync(ctx)).Messages[0].Content!;
        RainConfig cfg = sp.GetRequiredService<RuntimeConfig>().Config;
        cfg.MultiReply.Enabled = false;
        cfg.MultiReply.MaxSegments = 9;
        cfg.SubAgent.Enabled = false;
        string after = (await composer.BuildAsync(ctx)).Messages[0].Content!;

        Assert.Equal(before, after); // 能力段是静态常量，开关热改不破坏 DeepSeek 缓存前缀
    }

    // ---------------- 模型自决分隔符 + 硬上限 ----------------

    [Fact]
    public void 模型自决分隔符_拆成多段且只有收口段进历史()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReActResult result = new() { Text = "先说结论<seg/>再去查一下<seg/>补充完毕" };

        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);

        Assert.Equal(3, plan.Segments.Count);
        Assert.Equal(new[] { "先说结论", "再去查一下", "补充完毕" }, plan.Segments);
        Assert.Equal("补充完毕", plan.HistoryText);
        Assert.True(plan.MultiSegment);
    }

    [Theory]
    [InlineData("A<seg/>B")]
    [InlineData("A<seg>B")]
    [InlineData("A<SEG />B")]
    [InlineData("A【分段】B")]
    [InlineData("A|||B")]
    public void 分隔符变体兼容(string text)
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(
            new ReActResult { Text = text }, TriggerType.Passive, cfg.MultiReply, filter, logger);
        Assert.Equal(2, plan.Segments.Count);
    }

    [Fact]
    public void 段数硬上限_超出的段合并进最后一段()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        cfg.MultiReply.MaxSegments = 2;
        ReActResult result = new() { Text = "A<seg/>B<seg/>C<seg/>D" };

        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal("A", plan.Segments[0]);
        Assert.Contains("B", plan.Segments[1]);
    }

    [Fact]
    public void 总字数硬上限_严格不超预算()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        cfg.MultiReply.MaxTotalChars = 12;
        ReActResult result = new() { Text = "ABCDEFGHIJ<seg/>KLMNOPQRST" };

        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);

        Assert.Equal(2, plan.Segments.Count);
        Assert.True(plan.Segments.Sum(s => s.Length) <= cfg.MultiReply.MaxTotalChars);
        Assert.True(plan.DroppedChars > 0);
    }

    [Theory]
    [InlineData(TriggerType.Warmup)]
    [InlineData(TriggerType.RandomChat)]
    public void 暖群与随机插嘴强制单段(TriggerType type)
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReActResult result = new() { Text = "A<seg/>B", Interim = ["我去查一下"] };
        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, type, cfg.MultiReply, filter, logger);
        Assert.Single(plan.Segments);
        Assert.False(plan.MultiSegment);
    }

    [Fact]
    public void 工具轮中间发言作为前置分段()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReActResult result = new() { Text = "查完了", Interim = ["我去查一下"] };
        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);

        Assert.Equal(new[] { "我去查一下", "查完了" }, plan.Segments);
        Assert.Equal("查完了", plan.HistoryText);
    }

    [Fact]
    public void 最终文本为空时不发中间发言()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReActResult result = new() { Text = "", Interim = ["我去查一下"], Failed = true };
        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);
        Assert.Empty(plan.Segments);
    }

    [Fact]
    public void 命中兜底话术的分段被逐段过滤()
    {
        (OutputFilter filter, ILogger logger, RainConfig cfg) = Parts();
        ReActResult result = new() { Text = "正常回复<seg/>我接不上，让我想想" };
        ReplySegmentPlanner.Plan plan = ReplySegmentPlanner.Build(result, TriggerType.Passive, cfg.MultiReply, filter, logger);
        Assert.Single(plan.Segments);
        Assert.Equal("正常回复", plan.Segments[0]);
    }

    // ---------------- 工具轮中间发言收集 ----------------

    [Fact]
    public async Task 工具轮中间发言被收集且不额外调用模型()
    {
        int calls = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
        {
            calls++;
            return Ok(calls == 1
                ? ToolCallResponse("我去查一下", "web_search", "{\"query\":\"x\"}")
                : TestHelpers.LlmTextResponse("查完了"));
        });
        sp.GetRequiredService<ToolRegistry>().RegisterExecutor("web_search", (_, _) => Task.FromResult("结果"));

        ReActResult result = await sp.GetRequiredService<ReActLoop>().RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("查个东西")], ToolCtx());

        Assert.Equal(2, calls);
        Assert.Single(result.Interim);
        Assert.Equal("我去查一下", result.Interim[0]);
        Assert.Equal("查完了", result.Text);
    }

    // ---------------- 工作流端到端：段组任务 ----------------

    [Fact]
    public async Task 端到端_多段回复生成段组任务且历史只留收口段()
    {
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
            Ok(TestHelpers.LlmTextResponse("结论先说<seg/>再查一下<seg/>补充完毕")));
        RainConfig cfg = sp.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(cfg.Fun, false);
        cfg.Trigger.PassiveCooldownSeconds = 0;
        cfg.MultiReply.SegmentGapSeconds = 0;

        await sp.GetRequiredService<MessageProcessor>().ProcessAsync(
            TestHelpers.Msg("mr-e2e", "alice", "帮我查个东西", isAt: true, isAdmin: true), default);

        SendTask task = Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.NotNull(task.Segments);
        Assert.Equal(3, task.Segments!.Count);
        Assert.Equal("补充完毕", task.ContextContent); // Block E 只留收口段
        Assert.Equal(0, task.SegmentGapSeconds);
    }

    [Fact]
    public async Task 端到端_配置关闭只生成单段任务()
    {
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
            Ok(TestHelpers.LlmTextResponse("结论先说<seg/>再查一下")));
        RainConfig cfg = sp.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(cfg.Fun, false);
        cfg.Trigger.PassiveCooldownSeconds = 0;
        cfg.MultiReply.Enabled = false;

        await sp.GetRequiredService<MessageProcessor>().ProcessAsync(
            TestHelpers.Msg("mr-off", "alice", "帮我查个东西", isAt: true, isAdmin: true), default);

        SendTask task = Assert.Single(sp.GetRequiredService<SendQueue>().PeekPendingForTest());
        Assert.Null(task.Segments);
        Assert.Contains("结论先说", task.Content);
        Assert.Contains("再查一下", task.Content);
    }

    // ---------------- SendQueue：段组按序发送 ----------------

    [Fact]
    public async Task 段组按序发送_首段带被动msgId其余走主动消息()
    {
        List<string> bodies = [];
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.Contains("getAppAccessToken", StringComparison.Ordinal))
                return Ok("{\"access_token\":\"fake-token\",\"expires_in\":\"7200\"}");
            if (path.Contains("/v2/groups/", StringComparison.Ordinal))
                bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return Ok("{}");
        });
        SendQueue queue = sp.GetRequiredService<SendQueue>();
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(new SendTask
            {
                BotId = "qq", GroupOpenId = "qq:seg-group", Content = "兜底",
                Segments = ["第一段", "第二段", "第三段"],
                SegmentGapSeconds = 0,
                MsgId = "origin-msg"
            });

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (bodies.Count < 3 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);

            Assert.Equal(3, bodies.Count);
            string[] texts = bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("content").GetString()!).ToArray();
            Assert.Equal("\r\n第一段", texts[0]);
            Assert.Equal("\r\n第二段", texts[1]);
            Assert.Equal("\r\n第三段", texts[2]);
            Assert.True(JsonDocument.Parse(bodies[0]).RootElement.TryGetProperty("msg_id", out _));
            Assert.False(JsonDocument.Parse(bodies[1]).RootElement.TryGetProperty("msg_id", out _));
            Assert.False(JsonDocument.Parse(bodies[2]).RootElement.TryGetProperty("msg_id", out _));
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    // ---------------- 子代理：白名单与开关 ----------------

    [Fact]
    public void 子代理工具注册在builtin末尾且顺序确定()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        List<ToolDef> defs = sp.GetRequiredService<ToolRegistry>().GetToolDefs();
        int idx = defs.FindIndex(d => d.Function.Name == "delegate_research");
        Assert.True(idx >= 0);
        Assert.True(idx > defs.FindIndex(d => d.Function.Name == "admin_unmute_group"));
    }

    [Fact]
    public async Task 子代理_允许web_search并回传摘要()
    {
        int calls = 0;
        int executed = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
        {
            calls++;
            return Ok(calls == 1
                ? ToolCallResponse(null!, "web_search", "{\"query\":\"天气\"}")
                : TestHelpers.LlmTextResponse("摘要：今天有雨"));
        });
        sp.GetRequiredService<ToolRegistry>().RegisterExecutor("web_search", (_, _) =>
        {
            executed++;
            return Task.FromResult("今天有雨");
        });

        string result = await sp.GetRequiredService<SubAgentService>().RunAsync("查今天天气", ToolCtx());

        Assert.Equal("摘要：今天有雨", result);
        Assert.Equal(1, executed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 子代理_白名单外工具被拒绝执行()
    {
        int calls = 0;
        int executed = 0;
        List<string> requestBodies = [];
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            calls++;
            requestBodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return Ok(calls == 1
                ? ToolCallResponse(null!, "admin_set_setting", "{\"key\":\"DebugMode\",\"value\":\"true\"}")
                : TestHelpers.LlmTextResponse("摘要：没查到"));
        });
        sp.GetRequiredService<ToolRegistry>().RegisterExecutor("admin_set_setting", (_, _) =>
        {
            executed++;
            return Task.FromResult("已修改");
        });

        await sp.GetRequiredService<SubAgentService>().RunAsync("改个配置", ToolCtx());

        Assert.Equal(0, executed); // 有副作用的管理员工具绝不可达
        // 请求体里中文会被 JSON 转义，解析后再断言，避免假阴性
        using JsonDocument doc = JsonDocument.Parse(requestBodies[1]);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        string toolContent = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains("不在白名单", toolContent);
    }

    [Fact]
    public async Task 子代理_禁用时不调用模型()
    {
        int calls = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ => { calls++; return Ok(TestHelpers.LlmTextResponse("不该出现")); });
        sp.GetRequiredService<RuntimeConfig>().Config.SubAgent.Enabled = false;

        string result = await sp.GetRequiredService<SubAgentService>().RunAsync("查东西", ToolCtx());

        Assert.Contains("已禁用", result);
        Assert.Equal(0, calls);
    }
}
