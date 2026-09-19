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
    /// <param name="messages">消息列表</param>
    /// <param name="tools">工具定义（null 或不传则不带 tools 字段）</param>
    /// <param name="maxTokens">本次请求 max_tokens</param>
    /// <param name="temperature">采样温度（null 用 Llm.Temperature）</param>
    /// <param name="toolChoice">工具选择策略："auto" / "none"。null 时不序列化该字段（保持请求体与历史一致）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef>? tools = null, int? maxTokens = null, double? temperature = null, string? toolChoice = null, CancellationToken ct = default)
    {
        var cfg = _config.Config.Llm;
        ChatRequest request = new()
        {
            Model = cfg.Model,
            Messages = messages,
            Tools = tools != null && tools.Count > 0 ? tools : null,
            Temperature = temperature ?? cfg.Temperature,
            MaxTokens = maxTokens,
            Stream = false,
            ToolChoice = toolChoice
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

    /// <summary>
    /// 查询 DeepSeek 账户余额（GET {BaseUrl}/user/balance，取第一个币种余额）。
    /// 未配置 API Key 或接口异常时抛 LlmException。
    /// </summary>
    public async Task<DeepSeekBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        var cfg = _config.Config.Llm;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            throw new LlmException("未配置 DeepSeek API Key");
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"{cfg.BaseUrl.TrimEnd('/')}/user/balance");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, timeout.Token);

        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException($"DeepSeek 余额查询返回 {response.StatusCode}：{Truncate(responseBody, 300)}");
        }

        BalanceResponse? balance = JsonSerializer.Deserialize<BalanceResponse>(responseBody);
        BalanceInfo? info = balance?.BalanceInfos?.FirstOrDefault();
        if (info == null)
        {
            throw new LlmException("DeepSeek 余额接口返回数据为空");
        }
        return new DeepSeekBalance
        {
            Available = balance!.IsAvailable,
            Currency = info.Currency,
            TotalBalance = info.TotalBalance,
            GrantedBalance = info.GrantedBalance,
            ToppedUpBalance = info.ToppedUpBalance
        };
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}

/// <summary>DeepSeek 账户余额（取首个币种）</summary>
public class DeepSeekBalance
{
    /// <summary>账户是否可用</summary>
    public bool Available { get; set; }

    /// <summary>币种（如 CNY）</summary>
    public string Currency { get; set; } = "";

    /// <summary>总余额</summary>
    public string TotalBalance { get; set; } = "";

    /// <summary>赠送余额</summary>
    public string GrantedBalance { get; set; } = "";

    /// <summary>充值余额</summary>
    public string ToppedUpBalance { get; set; } = "";
}

/// <summary>余额接口响应</summary>
public class BalanceResponse
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("balance_infos")]
    public List<BalanceInfo> BalanceInfos { get; set; } = [];
}

/// <summary>单个币种余额</summary>
public class BalanceInfo
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("total_balance")]
    public string TotalBalance { get; set; } = "";

    [JsonPropertyName("granted_balance")]
    public string GrantedBalance { get; set; } = "";

    [JsonPropertyName("topped_up_balance")]
    public string ToppedUpBalance { get; set; } = "";
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

/// <summary>
/// 请求体（序列化顺序固定：model → messages → tools → temperature → max_tokens → stream → tool_choice）。
/// 新增字段一律追加在末尾，避免扰动已有字段序列化顺序（前缀稳定优先于可读性）。
/// </summary>
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

    /// <summary>工具选择策略（none / auto / required）；null 不序列化</summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }
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
