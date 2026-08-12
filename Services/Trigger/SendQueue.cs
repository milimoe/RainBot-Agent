using System.Collections.Concurrent;
using System.Threading.Channels;
using RainBot.Services.Config;
using RainBot.Services.QQ;

namespace RainBot.Services.Trigger;

/// <summary>
/// 发送队列：Channel + 每群滑动窗口频控（默认 15 qpm，官方 20 qpm 留余量）。
/// 超限消息延迟重试，超过重试上限丢弃并告警，避免死循环。
/// </summary>
public class SendQueue : BackgroundService
{
    private readonly Channel<SendTask> _channel;
    private readonly QQBotService _qqBotService;
    private readonly RuntimeConfig _config;
    private readonly ILogger<SendQueue> _logger;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _groupSendWindow = new();
    private readonly BotStatus _botStatus;

    /// <summary>发送消息序号（防服务器按 msg_seq 去重）</summary>
    private long _msgSeq = 0;

    private const int MaxRetryTimes = 10;

    public SendQueue(QQBotService qqBotService, RuntimeConfig config, ILogger<SendQueue> logger, BotStatus botStatus)
    {
        _qqBotService = qqBotService;
        _config = config;
        _logger = logger;
        _botStatus = botStatus;
        _channel = Channel.CreateUnbounded<SendTask>();
    }

    /// <summary>入队发送任务</summary>
    public ValueTask EnqueueAsync(SendTask task) => _channel.Writer.WriteAsync(task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (SendTask task in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (await TrySendAsync(task, stoppingToken))
                {
                    continue;
                }
                // 频控超限：延迟重试
                if (task.RetryTimes < MaxRetryTimes)
                {
                    task.RetryTimes++;
                    await Task.Delay(1500, stoppingToken);
                    await _channel.Writer.WriteAsync(task, stoppingToken);
                }
                else
                {
                    _logger.LogError("消息发送重试超限已丢弃（group={Group}）", task.GroupOpenId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送队列处理异常（group={Group}）", task.GroupOpenId);
            }
        }
    }

    private async Task<bool> TrySendAsync(SendTask task, CancellationToken ct)
    {
        int maxQpm = _config.Config.Safety.MaxQpmPerGroup;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<DateTimeOffset> window = _groupSendWindow.GetOrAdd(task.GroupOpenId, _ => []);
        lock (window)
        {
            window.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (window.Count >= maxQpm)
            {
                return false;
            }
            window.Add(now);
        }
        await _qqBotService.SendGroupTextAsync(task.GroupOpenId, task.Content, task.MsgId, Interlocked.Increment(ref _msgSeq));
        _botStatus.ReceivedMessages++; // 复用计数仅作统计占位，不参与限流
        return true;
    }
}

/// <summary>发送任务</summary>
public class SendTask
{
    public required string GroupOpenId { get; init; }

    public required string Content { get; init; }

    /// <summary>被动回复时引用原消息 ID</summary>
    public string? MsgId { get; init; }

    public int RetryTimes { get; set; }
}
