using System.Threading.Channels;
using RainBot.Models;

namespace RainBot.Services.QQ;

/// <summary>
/// 消息处理队列：协议层写入，后台消费者串行处理（保证同群消息顺序）。
/// </summary>
public class MessageQueue : BackgroundService
{
    private readonly Channel<IncomingMessage> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MessageQueue> _logger;

    public MessageQueue(IServiceProvider serviceProvider, ILogger<MessageQueue> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _channel = Channel.CreateBounded<IncomingMessage>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ValueTask EnqueueAsync(IncomingMessage message) => _channel.Writer.WriteAsync(message);

    /// <summary>当前排队待处理的消息数（状态页展示）</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>取出当前队列中的全部消息（仅测试用）</summary>
    internal List<IncomingMessage> DrainForTest()
    {
        List<IncomingMessage> result = [];
        while (_channel.Reader.TryRead(out IncomingMessage? message))
        {
            result.Add(message);
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (IncomingMessage message in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = _serviceProvider.CreateScope();
                MessageProcessor processor = scope.ServiceProvider.GetRequiredService<MessageProcessor>();
                await processor.ProcessAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单条消息处理失败不影响队列继续消费
                _logger.LogError(ex, "处理消息失败（group={Group}, msg={MsgId}）", message.GroupOpenId, message.MsgId);
            }
        }
    }
}
