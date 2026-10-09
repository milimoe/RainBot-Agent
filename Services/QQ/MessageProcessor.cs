using RainBot.Models;
using RainBot.Services.Commands;
using RainBot.Services.Config;
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
/// 统计 → 输入风控 → 历史入库 → 命令处理（无需 @，管理员指令按权限放行）→ 随机互动 → 被动触发判定（@ 即时响应 / 普通消息概率插嘴）→ 工作流。
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
    private readonly RuntimeConfig _config;
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
        RuntimeConfig config,
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
        _config = config;
        _logger = logger;
        _webUi = webUi;
    }

    public Task ProcessAsync(IncomingMessage message, CancellationToken ct)
        => ProcessBatchAsync([message], ct);

    /// <summary>同会话积压消息先逐条记录，再合并为一次工作流；私聊逐条处理。</summary>
    public async Task ProcessBatchAsync(IReadOnlyList<IncomingMessage> messages, CancellationToken ct)
    {
        List<IncomingMessage> eligible = [];
        foreach (IncomingMessage message in messages)
        {
            ct.ThrowIfCancellationRequested();
            bool fresh = IsFresh(message, DateTimeOffset.UtcNow);
            if (await ApplySideEffectsAsync(message, fresh))
            {
                eligible.Add(message);
            }
        }
        if (eligible.Count == 0) return;
        if (eligible[0].IsPrivate)
        {
            foreach (IncomingMessage message in eligible)
                await TriggerAsync([message], ct);
        }
        else
        {
            await TriggerAsync(eligible, ct);
        }
    }

    private bool IsFresh(IncomingMessage message, DateTimeOffset now)
    {
        int seconds = _config.Config.Trigger.BacklogMaxAgeSeconds;
        return seconds <= 0 || now - message.ReceivedAt <= TimeSpan.FromSeconds(seconds);
    }

    /// <summary>返回可参加本批触发判定的消息；过期消息仅记录统计和历史。</summary>
    private async Task<bool> ApplySideEffectsAsync(IncomingMessage message, bool fresh)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

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

        // 2. 输入风控：@ 消息命中敏感内容 → 不回应（仅标记观察）；
        //    普通消息命中敏感内容 → 不回应 @ 时同样不参与随机插嘴（避免拿敏感话头开涮）
        bool sensitive = _inputFilter.Check(message.Content) != null;
        // 3. 历史入库（所有消息都记录，供上下文与话题分析）。
        //    SkipSideEffects 时跳过：全量事件已入库，避免同一消息在上下文里出现两次。
        if (!message.SkipSideEffects)
        {
            await _historyStore.AppendAsync(message.GroupOpenId, message);
        }

        // 3.5 机器人自己的消息（全量模式回显，author.bot=true）：
        //     统计/历史已记录（机器人发言应进入上下文），但不再触发指令/随机互动/被动回复——机器人不回应自己。
        if (message.IsFromBot || sensitive || !fresh)
        {
            return false;
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
                string? reply = await _commandParser.ExecuteAsync(command, message.GroupOpenId, message.SenderOpenId, message.IsAdmin, message.BotId);
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
                return false;
            }
            if (message.IsAtRobot && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("@ 消息未匹配任何指令：{Content}（group={Group} msg={MsgId}）", message.Content, message.GroupOpenId, message.MsgId);
            }
        }

        // 5. 随机互动（原版 RainBOT 娱乐功能：反驳/复读/OSM/反向艾特/叫哥，纯规则不耗 Token）
        // 私聊不玩随机互动：反向艾特/叫哥/复读在 1:1 场景下很怪，且会打断正常对话
        if (!message.IsPrivate && !message.SkipSideEffects)
        {
            FunResult fun = await _fun.TryRespondAsync(message);
            if (fun.Blocked)
            {
                return false;
            }
        }

        return true;
    }

    private async Task TriggerAsync(IReadOnlyList<IncomingMessage> candidates, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // 副作用处理期间也可能耗时，再次检查时效；优先回应本批最后一条 @。
        List<IncomingMessage> fresh = candidates.Where(m => IsFresh(m, now)).ToList();
        if (fresh.Count == 0) return;
        IncomingMessage message = fresh.LastOrDefault(m => m.IsAtRobot) ?? fresh[^1];
        bool sensitive = false;
        // 6. 被动触发判定：@/回复 → 即时响应；普通消息 → 概率插嘴（Trigger.RandomChatProbability，0 = 关闭）
        bool atTriggered = _passiveTrigger.ShouldTrigger(message, now);
        bool randomChat = !atTriggered && _passiveTrigger.ShouldRandomChat(message, now, sensitive);
        if (!atTriggered && !randomChat)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("被动触发未通过（冷却中或非 @ 且未命中插嘴概率）：group={Group} msg={MsgId} isAt={IsAt}", message.GroupOpenId, message.MsgId, message.IsAtRobot);
            }
            return;
        }
        if (atTriggered)
        {
            _passiveTrigger.MarkTriggered(message.GroupOpenId, now);
        }
        else
        {
            _passiveTrigger.MarkRandomChat(message.GroupOpenId, now);
        }
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("被动触发通过（{Kind}），进入工作流：group={Group} msg={MsgId}", randomChat ? "随机插嘴" : "@", message.GroupOpenId, message.MsgId);
        }

        // 7. 构建触发上下文（L2 画像召回进 Block F，下轮丢弃；插嘴召回说话人画像用于个性化搭话）
        string recalled = !string.IsNullOrEmpty(message.SenderOpenId)
            ? await _profileRecaller.RecallAsync(message.GroupOpenId, message.SenderOpenId)
            : "";
        // 图片与引用：本条附件/引用自带优先，其次按 ref_msg_idx 本地回溯，最后按时间窗回溯本人最近的图
        (List<string> imageUrls, string quotedText) = ResolveContext(message, now);
        foreach (IncomingMessage other in fresh.Where(m => !ReferenceEquals(m, message)))
        {
            var (images, quote) = ResolveContext(other, now);
            imageUrls.AddRange(images);
            if (!string.IsNullOrWhiteSpace(quote))
                quotedText = string.IsNullOrWhiteSpace(quotedText) ? quote : quotedText + "\n" + quote;
        }
        imageUrls = imageUrls.Distinct(StringComparer.Ordinal).ToList();

        TriggerContext ctx = new()
        {
            BotId = message.BotId,
            GroupOpenId = message.GroupOpenId,
            CreatedAt = message.ReceivedAt,
            IsPrivate = message.IsPrivate,
            Type = randomChat ? TriggerType.RandomChat : TriggerType.Passive,
            Reason = randomChat
                ? "随机搭话（群友在群里说话但没 @ 你，群消息历史最后一条就是触发消息）"
                : message.IsPrivate ? "私聊互动" : "被群友 @ 互动",
            SenderOpenId = message.SenderOpenId,
            SenderNickname = message.Username,
            PendingMessages = fresh.Count > 1
                ? fresh.Select(m => new PendingSpeaker(m.SenderOpenId, m.Username, m.DisplayContent)).ToList()
                : [],
            RecalledProfile = recalled,
            ImageUrls = imageUrls,
            QuotedContent = quotedText,
            AllowProfileUpdate = false
        };

        // 8. 执行工作流（@ 事件消息引用原消息回复；全量模式消息按官方约束主动发送）
        if (fresh.Count > 1)
            _logger.LogInformation("会话 {Group} 合并 {Count} 条积压消息为一次回复", message.GroupOpenId, fresh.Count);
        await _workflowRunner.RunAsync(ctx, message.IsFullMessage ? null : message.MsgId, ct);
    }

    /// <summary>
    /// 解析本轮上下文里的图片与被引用内容：
    /// 1) 本条附件 / 引用消息随事件下发的图片（直接可用）；
    /// 2) 只有 ref_msg_idx 时按消息索引从本地历史回溯被引用的那条（正文 + 图片）；
    /// 3) 仍无图且开启视觉时，按时间窗回溯触发者本人最近一张图（「先发图、再 @ 分析」）。
    /// </summary>
    private (List<string> Images, string QuotedText) ResolveContext(IncomingMessage message, DateTimeOffset now)
    {
        List<string> images = [.. message.ImageUrls];
        string quoted = message.QuotedContent;

        // 引用消息只带索引（事件未下发引用内容）→ 本地历史按 msg_idx 回溯
        if (string.IsNullOrWhiteSpace(quoted) && !string.IsNullOrEmpty(message.RefMsgIdx))
        {
            HistoryEntry? entry = _historyStore.FindByMsgIdx(message.GroupOpenId, message.RefMsgIdx);
            if (entry != null)
            {
                quoted = entry.Content;
                images.AddRange(entry.ImageUrls);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("引用消息按 msg_idx 回溯到本地历史：{Content}", entry.Content);
                }
            }
        }

        // 仍无图 → 时间窗回溯本人最近一张图
        if (images.Count == 0)
        {
            int seconds = _config.Config.Trigger.ImageLookbackSeconds;
            if (seconds > 0 && _config.Config.Llm.EnableVision)
            {
                List<string> fromHistory = _historyStore.FindRecentImageUrls(
                    message.GroupOpenId, message.SenderOpenId, TimeSpan.FromSeconds(seconds), now);
                if (fromHistory.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("触发消息未带图，回溯到本人最近 {Count} 张图（{Seconds}s 窗口内）", fromHistory.Count, seconds);
                }
                images.AddRange(fromHistory);
            }
        }

        return ([.. images.Distinct(StringComparer.Ordinal)], quoted);
    }
}
