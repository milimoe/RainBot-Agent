using RainBot.Models;
using RainBot.Services.Tools;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 工具调用记录器测试：ToolRegistry.ExecuteAsync 统一埋点（内置与 MCP 工具），
/// 记录名称/参数/结果/耗时/成败（启发式判定），环形缓冲上限，无记录器时不影响执行。
/// </summary>
public class ToolCallRecorderTests
{
    private static ToolExecutionContext Ctx() => new()
    {
        GroupOpenId = "group_test",
        IsAdmin = false,
        AllowProfileUpdate = false
    };

    private static ToolRegistry NewRegistry(ToolCallRecorder? recorder = null)
    {
        ToolRegistry registry = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolRegistry>.Instance, recorder);
        registry.RegisterExecutor("dummy_ok", (_, _) => Task.FromResult("一切正常"));
        registry.RegisterExecutor("dummy_fail", (_, _) => Task.FromResult("[工具执行错误]\n参数缺失"));
        registry.RegisterExecutor("dummy_timeout", (_, _) => Task.FromResult("工具 dummy_timeout 调用超时（30 秒）"));
        return registry;
    }

    [Fact]
    public async Task 成功调用_记录完整字段()
    {
        ToolCallRecorder recorder = new();
        ToolRegistry registry = NewRegistry(recorder);

        await registry.ExecuteAsync("dummy_ok", """{"q":"测试"}""", Ctx());

        ToolCallRecord record = Assert.Single(recorder.Latest());
        Assert.Equal("dummy_ok", record.Tool);
        Assert.True(record.Success);
        Assert.Contains("测试", record.Arguments);
        Assert.Contains("一切正常", record.Result);
        Assert.True(record.ElapsedMs >= 0);
        Assert.True(record.Seq > 0);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task 失败调用_按错误标记判定()
    {
        ToolCallRecorder recorder = new();
        ToolRegistry registry = NewRegistry(recorder);

        await registry.ExecuteAsync("dummy_fail", "{}", Ctx());
        await registry.ExecuteAsync("dummy_timeout", "{}", Ctx());
        await registry.ExecuteAsync("no_such_tool", "{}", Ctx()); // 未知工具

        IReadOnlyList<ToolCallRecord> records = recorder.Latest();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.False(r.Success));
    }

    [Fact]
    public void 环形缓冲_超过上限丢弃最旧记录()
    {
        ToolCallRecorder recorder = new();
        for (int i = 0; i < ToolCallRecorder.MaxRecords + 50; i++)
        {
            recorder.Record($"tool_{i}", "{}", "ok", 1);
        }

        Assert.Equal(ToolCallRecorder.MaxRecords, recorder.Count);
        IReadOnlyList<ToolCallRecord> latest = recorder.Latest(ToolCallRecorder.MaxRecords);
        // 最新一条是最后写入的
        Assert.Equal($"tool_{ToolCallRecorder.MaxRecords + 49}", latest[0].Tool);
        // 最旧的一条已被挤出
        Assert.Equal("tool_50", latest[^1].Tool);
    }

    [Fact]
    public void 长文本_截断到上限()
    {
        ToolCallRecorder recorder = new();
        recorder.Record("t", new string('a', 1000), new string('b', 1000), 1);

        ToolCallRecord record = Assert.Single(recorder.Latest());
        Assert.Equal(ToolCallRecorder.MaxTextLength + 1, record.Arguments!.Length); // 600 + 省略号
        Assert.Equal(ToolCallRecorder.MaxTextLength + 1, record.Result!.Length);
        Assert.EndsWith("…", record.Result);
    }

    [Fact]
    public async Task 无记录器_执行不受影响()
    {
        ToolRegistry registry = NewRegistry(recorder: null);
        string result = await registry.ExecuteAsync("dummy_ok", "{}", Ctx());
        Assert.Equal("一切正常", result);
    }

    [Theory]
    [InlineData("[工具执行错误]\nx", true)]
    [InlineData("未知工具：abc", true)]
    [InlineData("未知 MCP 工具：mcp__x__y", true)]
    [InlineData("工具参数解析失败：bad json", true)]
    [InlineData("工具执行异常：boom", true)]
    [InlineData("工具 t 调用超时（30 秒）", true)]
    [InlineData("工具 t 调用失败：refused", true)]
    [InlineData("搜索结果……正常内容", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 错误标记启发式判定(string? text, bool expected)
    {
        Assert.Equal(expected, ToolCallRecorder.LooksLikeError(text));
    }

    [Fact]
    public void JSON参数可以不是对象_原样记录()
    {
        ToolCallRecorder recorder = new();
        recorder.Record("t", "not-json", "ok", 1);
        ToolCallRecord record = Assert.Single(recorder.Latest());
        Assert.Equal("not-json", record.Arguments);
    }
}
