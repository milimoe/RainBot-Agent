using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainBot.Services.Config;

namespace RainBot.Services.Llm;

/// <summary>
/// DeepSeek OpenAI 兼容客户端（仅用单一模型）。
/// 请求体序列化顺序固定（属性声明顺序即 JSON 顺序），保证前缀稳定命中硬盘缓存。
/// </summary>
public class DeepSeekClient(RuntimeConfig config, IHttpClientFactory httpClientFactory, ILogger<DeepSeekClient> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("deepseek");
    private readonly ILogger<DeepSeekClient> _logger = logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>发起一次对话（含工具），返回助手回复与用量统计</summary>
    public async Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef>? tools = null, int? maxTokens = null, CancellationToken ct = default)
    {
        var cfg = _config.Config.Llm;
        ChatRequest request = new()
        {
            Model = cfg.Model,
            Messages = messages,
            Tools = tools != null && tools.Count > 0 ? tools : null,
            Temperature = cfg.Temperature,
            MaxTokens = maxTokens,
            Stream = false
        };

        string json = JsonSerializer.Serialize(request, JsonOptions);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, $"{cfg.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = content
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, timeout.Token);

        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException($"DeepSeek API 返回 {response.StatusCode}：{Truncate(responseBody, 300)}");
        }

        ChatResponse? chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);
        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new LlmException("DeepSeek API 返回空结果");
        }

        ChatMessage? message = chatResponse.Choices[0].Message;
        if (message == null)
        {
            throw new LlmException("DeepSeek API 返回无消息内容");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Usage? u = chatResponse.Usage;
            double rate = u != null && u.PromptTokens > 0 ? (double)u.PromptCacheHitTokens / u.PromptTokens : 0;
            _logger.LogDebug("LLM 用量：input={In}（hit={Hit} miss={Miss}）output={Out}，cache_rate={Rate:0.00}",
                u?.PromptTokens, u?.PromptCacheHitTokens, u?.PromptCacheMissTokens, u?.CompletionTokens, rate);
        }

        return new ChatResult
        {
            Message = message,
            Usage = chatResponse.Usage,
            RawUsage = responseBody
        };
    }

    /// <summary>模型名（供日志/统计）</summary>
    public string ModelName => _config.Config.Llm.Model;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

/// <summary>LLM 调用异常</summary>
public class LlmException(string message) : Exception(message);

/// <summary>对话消息（序列化顺序固定：role → content → tool_calls → tool_call_id）</summary>
public class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ChatToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage ToolResult(string toolCallId, string content) => new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

public class ChatToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatToolCallFunction Function { get; set; } = new();
}

public class ChatToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

/// <summary>工具定义（请求体顶级 tools 字段，前缀固定部分）</summary>
public class ToolDef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolFunction Function { get; set; } = new();
}

public class ToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; } = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}");
}

/// <summary>请求体（序列化顺序固定：model → messages → tools → temperature → max_tokens → stream）</summary>
public class ChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatMessage> Messages { get; set; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolDef>? Tools { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.9;

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

public class ChatResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }
}

public class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}

/// <summary>DeepSeek 用量（缓存命中/未命中为顶层字段）</summary>
public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("prompt_cache_hit_tokens")]
    public int PromptCacheHitTokens { get; set; }

    [JsonPropertyName("prompt_cache_miss_tokens")]
    public int PromptCacheMissTokens { get; set; }

    /// <summary>缓存命中率（0~1）</summary>
    [JsonIgnore]
    public double CacheHitRate => PromptTokens > 0 ? (double)PromptCacheHitTokens / PromptTokens : 0;
}

public class ChatResult
{
    public required ChatMessage Message { get; init; }
    public Usage? Usage { get; init; }
    public string? RawUsage { get; init; }
}
