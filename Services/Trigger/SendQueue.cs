using System.Collections.Concurrent;
using System.Threading.Channels;
using RainBot.Services.Config;
using RainBot.Services.QQ;
using RainBot.Services.WebUi;

namespace RainBot.Services.Trigger;

/// <summary>
/// 发送队列：Channel + 每群滑动窗口频控（默认 15 qpm，官方 20 qpm 留余量）。
/// 超限消息延迟重试，超过重试上限丢弃并告警，避免死循环。
/// 试聊（仿真）模式：该群消息只推送到 WebUI 并落库回看，不发送到 QQ。
/// </summary>
public class SendQueue : BackgroundService
{
    private readonly Channel<SendTask> _channel;
    private readonly QQBotService _qqBotService;
    private readonly RuntimeConfig _config;
    private readonly ILogger<SendQueue> _logger;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _groupSendWindow = new();
    private readonly BotStatus _botStatus;
    private readonly WebUiBridge? _webUi;

    /// <summary>发送消息序号（防服务器按 msg_seq 去重）</summary>
    private long _msgSeq = 0;

    private const int MaxRetryTimes = 10;

    public SendQueue(QQBotService qqBotService, RuntimeConfig config, ILogger<SendQueue> logger, BotStatus botStatus, WebUiBridge? webUi = null)
    {
        _qqBotService = qqBotService;
        _config = config;
        _logger = logger;
        _botStatus = botStatus;
        _webUi = webUi;
        _channel = Channel.CreateUnbounded<SendTask>();
    }

    /// <summary>入队发送任务</summary>
    public ValueTask EnqueueAsync(SendTask task) => _channel.Writer.WriteAsync(task);

    /// <summary>窥探待发送任务（仅测试用）</summary>
    internal IReadOnlyList<SendTask> PeekPendingForTest()
    {
        List<SendTask> result = [];
        while (_channel.Reader.TryRead(out SendTask? task))
        {
            result.Add(task);
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (SendTask task in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // 延迟发送（复读/叫哥防"复读机"感知）
                if (task.DelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(task.DelaySeconds), stoppingToken);
                }
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

        // 调试：发送决策（排查"没有回复"问题）
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("发送队列：group={Group} sim={Sim} markdown={Md} msgId={MsgId} content={Content}",
                task.GroupOpenId, _webUi != null && _webUi.IsSimulationEnabled(task.GroupOpenId), _config.Config.MarkdownReply, task.MsgId, task.Content);
        }

        // 试聊（仿真）模式：不发送到 QQ，只推送到 WebUI 并落库供聊天页回看
        if (_webUi != null && _webUi.IsSimulationEnabled(task.GroupOpenId))
        {
            await _webUi.PublishBotMessageAsync(task.GroupOpenId, task.Content);
            return true;
        }

        // Markdown 回复模式：以 Markdown 消息（msg_type=2）发送，否则纯文本（msg_type=0）
        if (_config.Config.MarkdownReply)
        {
            await _qqBotService.SendGroupMarkdownAsync(task.GroupOpenId, task.Content, task.MsgId, Interlocked.Increment(ref _msgSeq));
        }
        else
        {
            await _qqBotService.SendGroupTextAsync(task.GroupOpenId, task.Content, task.MsgId, Interlocked.Increment(ref _msgSeq));
        }
        _botStatus.ReceivedMessages++; // 复用计数仅作统计占位，不参与限流
        if (_webUi != null)
        {
            await _webUi.PublishBotMessageAsync(task.GroupOpenId, task.Content);
        }
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

    /// <summary>延迟发送秒数（0 = 立即）</summary>
    public int DelaySeconds { get; init; }

    public int RetryTimes { get; set; }
}
