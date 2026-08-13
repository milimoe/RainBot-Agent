using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RainBot.Services.WebUi;

/// <summary>一条推送给 WebUI 的实时事件</summary>
public sealed record WebUiEvent(string Type, DateTimeOffset Time, JsonObject? Data);

/// <summary>
/// WebUI 实时事件总线：内存环形缓冲（断线重连回放）+ SSE 订阅者广播。
/// 事件类型：message（群友消息）/ bot_message（机器人回复）/ status（连接状态）/ simulation（试聊模式变更）。
/// </summary>
public class WebUiEventBus
{
    private const int BufferCapacity = 2000;
    private readonly ConcurrentQueue<WebUiEvent> _buffer = new();
    private readonly ConcurrentDictionary<Guid, Channel<WebUiEvent>> _subscribers = new();

    /// <summary>发布事件：写入环形缓冲并广播给全部 SSE 订阅者</summary>
    public void Publish(string type, JsonObject? data = null)
    {
        WebUiEvent ev = new(type, DateTimeOffset.UtcNow, data);
        _buffer.Enqueue(ev);
        while (_buffer.Count > BufferCapacity)
        {
            _buffer.TryDequeue(out _);
        }
        foreach (Channel<WebUiEvent> channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(ev);
        }
    }

    /// <summary>订阅事件流（返回读取器；不再使用时必须调用 Unsubscribe）</summary>
    public ChannelReader<WebUiEvent> Subscribe()
    {
        Guid id = Guid.NewGuid();
        Channel<WebUiEvent> channel = Channel.CreateBounded<WebUiEvent>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest // 网页慢时丢最旧，保证实时性
        });
        _subscribers[id] = channel;
        return new TrackedReader(id, this, channel.Reader);
    }

    /// <summary>退订（SSE 断开时由读取器自动调用）</summary>
    public void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    /// <summary>最近 N 条事件（SSE 连接建立时回放，补历史缺口）</summary>
    public IReadOnlyList<WebUiEvent> Recent(int max = 500)
        => _buffer.TakeLast(max).ToList();

    /// <summary>当前订阅者数量（状态页展示）</summary>
    public int SubscriberCount => _subscribers.Count;

    /// <summary>包装读取器：ReadAllAsync 完成/取消时自动退订</summary>
    private sealed class TrackedReader(Guid id, WebUiEventBus bus, ChannelReader<WebUiEvent> inner) : ChannelReader<WebUiEvent>
    {
        public override Task Completion => inner.Completion;
        public override bool TryRead([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out WebUiEvent item) => inner.TryRead(out item);
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => inner.WaitToReadAsync(cancellationToken);

        public override async IAsyncEnumerable<WebUiEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (WebUiEvent item in inner.ReadAllAsync(cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                bus.Unsubscribe(id);
            }
        }
    }
}
