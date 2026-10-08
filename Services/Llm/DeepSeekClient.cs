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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ChatMessageConverter() }
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

        string? finishReason = chatResponse.Choices[0].FinishReason;
        if (string.IsNullOrWhiteSpace(message.Content) && message.ToolCalls is not { Count: > 0 })
        {
            // 空回复诊断：推理型模型的思维链计入输出 token，max_tokens 太小时预算被思考耗尽，
            // content 为空且 finish_reason=length——表现为「有回复但内容为空」。
            _logger.LogWarning(
                "LLM 空回复（finish_reason={Finish}，output={Out} tokens，reasoning={ReasoningLen} 字）：若 finish_reason=length 说明输出预算被思维链耗尽，请调大 Llm.ToolRoundMaxTokens",
                finishReason ?? "unknown", chatResponse.Usage?.CompletionTokens ?? 0, message.ReasoningContent?.Length ?? 0);
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
            RawUsage = responseBody,
            FinishReason = finishReason
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

/// <summary>
/// 对话消息（序列化顺序固定：role → content → tool_calls → tool_call_id → reasoning_content；
/// reasoning_content 仅响应反序列化，请求中恒为 null 不序列化）。
/// content 由 ChatMessageConverter 输出：无 ContentParts 时为字符串（与历史请求体完全一致，保前缀缓存），
/// 有 ContentParts 时为视觉格式的块数组（文本 + 图片）。
/// </summary>
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

    /// <summary>推理型模型的思维链（仅响应中出现；用于空回复诊断，不参与对话）</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    /// <summary>多模态内容块（仅触发消息带图时使用；非空则 content 序列化为块数组）</summary>
    [JsonIgnore]
    public List<ContentPart>? ContentParts { get; set; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    /// <summary>带多模态内容块的消息（文本块 + 图片块）</summary>
    public static ChatMessage UserWithParts(List<ContentPart> parts) => new() { Role = "user", ContentParts = parts };
    public static ChatMessage ToolResult(string toolCallId, string content) => new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

/// <summary>多模态内容块（OpenAI 兼容视觉格式）：text 或 image_url</summary>
public class ContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public ImageUrlPart? ImageUrl { get; set; }

    public static ContentPart TextPart(string text) => new() { Type = "text", Text = text };
    public static ContentPart ImagePart(string url) => new() { Type = "image_url", ImageUrl = new ImageUrlPart { Url = url } };
}

public class ImageUrlPart
{
    [JsonPropertyName("url")]
    public required string Url { get; set; }
}

/// <summary>
/// ChatMessage 专用序列化：手写以精确控制 content 的两种形态与字段顺序
/// （role → content → tool_calls → tool_call_id → reasoning_content），
/// 保证纯文本请求体与改造前逐字节一致，不扰动前缀缓存命中。
/// </summary>
public class ChatMessageConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("ChatMessage 应为 JSON 对象");
        }
        ChatMessage message = new() { Role = "" };
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return message;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }
            string property = reader.GetString() ?? "";
            reader.Read(); // 移到属性值
            switch (property)
            {
                case "role":
                    message.Role = reader.GetString() ?? "";
                    break;
                case "content":
                    message.Content = ReadContent(ref reader);
                    break;
                case "tool_calls":
                    message.ToolCalls = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<List<ChatToolCall>>(ref reader, options);
                    break;
                case "tool_call_id":
                    message.ToolCallId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "reasoning_content":
                    message.ReasoningContent = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        throw new JsonException("ChatMessage JSON 未正常结束");
    }

    /// <summary>响应侧 content：正常为字符串；防御性兼容块数组（拼接文本块）</summary>
    private static string? ReadContent(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return null;
        }
        System.Text.StringBuilder sb = new();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "text")
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        sb.Append(reader.GetString());
                    }
                }
            }
        }
        return sb.ToString();
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);
        if (value.ContentParts is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (ContentPart part in value.ContentParts)
            {
                writer.WriteStartObject();
                writer.WriteString("type", part.Type);
                if (part.Text != null)
                {
                    writer.WriteString("text", part.Text);
                }
                if (part.ImageUrl != null)
                {
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", part.ImageUrl.Url);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else if (value.Content != null)
        {
            writer.WriteString("content", value.Content);
        }
        if (value.ToolCalls is { Count: > 0 })
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        if (value.ToolCallId != null)
        {
            writer.WriteString("tool_call_id", value.ToolCallId);
        }
        if (value.ReasoningContent != null)
        {
            writer.WriteString("reasoning_content", value.ReasoningContent);
        }
        writer.WriteEndObject();
    }
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

    /// <summary>结束原因：stop（正常结束）/ length（max_tokens 截断）/ tool_calls 等</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
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

    /// <summary>结束原因：stop / length / tool_calls；null = 响应未携带</summary>
    public string? FinishReason { get; init; }
}
