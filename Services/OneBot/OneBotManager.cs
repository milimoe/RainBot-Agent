using System.Collections.Concurrent;
using System.Text.Json;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.QQ;

namespace RainBot.Services.OneBot;

/// <summary>
/// OneBot11 实例管理器：维护每个实例的传输通道（HTTP / WS 正向 / WS 反向），
/// 把各通道收到的事件统一转成 IncomingMessage 投递进核心管道，并按优先级选路发送 API。
/// 通道优先级：WS 会话（正向/反向任一带连接即优先）→ HTTP API。
/// </summary>
public class OneBotManager(
    BotInstanceStore store,
    MessageQueue queue,
    RuntimeConfig config,
    IHttpClientFactory httpClientFactory,
    ILogger<OneBotManager> logger) : IAsyncDisposable
{
    private readonly BotInstanceStore _store = store;
    private readonly MessageQueue _queue = queue;
    private readonly RuntimeConfig _config = config;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<OneBotManager> _logger = logger;
    private readonly ConcurrentDictionary<string, InstanceRuntime> _runtimes = new();

    /// <summary>各实例的消息 ID 去重（同一条消息可能被多条通道重复推送）</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDedupe> _dedupe = new();

    private InstanceRuntime Runtime(string botId) => _runtimes.GetOrAdd(botId, _ => new InstanceRuntime());

    /// <summary>注册一个 WS 会话（正向连接成功或反向连入时调用）</summary>
    public void RegisterSession(string botId, OneBotWsSession session)
    {
        InstanceRuntime runtime = Runtime(botId);
        runtime.Sessions[session] = 0;
        session.EventReceived += json => HandleEventAsync(botId, json);
        session.Closed += closed =>
        {
            runtime.Sessions.TryRemove(closed, out _);
            _logger.LogInformation("OneBot 实例 {BotId} 的 {Name} 会话已断开", botId, closed.Name);
        };
        _logger.LogInformation("OneBot 实例 {BotId} 已建立 {Name} 会话", botId, session.Name);
    }

    /// <summary>取实例可用的 HTTP 传输（按当前配置惰性创建，配置变更后重建）</summary>
    public IOneBotTransport? GetHttpTransport(string botId)
    {
        BotInstance? instance = _store.Get(botId);
        if (instance == null || instance.Platform != BotPlatform.OneBot11 || !instance.OneBot.Http.Enabled)
        {
            return null;
        }
        string apiUrl = instance.OneBot.Http.ApiUrl;
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return null;
        }
        InstanceRuntime runtime = Runtime(botId);
        lock (runtime)
        {
            string key = $"{apiUrl}|{instance.OneBot.Http.Token}";
            if (runtime.HttpTransport is { } existing && existing.Key == key)
            {
                return existing.Transport;
            }
            OneBotHttpTransport transport = new(apiUrl, instance.OneBot.Http.Token, _httpClientFactory, _logger);
            runtime.HttpTransport = (key, transport);
            return transport;
        }
    }

    /// <summary>按优先级选一条可用通道</summary>
    private IOneBotTransport? PickTransport(string botId)
    {
        InstanceRuntime runtime = Runtime(botId);
        IOneBotTransport? ws = runtime.Sessions.Keys.FirstOrDefault(s => s.IsConnected);
        return ws ?? GetHttpTransport(botId);
    }

    /// <summary>调用 OneBot API（如 send_group_msg）</summary>
    public async Task<OneBotApiResponse?> SendApiAsync(string botId, string action, object? parameters, CancellationToken ct = default)
    {
        IOneBotTransport? transport = PickTransport(botId);
        if (transport == null)
        {
            _logger.LogWarning("OneBot 实例 {BotId} 无可用通道，调用 {Action} 失败", botId, action);
            return null;
        }
        return await transport.SendApiAsync(action, parameters, ct);
    }

    /// <summary>
    /// 处理一条原始事件 JSON（HTTP 上报端点与 WS 会话共用）。
    /// </summary>
    public async Task HandleEventAsync(string botId, string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }
        OneBotEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OneBotEvent>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "OneBot 事件解析失败（{BotId}）", botId);
            return;
        }
        if (evt == null)
        {
            return;
        }

        BotInstance? instance = _store.Get(botId);
        if (instance == null || !instance.Enabled)
        {
            _logger.LogDebug("OneBot 实例 {BotId} 不存在或已禁用，忽略事件", botId);
            return;
        }

        // 自动学习机器人自身 QQ 号
        if (evt.SelfId != 0)
        {
            await _store.LearnSelfIdentityAsync(botId, evt.SelfId.ToString());
        }

        // 群聊与私聊都进管道；心跳/生命周期等 meta 事件忽略
        if (evt.PostType != "message")
        {
            return;
        }
        bool isPrivate = string.Equals(evt.MessageType, "private", StringComparison.OrdinalIgnoreCase);
        bool isGroup = string.Equals(evt.MessageType, "group", StringComparison.OrdinalIgnoreCase);
        if (!isGroup && !isPrivate)
        {
            return;
        }
        if (evt.UserId is not { } userId)
        {
            return;
        }

        // 会话键：群聊 = {实例Id}:{群号}；私聊 = {实例Id}:p{用户号}（p 标记防止与群号撞键）
        string conversationKey;
        if (isPrivate)
        {
            conversationKey = BotKeys.Private(botId, userId.ToString());
        }
        else
        {
            if (evt.GroupId is not { } gid)
            {
                return;
            }
            conversationKey = BotKeys.Group(botId, gid.ToString());
        }

        // 忽略机器人自己发出的消息
        if (evt.SelfId != 0 && userId == evt.SelfId)
        {
            return;
        }

        // 去重键区分群聊/私聊（部分实现里两者的 message_id 序列会重叠）
        string msgId = (isPrivate ? "p" : "g") + evt.MessageId;
        ConcurrentDedupe dedupe = _dedupe.GetOrAdd(botId, _ => new ConcurrentDedupe(TimeSpan.FromSeconds(_config.Config.Safety.DedupeWindowSeconds)));
        if (dedupe.IsDuplicate(msgId))
        {
            _logger.LogDebug("OneBot 事件重复已忽略（{BotId} msg={MsgId}）", botId, msgId);
            return;
        }
        dedupe.Mark(msgId);

        string selfQq = string.IsNullOrWhiteSpace(instance.OneBot.SelfQq) ? evt.SelfId.ToString() : instance.OneBot.SelfQq;
        bool isAt = OneBotMessage.IsAt(evt.Message, selfQq);
        List<string> imageUrls = OneBotMessage.ExtractImageUrls(evt.Message);
        string content = OneBotMessage.ExtractText(evt.Message);
        // 纯图片消息保持空文本（走 [图片] 占位 + 多模态识图）；其余无文本消息退回原始 CQ 码文本
        if (content.Length == 0 && imageUrls.Count == 0)
        {
            content = evt.RawMessage ?? "";
        }
        string senderId = userId.ToString();
        string? username = string.IsNullOrWhiteSpace(evt.Sender?.Card) ? evt.Sender?.Nickname : evt.Sender?.Card;

        IncomingMessage message = new()
        {
            BotId = botId,
            MsgId = msgId,
            GroupOpenId = conversationKey,
            SenderOpenId = senderId,
            Username = username,
            Content = content,
            ImageUrls = imageUrls,
            // 引用（回复）消息：OneBot 用 reply 消息段带被引用消息 id，
            // MsgIdx 存本实现的消息 id（与 reply 段同命名空间），供本地历史按引用回溯
            MsgIdx = evt.MessageId.ToString(),
            RefMsgIdx = OneBotMessage.GetReplyId(evt.Message) ?? "",
            // 私聊里每一条都是对机器人说的，直接视为触发
            IsAtRobot = isPrivate || isAt,
            IsAdmin = await _store.IsAdminAsync(botId, senderId),
            // OneBot 只有单次推送（没有官方那种 @/全量双事件），按全量消息语义处理
            IsFullMessage = true,
            IsPrivate = isPrivate,
            SkipSideEffects = false,
            ReceivedAt = evt.Time > 0
                ? DateTimeOffset.FromUnixTimeSeconds(evt.Time)
                : DateTimeOffset.UtcNow
        };
        await _queue.EnqueueAsync(message);
    }

    /// <summary>各 OneBot 实例的连接状态（WebUI / /health 透出）</summary>
    public IReadOnlyList<BotInstanceStatus> GetStatuses()
    {
        List<BotInstanceStatus> result = [];
        foreach (BotInstance instance in _store.All.Where(i => i.Platform == BotPlatform.OneBot11))
        {
            InstanceRuntime runtime = Runtime(instance.Id);
            List<string> transports = [];
            transports.AddRange(runtime.Sessions.Keys.Where(s => s.IsConnected).Select(s => s.Name));
            if (GetHttpTransport(instance.Id) is { IsConnected: true })
            {
                transports.Add("http");
            }
            result.Add(new BotInstanceStatus
            {
                Id = instance.Id,
                Name = instance.Name,
                Platform = instance.Platform.ToString(),
                Enabled = instance.Enabled,
                Connected = transports.Count > 0,
                ActiveTransports = transports
            });
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (InstanceRuntime runtime in _runtimes.Values)
        {
            foreach (OneBotWsSession session in runtime.Sessions.Keys)
            {
                await session.DisposeAsync();
            }
            runtime.Sessions.Clear();
        }
        _runtimes.Clear();
    }

    private sealed class InstanceRuntime
    {
        public ConcurrentDictionary<OneBotWsSession, byte> Sessions { get; } = new();

        /// <summary>HTTP 传输按 (apiUrl|token) 缓存，配置变更后惰性重建</summary>
        public (string Key, OneBotHttpTransport Transport)? HttpTransport { get; set; }
    }
}
