using RainBot.Models;
using RainBot.Services.Commands;
using RainBot.Services.Context;
using RainBot.Services.Profile;
using RainBot.Services.Safety;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;

namespace RainBot.Services.QQ;

/// <summary>
/// 入站消息处理（消息队列消费者）：
/// 统计 → 输入风控 → 历史入库 → 命令处理 → 被动触发判定 → 工作流。
/// </summary>
public class MessageProcessor(
    GroupStateManager states,
    InputFilter inputFilter,
    HistoryStore historyStore,
    CommandParser commandParser,
    PassiveTrigger passiveTrigger,
    ProfileRecaller profileRecaller,
    WorkflowRunner workflowRunner,
    OutputFilter outputFilter,
    SendQueue sendQueue,
    ILogger<MessageProcessor> logger)
{
    private readonly GroupStateManager _states = states;
    private readonly InputFilter _inputFilter = inputFilter;
    private readonly HistoryStore _historyStore = historyStore;
    private readonly CommandParser _commandParser = commandParser;
    private readonly PassiveTrigger _passiveTrigger = passiveTrigger;
    private readonly ProfileRecaller _profileRecaller = profileRecaller;
    private readonly WorkflowRunner _workflowRunner = workflowRunner;
    private readonly OutputFilter _outputFilter = outputFilter;
    private readonly SendQueue _sendQueue = sendQueue;
    private readonly ILogger<MessageProcessor> _logger = logger;

    public async Task ProcessAsync(IncomingMessage message, CancellationToken ct)
    {
        DateTimeOffset now = message.ReceivedAt;

        // 1. 群状态统计（休眠计数 / 静默计时 / 管理员发言记录）
        await _states.OnMessageAsync(message);

        // 2. 输入风控：@ 消息命中敏感内容 → 不回应（仅标记观察）
        if (message.IsAtRobot && _inputFilter.Check(message.Content) != null)
        {
            _logger.LogInformation("群 {Group} 收到敏感 @ 消息，不回应（已标记观察）", message.GroupOpenId);
            return;
        }

        // 3. 历史入库（所有消息都记录，供上下文与话题分析）
        await _historyStore.AppendAsync(message.GroupOpenId, message);

        // 4. 指令处理（@ 机器人时优先响应指令）
        if (message.IsAtRobot)
        {
            ParsedCommand? command = _commandParser.Parse(message.Content);
            if (command != null)
            {
                _passiveTrigger.MarkTriggered(message.GroupOpenId, now);
                string? reply = await _commandParser.ExecuteAsync(command, message.GroupOpenId, message.SenderOpenId, message.IsAdmin);
                if (reply != null)
                {
                    string? filtered = _outputFilter.Filter(reply);
                    if (filtered != null)
                    {
                        await _sendQueue.EnqueueAsync(new SendTask
                        {
                            GroupOpenId = message.GroupOpenId,
                            Content = filtered,
                            MsgId = message.MsgId
                        });
                    }
                }
                return;
            }
        }

        // 5. 被动触发判定（@ + 冷却）
        if (!_passiveTrigger.ShouldTrigger(message, now))
        {
            return;
        }
        _passiveTrigger.MarkTriggered(message.GroupOpenId, now);

        // 6. 构建触发上下文（L2 画像召回进 Block F，下轮丢弃）
        string recalled = message.IsAtRobot && !string.IsNullOrEmpty(message.SenderOpenId)
            ? await _profileRecaller.RecallAsync(message.GroupOpenId, message.SenderOpenId)
            : "";
        TriggerContext ctx = new()
        {
            GroupOpenId = message.GroupOpenId,
            Type = TriggerType.Passive,
            Reason = message.IsAtRobot ? "被群友 @ 互动" : "群友互动",
            SenderOpenId = message.SenderOpenId,
            RecalledProfile = recalled,
            AllowProfileUpdate = false
        };

        // 7. 执行工作流（引用原消息回复）
        await _workflowRunner.RunAsync(ctx, message.MsgId, ct);
    }
}
