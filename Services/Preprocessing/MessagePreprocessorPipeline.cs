namespace RainBot.Services.Preprocessing;

/// <summary>独立运行所有已注册解析器，按注册顺序汇总；单个失败不阻断回复或其他解析器。</summary>
public sealed class MessagePreprocessorPipeline(
    IEnumerable<IMessagePreprocessor> processors,
    ILogger<MessagePreprocessorPipeline> logger,
    ParsedContentSender? sender = null)
{
    public async Task<List<PreprocessResult>> ProcessAsync(MessagePreprocessContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(processors.Select(async processor =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                budget.CancelAfter(processor.Timeout);
                var content = await processor.ProcessAsync(context, budget.Token).WaitAsync(budget.Token);
                if (processor.SendParsedContent && sender != null)
                    await sender.SendAsync(context, processor.Name, content, cancellationToken);
                return content.Select(item => new PreprocessResult(processor.Name, item)).ToList();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("消息预处理器 {Processor} 超时", processor.Name);
                return new List<PreprocessResult> { new(processor.Name, new("", "timeout", Error: "解析超时，未取得资料。")) };
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "消息预处理器 {Processor} 失败", processor.Name);
                return new List<PreprocessResult> { new(processor.Name, new("", "unavailable", Error: "解析失败，未取得资料。")) };
            }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return results.SelectMany(result => result).ToList();
    }
}
