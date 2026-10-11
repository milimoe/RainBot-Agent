using RainBot.Models;

namespace RainBot.Services.Preprocessing;

/// <summary>通过回复触发判定后、调用 LLM 前的本轮消息上下文。</summary>
public sealed record MessagePreprocessContext(
    IncomingMessage Current,
    IReadOnlyList<IncomingMessage> Batch,
    string QuotedText,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> QuotedVideoUrls,
    DateTimeOffset Now);

/// <summary>本轮解析资料；Source 可以是 URL、文件名或其他来源标识，Text 只包含实际解析到的内容。</summary>
public sealed record PreprocessedText(string Source, string Status = "ok", string Text = "", string Error = "")
{
    /// <summary>仅用于直接发送的资料卡片，不进入 LLM 文本上下文。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ParsedContentCard? DisplayCard { get; init; }
}

/// <summary>自动解析消息为 LLM 可直接读取的文本，不注册为模型工具；无适用内容时返回空集合。</summary>
public interface IMessagePreprocessor
{
    string Name { get; }
    /// <summary>解析完成后是否直接向当前会话发送成功资料；默认不开启。</summary>
    bool SendParsedContent => false;
    /// <summary>单个解析器的最大等待时间；实现仍须尊重 cancellationToken。</summary>
    TimeSpan Timeout => TimeSpan.FromSeconds(10);
    Task<IReadOnlyList<PreprocessedText>> ProcessAsync(MessagePreprocessContext context, CancellationToken cancellationToken);
}

/// <summary>标记解析器名称，使不同来源的资料在上下文中可辨认。</summary>
public sealed record PreprocessResult(string Processor, PreprocessedText Content);
