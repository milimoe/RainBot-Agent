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

        // 达上限后无文本 → 兜底文案，不死循环
        Assert.Equal("嗯……我暂时想不出怎么接这个话题，等我缓缓 🌧️", result.Text);
    }

    [Fact]
    public async Task LLM异常_兜底文案()
    {
        HttpResponseMessage Responder(HttpRequestMessage request)
            => new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

        ServiceProvider sp = await TestHost.BuildReadyAsync(Responder);
        var loop = sp.GetRequiredService<ReActLoop>();
        var result = await loop.RunAsync(
            [ChatMessage.System("测试"), ChatMessage.User("hi")],
            new ToolExecutionContext { GroupOpenId = "g", IsAdmin = false, AllowProfileUpdate = false });

        Assert.True(result.Failed);
        Assert.Equal("嗯……我暂时想不出怎么接这个话题，等我缓缓 🌧️", result.Text);
    }
}
