using System.Text.Json;
using System.Text.Json.Nodes;

namespace RainBot.Services.Mcp;

/// <summary>
/// MCP 工具桥接的纯函数部分（无外部依赖，便于单元测试）：
/// - MCP 工具名 → RainBot 工具名（mcp__{server}__{tool}，非法字符清洗）；
/// - MCP JSON Schema → OpenAI function calling 参数 Schema（剔除不兼容关键字）。
/// </summary>
public static class McpSchemaConverter
{
    /// <summary>RainBot 外部工具统一前缀（与 builtin 隔离，防冲突）</summary>
    public const string ToolPrefix = "mcp__";

    /// <summary>
    /// 生成 RainBot 工具名：mcp__{server}__{tool}。
    /// 清洗为 [a-zA-Z0-9_-]（OpenAI function 名约束），保证确定性。
    /// </summary>
    public static string MakeToolName(string serverName, string toolName)
    {
        string Clean(string s) => string.IsNullOrWhiteSpace(s) ? "unnamed" : Sanitize(s);
        return $"{ToolPrefix}{Clean(serverName)}__{Sanitize(toolName)}";
    }

    /// <summary>把任意字符串清洗为 [a-zA-Z0-9_-]（其余替换为 _，连续 _ 合并）</summary>
    public static string Sanitize(string s)
    {
        System.Text.StringBuilder sb = new(s.Length);
        bool lastUnderscore = false;
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                sb.Append(c);
                lastUnderscore = c == '_';
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }
        string result = sb.ToString().Trim('_');
        return result.Length == 0 ? "tool" : result;
    }

    /// <summary>
    /// 把 MCP 工具输入 JSON Schema（JsonElement）转换为 OpenAI function calling 参数 Schema（JsonObject）。
    /// 剔除 DeepSeek 不兼容/无用关键字：$schema、$defs、definitions、$ref、title；
    /// 顶层若无 type 则补 "object"。其余（properties/required/enum/items/嵌套结构）原样保留。
    /// 返回 null 表示 schema 无效（视为无参数）。
    /// </summary>
    public static JsonObject? ToOpenAiParameters(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Undefined || schema.ValueKind == JsonValueKind.Null)
        {
            return EmptyObject();
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return EmptyObject();
        }

        JsonObject copy = new();
        foreach (JsonProperty prop in schema.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "$schema":
                case "$defs":
                case "definitions":
                case "$ref":
                case "$id":
                case "title":
                    continue; // 不支持的关键字直接剔除
                default:
                    copy[prop.Name] = JsonSerializer.SerializeToNode(prop.Value); // 复制语义，生命周期安全
                    break;
            }
        }

        // 顶层必须是 object 类型（OpenAI function calling 约定）
        if (copy["type"] is null)
        {
            copy["type"] = "object";
        }
        if (copy["properties"] is null)
        {
            copy["properties"] = new JsonObject();
        }
        return copy;
    }

    private static JsonObject EmptyObject()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        };
    }
}
