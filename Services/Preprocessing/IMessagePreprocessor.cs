using RainBot.Models;

namespace RainBot.Services.Preprocessing;

/// <summary>随机互动和回复触发判定前的本轮消息上下文。</summary>
public sealed record MessagePreprocessContext(
    IncomingMessage Current,
    IReadOnlyList<IncomingMessage> Batch,
    string QuotedText,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> QuotedVideoUrls,
    DateTimeOffset Now)
{
    /// <summary>仅本批新消息自身携带的链接可触发直发；引用和历史回溯不在其中。</summary>
    internal Dictionary<string, IncomingMessage> GetDirectLinkSources(Func<IncomingMessage, IEnumerable<string>> extract)
    {
        Dictionary<string, IncomingMessage> sources = new(StringComparer.Ordinal);
        foreach (var message in Batch.Prepend(Current).Where(m => !m.SkipSideEffects && !m.IsFromBot))
            foreach (string link in extract(message)) sources.TryAdd(link, message);
        return sources;
    }
}

/// <summary>本轮解析资料；Source 可以是 URL、文件名或其他来源标识，Text 只包含实际解析到的内容。</summary>
public sealed record PreprocessedText(string Source, string Status = "ok", string Text = "", string Error = "")
{
    /// <summary>仅用于直接发送的资料卡片，不进入 LLM 文本上下文。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ParsedContentCard? DisplayCard { get; init; }

    /// <summary>引用或历史回溯资料仅供模型读取，不直接发送卡片或风控提示。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowDirectSend { get; init; } = true;

    /// <summary>直发应关联的原始链接消息；其他类型解析器未指定时沿用 Current。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IncomingMessage? DirectSendMessage { get; init; }
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

