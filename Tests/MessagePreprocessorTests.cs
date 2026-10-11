using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Preprocessing;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

public class MessagePreprocessorTests
{
    private static MessagePreprocessContext Context() => new(
        TestHelpers.Msg("group", "alice", "问题", isAt: true), [], "引用", ["image"], [], DateTimeOffset.UtcNow);

    private static MessagePreprocessorPipeline Pipeline(params IMessagePreprocessor[] processors) =>
        new(processors, NullLogger<MessagePreprocessorPipeline>.Instance);

    [Fact]
    public async Task 汇总多个解析器并隔离失败且不泄露异常内容()
    {
        var pipeline = Pipeline(
            new Parser("first", (_, _) => Task.FromResult<IReadOnlyList<PreprocessedText>>([new("文件", Text: "正文")])),
            new Parser("broken", (_, _) => throw new InvalidOperationException("内部敏感信息")),
            new Parser("skip", (_, _) => Task.FromResult<IReadOnlyList<PreprocessedText>>([])),
            new Parser("last", (_, _) => Task.FromResult<IReadOnlyList<PreprocessedText>>([new("音频", Text: "转录")])));
        var results = await pipeline.ProcessAsync(Context(), default);
        Assert.Equal(new[] { "first", "broken", "last" }, results.Select(result => result.Processor));
        Assert.Equal("unavailable", results[1].Content.Status);
        Assert.DoesNotContain("内部敏感信息", results[1].Content.Error);
        Assert.Equal("转录", results[2].Content.Text);
    }

    [Fact]
    public async Task 不响应取消的解析器也有等待预算()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<PreprocessedText>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new Parser("slow", (_, _) => pending.Task, TimeSpan.FromMilliseconds(30));
        try
        {
            var results = await Pipeline(parser).ProcessAsync(Context(), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("timeout", Assert.Single(results).Content.Status);
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task 外部取消传播而不是转成解析失败()
    {
        using var cancellation = new CancellationTokenSource();
        var parser = new Parser("cancel", async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(parser).ProcessAsync(Context(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().ProcessAsync(Context(), cancellation.Token));
    }

    [Fact]
    public async Task 消息回复前自动解析并仅注入本轮上下文()
    {
        string request = "";
        await using var provider = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("chat/completions"))
                request = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(TestHelpers.LlmTextResponse("收到"), Encoding.UTF8, "application/json") };
        });
        var config = provider.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(config.Fun, false);
        config.Trigger.RandomChatProbability = 0;
        config.Trigger.PassiveCooldownSeconds = 0;
        MessagePreprocessContext? received = null;
        var pipeline = Pipeline(new Parser("文档解析", (ctx, _) =>
        {
            received = ctx;
            return Task.FromResult<IReadOnlyList<PreprocessedText>>([new("附件.txt", Text: "唯一解析正文")]);
        }));
        var processor = ActivatorUtilities.CreateInstance<MessageProcessor>(provider, pipeline);
        var message = TestHelpers.Msg("group", "alice", "看看附件", isAt: true);
        await processor.ProcessAsync(message, default);
        Assert.NotNull(received);
        Assert.Same(message, received.Current);
        Assert.Single(received.Batch);
        using var body = JsonDocument.Parse(request);
        Assert.Contains("唯一解析正文", body.RootElement.GetProperty("messages")[3].GetProperty("content").GetString());
        var history = provider.GetRequiredService<HistoryStore>().GetRecent("group", 10000, out _);
        Assert.DoesNotContain(history, entry => entry.Content.Contains("唯一解析正文"));

        var composed = await provider.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
        {
            GroupOpenId = "group", Type = TriggerType.Passive, Reason = "测试",
            PreprocessedResults = [new("文档解析", new("附件.txt", Text: "唯一解析正文")), new("音频", new("录音", "timeout", Error: "转录超时"))]
        });
        Assert.Contains("唯一解析正文", composed.Messages[3].Content);
        Assert.Contains("转录超时", composed.Messages[3].Content);
        Assert.Contains("外部非可信数据", composed.Messages[3].Content);
        Assert.DoesNotContain("唯一解析正文", composed.Messages[2].Content);
    }

    private sealed class Parser(string name,
        Func<MessagePreprocessContext, CancellationToken, Task<IReadOnlyList<PreprocessedText>>> process,
        TimeSpan? timeout = null) : IMessagePreprocessor
    {
        public string Name => name;
        public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(10);
        public Task<IReadOnlyList<PreprocessedText>> ProcessAsync(MessagePreprocessContext context, CancellationToken cancellationToken) =>
            process(context, cancellationToken);
    }
}
