using RainBot.Models;
using RainBot.Services.Commands;
using RainBot.Services.Context;
using RainBot.Services.Fun;
using RainBot.Services.Profile;
using RainBot.Services.Safety;
using RainBot.Services.Trigger;
using RainBot.Services.WebUi;
using RainBot.Services.Workflow;

namespace RainBot.Services.QQ;

/// <summary>
/// 入站消息处理（消息队列消费者）：
/// 统计 → 输入风控 → 历史入库 → 命令处理（无需 @，管理员指令按权限放行）→ 随机互动 → 被动触发判定 → 工作流。
/// </summary>
public class MessageProcessor
{
    private readonly GroupStateManager _states;
    private readonly InputFilter _inputFilter;
    private readonly HistoryStore _historyStore;
    private readonly CommandParser _commandParser;
    private readonly PassiveTrigger _passiveTrigger;
    private readonly ProfileRecaller _profileRecaller;
    private readonly WorkflowRunner _workflowRunner;
    private readonly OutputFilter _outputFilter;
    private readonly SendQueue _sendQueue;
    private readonly FunService _fun;
    private readonly ILogger<MessageProcessor> _logger;
    private readonly WebUiBridge? _webUi;

    public MessageProcessor(
        GroupStateManager states,
        InputFilter inputFilter,
        HistoryStore historyStore,
        CommandParser commandParser,
        PassiveTrigger passiveTrigger,
        ProfileRecaller profileRecaller,
        WorkflowRunner workflowRunner,
        OutputFilter outputFilter,
        SendQueue sendQueue,
        FunService funService,
        ILogger<MessageProcessor> logger,
        WebUiBridge? webUi = null)
    {
        _states = states;
        _inputFilter = inputFilter;
        _historyStore = historyStore;
        _commandParser = commandParser;
        _passiveTrigger = passiveTrigger;
        _profileRecaller = profileRecaller;
        _workflowRunner = workflowRunner;
        _outputFilter = outputFilter;
        _sendQueue = sendQueue;
        _fun = funService;
        _logger = logger;
        _webUi = webUi;
    }

    public async Task ProcessAsync(IncomingMessage message, CancellationToken ct)
    {
        DateTimeOffset now = message.ReceivedAt;

        // 0. 推送 WebUI（实时聊天页）
        _webUi?.PublishMemberMessage(message);

        // 调试：处理入口（排查"收到消息却不回复"问题）
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("处理消息：group={Group} msg={MsgId} sender={Sender} isAt={IsAt} isAdmin={IsAdmin} skipSideEffects={Skip} content={Content}",
                message.GroupOpenId, message.MsgId, message.SenderOpenId, message.IsAtRobot, message.IsAdmin, message.SkipSideEffects, message.Content);
        }

        // 1. 群状态统计（休眠计数 / 静默计时 / 管理员发言记录）。
        //    SkipSideEffects（@ 事件晚于同 msg_id 全量事件到达）时跳过：全量事件已统计，避免重复计数。
        if (!message.SkipSideEffects)
        {
            await _states.OnMessageAsync(message);
        }

        // 2. 输入风控：@ 消息命中敏感内容 → 不回应（仅标记观察）
        if (message.IsAtRobot && _inputFilter.Check(message.Content) != null)
        {
            _logger.LogInformation("群 {Group} 收到敏感 @ 消息，不回应（已标记观察）", message.GroupOpenId);
            return;
        }

        // 3. 历史入库（所有消息都记录，供上下文与话题分析）。
        //    SkipSideEffects 时跳过：全量事件已入库，避免同一消息在上下文里出现两次。
        if (!message.SkipSideEffects)
        {
            await _historyStore.AppendAsync(message.GroupOpenId, message);
        }

        // 4. 指令处理（无需 @，群里直接发送指令即可；@ 发送同样有效，管理员指令按权限放行）
        {
            ParsedCommand? command = _commandParser.Parse(message.Content);
            if (command != null)
            {
                // @ 发送的指令视为一次被动触发（进入冷却），直接发送的指令不占用被动冷却
                if (message.IsAtRobot)
                {
                    _passiveTrigger.MarkTriggered(message.GroupOpenId, now);
                }
                string? reply = await _commandParser.ExecuteAsync(command, message.GroupOpenId, message.SenderOpenId, message.IsAdmin);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("指令命中：{Kind}（group={Group} msg={MsgId} isAt={IsAt}），回复：{Reply}",
                        command.Kind, message.GroupOpenId, message.MsgId, message.IsAtRobot, reply ?? "(无)");
                }
                if (reply != null)
                {
                    // 指令回复不触发 LLM，不受 LLM 输出限制（行数/字符截断）影响，仅做安全过滤
                    string? filtered = _outputFilter.Filter(reply, applyLlmLimits: false);
                    if (filtered != null)
                    {
                        await _sendQueue.EnqueueAsync(new SendTask
                        {
                            BotId = message.BotId,
                            GroupOpenId = message.GroupOpenId,
                            Content = filtered,
                            IsPrivate = message.IsPrivate,
                            // 被动回复的 msg_id 仅对 @ 事件消息有效（官方约束），全量模式消息直接主动发送
                            MsgId = message.IsFullMessage ? null : message.MsgId
                        });
                    }
                }
                return;
            }
            if (message.IsAtRobot && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("@ 消息未匹配任何指令：{Content}（group={Group} msg={MsgId}）", message.Content, message.GroupOpenId, message.MsgId);
            }
        }

        // 5. 随机互动（原版 RainBOT 娱乐功能：反驳/复读/OSM/反向艾特/叫哥，纯规则不耗 Token）
        // 私聊不玩随机互动：反向艾特/叫哥/复读在 1:1 场景下很怪，且会打断正常对话
        if (!message.IsPrivate)
        {
            FunResult fun = await _fun.TryRespondAsync(message);
            if (fun.Blocked)
            {
                return;
            }
        }

        // 6. 被动触发判定（@ + 冷却）
        if (!_passiveTrigger.ShouldTrigger(message, now))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("被动触发未通过（冷却中或非 @）：group={Group} msg={MsgId} isAt={IsAt}", message.GroupOpenId, message.MsgId, message.IsAtRobot);
            }
            return;
        }
        _passiveTrigger.MarkTriggered(message.GroupOpenId, now);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("被动触发通过，进入工作流：group={Group} msg={MsgId}", message.GroupOpenId, message.MsgId);
        }

        // 7. 构建触发上下文（L2 画像召回进 Block F，下轮丢弃）
        string recalled = message.IsAtRobot && !string.IsNullOrEmpty(message.SenderOpenId)
            ? await _profileRecaller.RecallAsync(message.GroupOpenId, message.SenderOpenId)
            : "";
                TriggerContext ctx = new()
                {
                    BotId = message.BotId,
                    GroupOpenId = message.GroupOpenId,
                    IsPrivate = message.IsPrivate,
            Type = TriggerType.Passive,
            Reason = message.IsPrivate ? "私聊互动" : message.IsAtRobot ? "被群友 @ 互动" : "群友互动",
            SenderOpenId = message.SenderOpenId,
            RecalledProfile = recalled,
            AllowProfileUpdate = false
        };

        // 8. 执行工作流（@ 事件消息引用原消息回复；全量模式消息按官方约束主动发送）
        await _workflowRunner.RunAsync(ctx, message.IsFullMessage ? null : message.MsgId, ct);
    }
}
