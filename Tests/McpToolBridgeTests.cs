using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RainBot.Services.Mcp;
using RainBot.Services.Tools;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// MCP 工具桥接测试：
/// 工具名生成（mcp__ 前缀 + 清洗）、JSON Schema → OpenAI 参数转换、
/// ToolRegistry 外部注册的顺序确定性与前缀稳定、工具结果格式化。
/// 纯函数测试，不依赖真实 MCP server。
/// </summary>
public class McpToolBridgeTests
{
    // ---------- 工具名生成 ----------

    [Fact]
    public void MakeToolName_带前缀且保留合法字符()
    {
        Assert.Equal("mcp__git__get_repo", McpSchemaConverter.MakeToolName("git", "get_repo"));
        Assert.Equal("mcp__my_server__foo-bar", McpSchemaConverter.MakeToolName("my.server", "foo-bar"));
    }

    [Theory]
    [InlineData("a b", "a_b")]
    [InlineData("a..b", "a_b")]
    [InlineData("!!!abc", "abc")]
    [InlineData("abc!!!", "abc")]
    [InlineData("a_b_c", "a_b_c")]
    [InlineData("汉字", "tool")]
    public void Sanitize_非法字符替换为下划线并修剪(string input, string expected)
    {
        Assert.Equal(expected, McpSchemaConverter.Sanitize(input));
    }

    [Fact]
    public void MakeToolName_空名称回退()
    {
        Assert.Equal("mcp__unnamed__tool", McpSchemaConverter.MakeToolName("", ""));
        Assert.Equal("mcp__server__tool", McpSchemaConverter.MakeToolName("server", ""));
    }

    // ---------- Schema 转换 ----------

    [Fact]
    public void ToOpenAiParameters_剔除不兼容关键字并保留核心结构()
    {
        string json = """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "title": "GetWeather",
          "type": "object",
          "properties": {
            "city": { "type": "string", "description": "城市", "enum": ["北京", "上海"] },
            "days": { "type": "integer", "minimum": 1 },
            "tags": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["city"],
          "$defs": { "X": { "type": "string" } }
        }
        """;
        JsonObject? p = McpSchemaConverter.ToOpenAiParameters(JsonDocument.Parse(json).RootElement);

        Assert.NotNull(p);
        Assert.False(p!.ContainsKey("$schema"));
        Assert.False(p.ContainsKey("$defs"));
        Assert.False(p.ContainsKey("title"));
        Assert.Equal("object", (string?)p["type"]);

        JsonObject props = (JsonObject)p["properties"]!;
        Assert.Equal("string", (string?)props["city"]!["type"]);
        Assert.Equal(2, ((JsonArray)props["city"]!["enum"]!).Count);
        Assert.Equal("integer", (string?)props["days"]!["type"]);
        Assert.Equal("string", (string?)((JsonObject)props["tags"]!["items"]!)["type"]);

        JsonArray required = (JsonArray)p["required"]!;
        Assert.Equal("city", (string?)required[0]);
    }

    [Fact]
    public void ToOpenAiParameters_缺顶层type自动补object()
    {
        JsonObject? p = McpSchemaConverter.ToOpenAiParameters(JsonDocument.Parse("""{ "properties": { "a": { "type": "string" } } }""").RootElement);
        Assert.NotNull(p);
        Assert.Equal("object", (string?)p!["type"]);
        Assert.NotNull(p["properties"]);
    }

    [Fact]
    public void ToOpenAiParameters_无效输入返回空对象()
    {
        JsonObject? p1 = McpSchemaConverter.ToOpenAiParameters(default);
        Assert.Equal("object", (string?)p1!["type"]);
        Assert.Empty((JsonObject)p1["properties"]!);

        JsonObject? p2 = McpSchemaConverter.ToOpenAiParameters(JsonDocument.Parse("null").RootElement);
        Assert.Equal("object", (string?)p2!["type"]);
    }

    // ---------- ToolRegistry 外部注册 ----------

    [Fact]
    public void RegisterExternal_追加在builtin之后且顺序确定()
    {
        ToolRegistry registry = new(NullLogger<ToolRegistry>.Instance);
        registry.RegisterExternal("mcp__srv-a__z_tool", "z 描述", Params("z"));
        registry.RegisterExternal("mcp__srv-b__a_tool", "a 描述", Params("a"));

        var defs = registry.GetToolDefs();
        // 7 个 builtin + 2 个外部
        Assert.Equal(9, defs.Count);
        Assert.Equal("mcp__srv-a__z_tool", defs[7].Function.Name);
        Assert.Equal("mcp__srv-b__a_tool", defs[8].Function.Name);
        Assert.Equal("z 描述", defs[7].Function.Description);
    }

    [Fact]
    public void GetSchemaJson_缓存稳定_同一前缀不变化()
    {
        ToolRegistry registry = new(NullLogger<ToolRegistry>.Instance);
        registry.RegisterExternal("mcp__srv__tool_a", "desc", Params("a"));

        string first = registry.GetSchemaJson();
        string second = registry.GetSchemaJson();
        Assert.Equal(first, second);
        Assert.Contains("mcp__srv__tool_a", first);
    }

    [Fact]
    public void RegisterExternal_与builtin名称隔离_执行器不互相干扰()
    {
        ToolRegistry registry = new(NullLogger<ToolRegistry>.Instance);
        JsonObject p = Params("x");
        registry.RegisterExternal("mcp__srv__web_search", "外部工具（与 builtin 同名不同前缀）", p);
        registry.RegisterExecutor("mcp__srv__web_search", (_, _) => Task.FromResult("external-hit"));

        // 外部工具按完整名分发
        string result = registry.ExecuteAsync("mcp__srv__web_search", "{}", Ctx()).Result;
        Assert.Equal("external-hit", result);

        // 完整名与 builtin 名并存，互不覆盖
        var names = registry.GetToolDefs().Select(d => d.Function.Name).ToList();
        Assert.Contains("web_search", names);
        Assert.Contains("mcp__srv__web_search", names);

        // builtin 名不会命中外部执行器（执行器按完整名注册）
        Assert.Equal("未知工具：web_search", registry.ExecuteAsync("web_search", "{}", Ctx()).Result);
    }

    // ---------- 结果格式化 ----------

    [Fact]
    public void FormatToolResult_文本内容与错误标志()
    {
        CallToolResult result = new()
        {
            Content = [new TextContentBlock { Text = "天气晴朗" }],
            IsError = false
        };
        string text = McpClientManager.FormatToolResult(result);
        Assert.Contains("天气晴朗", text);
        Assert.DoesNotContain("错误", text);

        CallToolResult error = new()
        {
            Content = [new TextContentBlock { Text = "参数缺失" }],
            IsError = true
        };
        Assert.Contains("工具执行错误", McpClientManager.FormatToolResult(error));
    }

    [Fact]
    public void FormatToolResult_结构化内容与空结果兜底()
    {
        CallToolResult structured = new()
        {
            Content = [],
            StructuredContent = JsonDocument.Parse("""{"count":3}""").RootElement.Clone()
        };
        Assert.Contains("count", McpClientManager.FormatToolResult(structured));

        CallToolResult empty = new() { Content = [] };
        Assert.Equal("(工具无返回内容)", McpClientManager.FormatToolResult(empty));
    }

    // ---------- 辅助 ----------

    private static JsonObject Params(string name)
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [name] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray(name)
        };
    }

    private static ToolExecutionContext Ctx() => new()
    {
        GroupOpenId = "g1",
        IsAdmin = false,
        AllowProfileUpdate = false
    };
}
