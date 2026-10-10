using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Profile;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

public class MessageBacklogTests
{
    private static HttpResponseMessage Reply(string text = "收到")
        => new(HttpStatusCode.OK) { Content = new StringContent(TestHelpers.LlmTextResponse(text), Encoding.UTF8, "application/json") };

    private static void DisableFun(ServiceProvider sp)
    {
        var config = sp.GetRequiredService<RuntimeConfig>().Config;
        foreach (var property in typeof(FunConfig).GetProperties().Where(p => p.Name.StartsWith("Enable")))
            property.SetValue(config.Fun, false);
        config.Trigger.RandomChatProbability = 0;
        config.Trigger.PassiveCooldownSeconds = 0;
    }

    [Fact]
    public async Task 同群积压多条艾特_保存全部历史_仅触发一次并包含全部消息()
    {
        int calls = 0;
        string request = "";
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            Interlocked.Increment(ref calls);
            request = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Reply();
        });
        DisableFun(sp);
        var processor = ActivatorUtilities.CreateInstance<MessageProcessor>(sp);
        await processor.ProcessBatchAsync([
            TestHelpers.Msg("batch", "alice", "first question", isAt: true),
            TestHelpers.Msg("batch", "bob", "second question", isAt: true)
        ], CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(2, sp.GetRequiredService<HistoryStore>().Count("batch"));
        Assert.Contains("first question", request);
        Assert.Contains("second question", request);
    }

    [Fact]
    public async Task 超龄消息_保留统计历史_不执行指令娱乐和模型()
    {
        int calls = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ => { calls++; return Reply(); });
        var config = sp.GetRequiredService<RuntimeConfig>().Config;
        config.Trigger.BacklogMaxAgeSeconds = 10;
        var processor = ActivatorUtilities.CreateInstance<MessageProcessor>(sp);
        await processor.ProcessBatchAsync([
            TestHelpers.Msg("old", "alice", "old question", isAt: true, msgTime: DateTimeOffset.UtcNow.AddMinutes(-1)),
            TestHelpers.Msg("old", "bob", "/admin help", isAt: true, msgTime: DateTimeOffset.UtcNow.AddMinutes(-1))
        ], CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(1, sp.GetRequiredService<HistoryStore>().Count("old")); // 已识别指令不进入模型历史
        Assert.True(sp.GetRequiredService<GroupStateManager>().GetOrCreate("old").LastMessageUtc > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task 私聊积压_逐条回复()
    {
        int calls = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ => { calls++; return Reply(); });
        DisableFun(sp);
        var processor = ActivatorUtilities.CreateInstance<MessageProcessor>(sp);
        await processor.ProcessBatchAsync([
            new IncomingMessage { MsgId = "p1", GroupOpenId = "bot:puser", SenderOpenId = "user", Content = "one", IsAtRobot = true, IsPrivate = true },
            new IncomingMessage { MsgId = "p2", GroupOpenId = "bot:puser", SenderOpenId = "user", Content = "two", IsAtRobot = true, IsPrivate = true }
        ], CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 工作流不同会话_首个模型阻塞时另一会话仍完成()
    {
        using ManualResetEventSlim entered = new(false), release = new(false);
        int calls = 0;
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            return Reply();
        });
        var runner = sp.GetRequiredService<WorkflowRunner>();
        TriggerContext Context(string group) => new() { GroupOpenId = group, Type = TriggerType.Passive, Reason = "test" };
        Task<bool> first = Task.Run(() => runner.RunAsync(Context("slow"), null));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<bool> second = Task.Run(() => runner.RunAsync(Context("fast"), null));
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public async Task 队列_慢会话阻塞不影响其他会话_恢复后合并积压()
    {
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fastDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource backlogDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        string backlogInput = "";
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            int index = Interlocked.Increment(ref calls);
            if (index == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else if (index == 2) fastDone.TrySetResult();
            else
            {
                backlogInput = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                backlogDone.TrySetResult();
            }
            return Reply();
        });
        DisableFun(sp);
        MessageQueue queue = sp.GetRequiredService<MessageQueue>();
        await queue.EnqueueAsync(TestHelpers.Msg("slowqueue", "alice", "initial", isAt: true));
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.EnqueueAsync(TestHelpers.Msg("slowqueue", "alice", "pending one", isAt: true));
            await queue.EnqueueAsync(TestHelpers.Msg("slowqueue", "bob", "pending two", isAt: true));
            await queue.EnqueueAsync(TestHelpers.Msg("fastqueue", "charlie", "fast question", isAt: true));
            await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, calls);
            release.Set();
            await backlogDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, calls);
            Assert.Contains("pending one", backlogInput);
            Assert.Contains("pending two", backlogInput);
            Assert.Equal(0, queue.PendingCount);
        }
        finally { release.Set(); await queue.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task 蒸馏_水位处理立即裁剪降级_后台使用原历史快照()
    {
        string input = "";
        await using ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            input = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Reply("""["summary"]""");
        });
        var store = sp.GetRequiredService<HistoryStore>();
        for (int i = 0; i < 5; i++)
            await store.AppendAsync("distill", TestHelpers.Msg("distill", "alice", "snapshot" + i));
        var watermark = sp.GetRequiredService<WatermarkManager>();
        var distiller = sp.GetRequiredService<Distiller>();
        var action = await watermark.EnforceAsync(new ComposeResult
        {
            GroupOpenId = "distill", Messages = [],
            EstimatedTokens = int.MaxValue
        });
        Assert.Equal(WatermarkAction.Distilled, action);
        Assert.Equal(3, store.Count("distill"));
        Assert.True(sp.GetRequiredService<GroupStateManager>().GetOrCreate("distill").Degraded);
        Assert.Empty(input); // LLM 尚未启动，水位治理已经返回。
        await distiller.StartAsync(CancellationToken.None);
        try
        {
            var db = sp.GetRequiredService<Database>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await db.GetRecentDistillSummariesAsync("distill")).Count == 0)
                await Task.Delay(20, timeout.Token);
            Assert.Contains("snapshot0", input);
            Assert.Contains("snapshot4", input);
            Assert.Contains("ualice", input);
            var compose = await sp.GetRequiredService<BlockComposer>().BuildAsync(new TriggerContext
            {
                GroupOpenId = "distill", Type = TriggerType.Passive, Reason = "next reply"
            });
            Assert.Contains("summary", compose.Messages[1].Content);
        }
        finally { await distiller.StopAsync(CancellationToken.None); }
    }
}
