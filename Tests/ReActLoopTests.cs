using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Llm;
using RainBot.Services.Tools;
using Xunit;

namespace RainBot.Tests;

public class ReActLoopTests
{
    [Fact]
    public async Task 工具调用后继续_返回最终文本()
    {
        int callCount = 0;
        HttpResponseMessage Responder(HttpRequestMessage request)
        {
            callCount++;
            string json;
            if (callCount == 1)
            {
                // 第一轮：请求调用 web_search
                json = """
                {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                  {"id":"call_1","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"今天天气\"}"}}
                ]}}],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10}}
                """;
            }
            else
            {
                // 第二轮：给出最终回答
                json = TestHelpers.LlmTextResponse("今天有雨，记得带伞 ☔");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var registry = sp.GetRequiredService<ToolRegistry>();
        registry.RegisterExecutor("web_search", (args, _) => Task.FromResult("搜索结果：今天有雨，气温 22°C"));

        var loop = sp.GetRequiredService<ReActLoop>();
        var result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("今天天气怎么样？")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.False(result.Failed);
        Assert.Contains("有雨", result.Text);
        Assert.Equal(2, callCount); // 工具调用轮 + 最终轮
        Assert.NotNull(result.Usage);
        Assert.True(result.Usage.PromptCacheHitTokens > 0);
    }

    [Fact]
    public async Task 工具轮次上限_防死循环()
    {
        HttpResponseMessage Responder(HttpRequestMessage request)
        {
            // 永远请求工具（模拟模型死循环）
            string json = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
              {"id":"call_x","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"x\"}"}}
            ]}}],"usage":{"prompt_tokens":50,"completion_tokens":5,"prompt_cache_hit_tokens":40,"prompt_cache_miss_tokens":10}}
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var registry = sp.GetRequiredService<ToolRegistry>();
        registry.RegisterExecutor("web_search", (_, _) => Task.FromResult("结果"));

        var loop = sp.GetRequiredService<ReActLoop>();
        var result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        // 达上限后无文本 → 空文本（由上层静默跳过），不死循环
        Assert.Equal("", result.Text);
    }

    [Theory]
    [InlineData("(empty)")]
    [InlineData("  (empty)\n")]
    [InlineData("(empty)。")]         // 句尾多余标点
    [InlineData("```\n（空）\n```")] // 代码块包裹 + 全角变体：兼容模型自由发挥
    public async Task 空内容哨兵_归一为空文本(string marker)
    {
        // 随机插嘴约定「接不上就输出 (empty)」：哨兵必须被识别为空文本，不能当普通回复发出去
        ServiceProvider sp = await TestHost.BuildReadyAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestHelpers.LlmTextResponse(marker), Encoding.UTF8, "application/json")
        });
        var loop = sp.GetRequiredService<ReActLoop>();
        ReActResult result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.False(result.Failed);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task LLM异常_返回空文本不编兜底话术()
    {
        HttpResponseMessage Responder(HttpRequestMessage request)
            => new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var loop = sp.GetRequiredService<ReActLoop>();
        var result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.True(result.Failed);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task 触顶收口_基于工具结果作答且禁用工具()
    {
        List<string> bodies = [];
        HttpResponseMessage Responder(HttpRequestMessage request)
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            // 前 3 次（= MaxToolRounds）一直请求工具，第 4 次（收口轮）才给文本
            string json = bodies.Count <= 3
                ? """
                  {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                    {"id":"call_loop","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"x\"}"}}
                  ]}}],"usage":{"prompt_tokens":50,"completion_tokens":5,"prompt_cache_hit_tokens":40,"prompt_cache_miss_tokens":10}}
                  """
                : TestHelpers.LlmTextResponse("查到了，今天有雨 ☔");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var registry = sp.GetRequiredService<ToolRegistry>();
        registry.RegisterExecutor("web_search", (_, _) => Task.FromResult("结果：今天有雨，22°C"));

        var loop = sp.GetRequiredService<ReActLoop>();
        var result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.Equal(4, bodies.Count);                            // 3 工具轮 + 1 收口轮
        Assert.Equal(3, result.ToolCallCount);
        Assert.Contains("有雨", result.Text);                      // 用工具结果作答，而不是丢结果兜底
        Assert.Contains("\"tool_choice\":\"none\"", bodies[^1]);   // 收口轮禁用工具
        Assert.DoesNotContain("tool_choice", bodies[0]);           // 普通轮不序列化 → 请求体前缀不变
    }

    [Fact]
    public async Task 温度两档_首轮主温度_工具链中段降温()
    {
        List<string> bodies = [];
        HttpResponseMessage Responder(HttpRequestMessage request)
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            string json = bodies.Count == 1
                ? """
                  {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                    {"id":"call_1","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"x\"}"}}
                  ]}}],"usage":{"prompt_tokens":50,"completion_tokens":5,"prompt_cache_hit_tokens":40,"prompt_cache_miss_tokens":10}}
                  """
                : TestHelpers.LlmTextResponse("今天有雨 ☔");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var registry = sp.GetRequiredService<ToolRegistry>();
        registry.RegisterExecutor("web_search", (_, _) => Task.FromResult("结果"));

        var loop = sp.GetRequiredService<ReActLoop>();
        await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"temperature\":0.9", bodies[0]); // 首轮：主温度（保人设）
        Assert.Contains("\"temperature\":0.2", bodies[1]); // 工具链中段：降温（稳参数）
    }
}
