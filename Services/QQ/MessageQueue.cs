using System.Threading.Channels;
using RainBot.Models;

namespace RainBot.Services.QQ;

/// <summary>有界消息队列；会话内顺序消费积压批次，会话间并行。</summary>
public class MessageQueue : BackgroundService
{
    private readonly Channel<IncomingMessage> _channel = Channel.CreateUnbounded<IncomingMessage>(
        new UnboundedChannelOptions { SingleReader = true });
    // 覆盖入站通道和会话待处理列表，避免后台分流绕过容量限制。
    private readonly SemaphoreSlim _capacity = new(2000, 2000);
    private readonly object _gate = new();
    private readonly Dictionary<string, List<IncomingMessage>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _workers = [];
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MessageQueue> _logger;
    private int _pendingCount;

    public MessageQueue(IServiceProvider serviceProvider, ILogger<MessageQueue> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async ValueTask EnqueueAsync(IncomingMessage message)
    {
        await _capacity.WaitAsync();
        Interlocked.Increment(ref _pendingCount);
        try { await _channel.Writer.WriteAsync(message); }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            _capacity.Release();
            throw;
        }
    }

    public int PendingCount => Volatile.Read(ref _pendingCount);

    internal List<IncomingMessage> DrainForTest()
    {
        List<IncomingMessage> result = [];
        while (_channel.Reader.TryRead(out IncomingMessage? message))
        {
            result.Add(message);
            Interlocked.Decrement(ref _pendingCount);
            _capacity.Release();
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                lock (_gate)
                {
                    // 分流整个当前积压再启动消费者，确保初始积压同会话能够合并。
                    HashSet<string> newSessions = [];
                    while (_channel.Reader.TryRead(out IncomingMessage? message))
                    {
                        if (!_pending.TryGetValue(message.GroupOpenId, out var pending))
                        {
                            pending = [];
                            _pending.Add(message.GroupOpenId, pending);
                            newSessions.Add(message.GroupOpenId);
                        }
                        pending.Add(message);
                    }
                    foreach (string key in newSessions)
                    {
                        Task worker = Task.Run(() => ProcessSessionAsync(key, stoppingToken), CancellationToken.None);
                        _workers.Add(worker);
                    }
                    _workers.RemoveWhere(t => t.IsCompleted);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _channel.Writer.TryComplete();
            Task[] workers;
            lock (_gate) workers = _workers.ToArray();
            await Task.WhenAll(workers);
        }
    }

    private async Task ProcessSessionAsync(string key, CancellationToken ct)
    {
        while (true)
        {
            List<IncomingMessage> batch;
            lock (_gate)
            {
                var pending = _pending[key];
                if (pending.Count == 0)
                {
                    _pending.Remove(key);
                    return;
                }
                batch = [.. pending];
                pending.Clear();
                Interlocked.Add(ref _pendingCount, -batch.Count);
                _capacity.Release(batch.Count);
            }
            try
            {
                if (ct.IsCancellationRequested) return;
                using IServiceScope scope = _serviceProvider.CreateScope();
                MessageProcessor processor = scope.ServiceProvider.GetRequiredService<MessageProcessor>();
                await processor.ProcessBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理消息批次失败（会话={Group}, 条数={Count}）", key, batch.Count);
            }
        }
    }
}
