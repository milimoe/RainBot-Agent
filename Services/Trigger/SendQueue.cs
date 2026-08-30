using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.QQ;
using RainBot.Services.WebUi;

namespace RainBot.Services.Trigger;

/// <summary>
/// 发送队列：Channel + 每群滑动窗口频控（默认 15 qpm，官方 20 qpm 留余量）。
/// 超限消息延迟重试，超过重试上限丢弃并告警，避免死循环。
/// 试聊（仿真）模式：该群消息只推送到 WebUI 并落库回看，不发送到平台。
/// 平台无关：经 BotSenderRouter 按实例所属平台分发（QQ 官方 / OneBot11）。
/// </summary>
public class SendQueue : BackgroundService
{
    private readonly Channel<SendTask> _channel;
    private readonly BotSenderRouter _senderRouter;
    private readonly BotSendStats _sendStats;
    private readonly RuntimeConfig _config;
    private readonly ILogger<SendQueue> _logger;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _groupSendWindow = new();
    private readonly BotStatus _botStatus;
    private readonly WebUiBridge? _webUi;

    /// <summary>发送消息序号（防服务器按 msg_seq 去重）</summary>
    private long _msgSeq = 0;

    private const int MaxRetryTimes = 10;

    public SendQueue(BotSenderRouter senderRouter, BotSendStats sendStats, RuntimeConfig config, ILogger<SendQueue> logger, BotStatus botStatus, WebUiBridge? webUi = null)
    {
        _senderRouter = senderRouter;
        _sendStats = sendStats;
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

        // 试聊（仿真）模式：不发送到平台，只推送到 WebUI 并落库供聊天页回看
        if (_webUi != null && _webUi.IsSimulationEnabled(task.GroupOpenId))
        {
            await _webUi.PublishBotMessageAsync(task.GroupOpenId, task.Content);
            return true;
        }

        // 平台无关发送：内部会话键 {实例Id}:{群号|p用户号} → 路由器按实例平台分发
        string botId = task.BotId.Length > 0 ? task.BotId : BotKeys.GetBotId(task.GroupOpenId);
        bool isPrivate = task.IsPrivate || BotKeys.IsPrivateKey(task.GroupOpenId);
        // GetRawPeerId 会去掉私聊键的 p 标记，得到对端原始 ID
        BotSendRequest request = new()
        {
            BotId = botId,
            RawGroupId = BotKeys.GetRawPeerId(task.GroupOpenId),
            Content = task.Content,
            Markdown = _config.Config.MarkdownReply,
            ReplyMsgId = task.MsgId,
            AtUserId = isPrivate ? null : task.AtUserId,
            ImageUrl = task.ImageUrl,
            IsPrivate = isPrivate,
            MsgSeq = Interlocked.Increment(ref _msgSeq)
        };
        bool ok;
        string? error = null;
        try
        {
            ok = await _senderRouter.SendAsync(request);
            if (!ok)
            {
                error = "平台返回失败";
            }
        }
        catch (Exception ex)
        {
            // 兜底：任何未捕获异常都按失败统计，不让异常打断队列循环
            ok = false;
            error = ex.Message;
            _logger.LogWarning(ex, "发送异常：实例 {BotId} → 会话 {Conversation}", botId, task.GroupOpenId);
        }
        await _sendStats.RecordAsync(botId, ok, error);
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
    /// <summary>目标机器人实例 Id（决定走哪个平台的发送器）</summary>
    public string BotId { get; init; } = "";

    /// <summary>内部会话键：群聊 {实例Id}:{原始群号}，私聊 {实例Id}:p{用户号}</summary>
    public required string GroupOpenId { get; init; }

    public required string Content { get; init; }

    /// <summary>被动回复时引用原消息 ID</summary>
    public string? MsgId { get; init; }

    /// <summary>
    /// 需要 @ 的用户原始 ID（平台无关）。由各平台发送器渲染为对应语义：
    /// QQ 官方 = 文本 &lt;@!{id}&gt;；OneBot11 = at 消息段。私聊中无意义。
    /// </summary>
    public string? AtUserId { get; init; }

    /// <summary>延迟发送秒数（0 = 立即）</summary>
    public int DelaySeconds { get; init; }

    /// <summary>图片地址（OSM 梗图等；非空则按平台方式发送图片而非文本）</summary>
    public string? ImageUrl { get; init; }

    /// <summary>是否私聊会话（私聊走 send_private_msg 等私聊接口）</summary>
    public bool IsPrivate { get; init; }

    public int RetryTimes { get; set; }
}
