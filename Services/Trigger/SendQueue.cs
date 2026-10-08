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
/// 平台发送失败重试：按错误类型分类（SendResult.Retryable）——频控/参数类不重试，
/// 服务端临时故障（5xx）与网络异常做有限次指数退避重试（1.5s/3s/6s，上限 3 次）。
/// 试聊（仿真）模式：该群消息只推送到 WebUI 并落库回看，不发送到平台。
/// 平台无关：经 BotSenderRouter 按实例所属平台分发（QQ 官方 / OneBot11）。
/// </summary>
public class SendQueue : BackgroundService
{
    private readonly Channel<SendTask> _channel;
    private readonly BotSenderRouter _senderRouter;
    private readonly BotSendStats _sendStats;
    private readonly RuntimeConfig _config;
    private readonly GroupStateManager _groupStates;
    private readonly ILogger<SendQueue> _logger;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _groupSendWindow = new();
    private readonly BotStatus _botStatus;
    private readonly WebUiBridge? _webUi;

    /// <summary>发送消息序号（防服务器按 msg_seq 去重）</summary>
    private long _msgSeq = 0;

    /// <summary>本地频控超限的最大重试次数</summary>
    private const int MaxRetryTimes = 10;

    /// <summary>平台发送失败的最大重试次数（有限退避，防风暴）</summary>
    internal const int MaxSendRetries = 3;

    /// <summary>退避基数（毫秒）：1.5s → 3s → 6s</summary>
    internal const int RetryBackoffBaseMs = 1500;

    public SendQueue(BotSenderRouter senderRouter, BotSendStats sendStats, RuntimeConfig config, GroupStateManager groupStates, ILogger<SendQueue> logger, BotStatus botStatus, WebUiBridge? webUi = null)
    {
        _senderRouter = senderRouter;
        _sendStats = sendStats;
        _config = config;
        _groupStates = groupStates;
        _logger = logger;
        _botStatus = botStatus;
        _webUi = webUi;
        _channel = Channel.CreateUnbounded<SendTask>();
    }

    /// <summary>单条发送任务的处理结果（供 ExecuteAsync 决定重试策略）</summary>
    private enum SendOutcome
    {
        /// <summary>已发送（或试聊模式已推送 WebUI）</summary>
        Sent,

        /// <summary>本地 qpm 频控超限，延迟后重入队</summary>
        RateLimited,

        /// <summary>平台发送失败且可重试（临时故障），退避后重入队</summary>
        RetryableFailure,

        /// <summary>失败且不可重试（频控/参数类）或放弃</summary>
        Failed
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
                SendOutcome outcome = await TrySendAsync(task, stoppingToken);
                switch (outcome)
                {
                    case SendOutcome.Sent:
                        // 记录机器人发言时间：供暖群"最后一条消息是自己则不暖群"判定
                        // （含试聊仿真，保证仿真模式下暖群判定行为一致）
                        _groupStates.MarkBotSpeak(task.GroupOpenId, DateTimeOffset.UtcNow);
                        continue;
                    case SendOutcome.RateLimited:
                        // 本地 qpm 频控：延迟重试，超过上限丢弃
                        if (task.RetryTimes >= MaxRetryTimes)
                        {
                            _logger.LogError("消息发送频控重试超限已丢弃（group={Group}）", task.GroupOpenId);
                            continue;
                        }
                        task.RetryTimes++;
                        await Task.Delay(1500, stoppingToken);
                        await _channel.Writer.WriteAsync(task, stoppingToken);
                        break;
                    case SendOutcome.RetryableFailure:
                        // 平台临时故障：指数退避重试（1.5s/3s/6s），超过上限丢弃
                        if (task.SendRetryTimes >= MaxSendRetries)
                        {
                            _logger.LogError("消息发送重试 {Times} 次后仍失败，已丢弃（group={Group} error={Error}）",
                                MaxSendRetries, task.GroupOpenId, task.LastError);
                            continue;
                        }
                        task.SendRetryTimes++;
                        int backoffMs = RetryBackoffBaseMs * (1 << (task.SendRetryTimes - 1));
                        _logger.LogWarning("发送失败将退避 {BackoffMs}ms 后重试（第 {Times}/{Max} 次，group={Group} error={Error}）",
                            backoffMs, task.SendRetryTimes, MaxSendRetries, task.GroupOpenId, task.LastError);
                        await Task.Delay(backoffMs, stoppingToken);
                        await _channel.Writer.WriteAsync(task, stoppingToken);
                        break;
                    case SendOutcome.Failed:
                    default:
                        // 频控/参数类等不可重试失败：只计数，不重试
                        _logger.LogWarning("发送失败且不可重试，已丢弃（group={Group} error={Error}）", task.GroupOpenId, task.LastError);
                        break;
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

    private async Task<SendOutcome> TrySendAsync(SendTask task, CancellationToken ct)
    {
        int maxQpm = _config.Config.Safety.MaxQpmPerGroup;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<DateTimeOffset> window = _groupSendWindow.GetOrAdd(task.GroupOpenId, _ => []);
        lock (window)
        {
            window.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (window.Count >= maxQpm)
            {
                return SendOutcome.RateLimited;
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
            return SendOutcome.Sent;
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
        SendResult result;
        try
        {
            result = await _senderRouter.SendAsync(request);
        }
        catch (Exception ex)
        {
            // 兜底：任何未捕获异常按可重试失败统计（有限次数），不让异常打断队列循环
            result = SendResult.Fail(ex.Message, retryable: true);
            _logger.LogWarning(ex, "发送异常：实例 {BotId} → 会话 {Conversation}", botId, task.GroupOpenId);
        }
        task.LastError = result.Error;
        await _sendStats.RecordAsync(botId, result.Success, result.Success ? null : result.Error);
        _botStatus.ReceivedMessages++; // 复用计数仅作统计占位，不参与限流
        if (_webUi != null)
        {
            await _webUi.PublishBotMessageAsync(task.GroupOpenId, task.Content);
        }
        if (result.Success)
        {
            return SendOutcome.Sent;
        }
        return result.Retryable ? SendOutcome.RetryableFailure : SendOutcome.Failed;
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

    /// <summary>平台发送失败后的已重试次数（指数退避上限 MaxSendRetries）</summary>
    public int SendRetryTimes { get; set; }

    /// <summary>最近一次发送失败的错误信息（日志与丢弃告警用）</summary>
    public string? LastError { get; set; }
}
