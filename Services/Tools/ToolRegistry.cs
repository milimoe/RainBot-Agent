using System.Text.Json;
using System.Text.Json.Nodes;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Llm;

namespace RainBot.Services.Tools;

/// <summary>
/// 工具注册表：工具定义（Block B 的 JSON Schema 来源）与执行分发。
/// 注册顺序固定 → Schema 序列化顺序固定 → 前缀稳定。
/// </summary>
public class ToolRegistry
{
    private readonly List<RegisteredTool> _tools = [];
    private readonly ILogger<ToolRegistry> _logger;
    private readonly ToolCallRecorder? _recorder;
    private string? _schemaJson;

    public ToolRegistry(ILogger<ToolRegistry> logger, ToolCallRecorder? recorder = null)
    {
        _logger = logger;
        _recorder = recorder;
        RegisterBuiltins();
    }

    private void RegisterBuiltins()
    {
        // 注意：注册顺序即 Block B 输出顺序，勿随意调整
        Register("web_search",
            "联网搜索实时信息。需要最新资讯、事实核查、不知道答案时调用。返回简短搜索结果列表。",
            Parameters(("query", "搜索关键词", true, "string"), ("topic", "话题分类（可选，用于缓存归类）", false, "string")));

        Register("get_user_profile",
            "获取某位群友的画像信息（兴趣、习惯、过往话题）。参数 user 为群友短 ID（如 u123456）或完整 openid。仅在你需要了解某人背景以更好回应时调用。",
            Parameters(("user", "群友短 ID（u 开头）或 openid", true, "string")));

        Register("update_user_profile",
            "更新某位群友的画像（标签、兴趣、习惯、总结）。仅管理员主动暖群场景可用，用于沉淀群友特征。",
            Parameters(("user", "群友短 ID（u 开头）或 openid", true, "string"),
                       ("tags", "标签数组，如 [\"装机\",\"显卡\"]", false, "array"),
                       ("interests", "兴趣描述", false, "string"),
                       ("habits", "表达习惯描述", false, "string"),
                       ("summary", "对 TA 的观察总结", false, "string")));

        Register("admin_set_setting",
            "修改机器人运行参数（仅管理员）。key 形如 Trigger.PassiveCooldownSeconds，value 为数值或字符串。修改即时生效并持久化。",
            Parameters(("key", "参数键", true, "string"), ("value", "参数值", true, "string")));

        Register("admin_get_stats",
            "查看机器人运行统计：各群缓存命中率、消息数、触发次数。",
            Parameters());

        Register("admin_mute_group",
            "静默指定群（仅管理员），静默期间不主动发言、不响应 @。minutes 为静默时长（分钟），缺省永久。",
            Parameters(("minutes", "静默时长（分钟），缺省永久", false, "integer")));

        Register("admin_unmute_group",
            "解除当前群静默（仅管理员）。",
            Parameters());
    }

    /// <summary>
    /// 注册外部工具（如 MCP 工具），追加在 builtin 之后。
    /// 调用方需保证调用顺序确定（如按 server/tool 名排序），以维持 Block B 前缀稳定。
    /// </summary>
    public void RegisterExternal(string name, string description, JsonObject parameters)
    {
        _tools.Add(new RegisteredTool
        {
            Name = name,
            Description = description,
            Parameters = parameters
        });
        _schemaJson = null; // 失效缓存
    }

    private void Register(string name, string description, JsonObject parameters)
    {
        _tools.Add(new RegisteredTool
        {
            Name = name,
            Description = description,
            Parameters = parameters
        });
        _schemaJson = null; // 失效缓存
    }

    /// <summary>Block B：确定性序列化的工具 Schema（缓存）</summary>
    public string GetSchemaJson()
    {
        if (_schemaJson != null)
        {
            return _schemaJson;
        }
        List<ToolDef> defs = _tools.Select(t => new ToolDef
        {
            Function = new ToolFunction
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = JsonSerializer.Deserialize<JsonElement>(t.Parameters.ToJsonString())
            }
        }).ToList();
        _schemaJson = JsonSerializer.Serialize(defs, new JsonSerializerOptions { WriteIndented = true });
        return _schemaJson;
    }

    /// <summary>发给 API 的 ToolDef 列表（顺序固定）</summary>
    public List<ToolDef> GetToolDefs()
    {
        return _tools.Select(t => new ToolDef
        {
            Function = new ToolFunction
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = JsonSerializer.Deserialize<JsonElement>(t.Parameters.ToJsonString())
            }
        }).ToList();
    }

    /// <summary>执行工具（由 ReActLoop 调用）。统一埋点：所有工具调用（含 MCP）进 ToolCallRecorder。</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, ToolExecutionContext context)
    {
        System.Diagnostics.Stopwatch? sw = _recorder == null ? null : System.Diagnostics.Stopwatch.StartNew();
        string result;
        try
        {
            ToolExecutor? executor = _executors.GetValueOrDefault(name);
            if (executor == null)
            {
                result = $"未知工具：{name}";
            }
            else
            {
                result = await executor(argumentsJson, context);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("工具 {Name} 参数解析失败：{Error}", name, ex.Message);
            result = $"工具参数解析失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工具 {Name} 执行异常", name);
            result = $"工具执行异常：{ex.Message}";
        }
        _recorder?.Record(name, argumentsJson, result, sw!.ElapsedMilliseconds);
        return result;
    }

    // ---------- 执行器注册 ----------

    private readonly Dictionary<string, ToolExecutor> _executors = [];

    public delegate Task<string> ToolExecutor(string argumentsJson, ToolExecutionContext context);

    /// <summary>注册工具执行器</summary>
    public void RegisterExecutor(string name, ToolExecutor executor) => _executors[name] = executor;

    /// <summary>解析工具调用参数</summary>
    public static JsonObject? ParseArguments(string argumentsJson)
    {
        JsonNode? node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        return node as JsonObject;
    }

    private static JsonObject Parameters(params (string Name, string Description, bool Required, string Type)[] fields)
    {
        JsonObject properties = [];
        JsonArray required = [];
        foreach ((string name, string description, bool isRequired, string type) in fields)
        {
            JsonObject field = new()
            {
                ["type"] = type,
                ["description"] = description
            };
            if (type == "array")
            {
                field["items"] = new JsonObject { ["type"] = "string" };
            }
            properties[name] = field;
            if (isRequired)
            {
                required.Add(name);
            }
        }
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
        return schema;
    }
}

/// <summary>已注册工具</summary>
public class RegisteredTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
}

/// <summary>工具执行上下文（由 ReActLoop 传入）</summary>
public class ToolExecutionContext
{
    public required string GroupOpenId { get; init; }
    public string? SenderOpenId { get; init; }
    public required bool IsAdmin { get; init; }
    public required bool AllowProfileUpdate { get; init; }
}
