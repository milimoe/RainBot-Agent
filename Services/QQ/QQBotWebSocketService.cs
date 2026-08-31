using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.WebUi;

namespace RainBot.Services.QQ;

/// <summary>
/// QQ 官方网关连接（单实例）：状态机 Op 2/6/1/10/11/7/9、心跳 ACK 检测、
/// session 断线续传、5 秒重连；事件分发给 MessageDispatcher（带实例 Id）。
/// 凭据按实例从 BotInstanceStore 实时取，WebUI 改完立即生效。
/// </summary>
public class QqGatewayConnection(
    string botId,
    ILogger<QqGatewayConnection> logger,
    IHttpClientFactory httpClientFactory,
    IServiceProvider serviceProvider,
    BotInstanceStore store,
    BotConnectionRegistry registry,
    BotStatus botStatus,
    WebUiBridge? webUi = null)
{
    private readonly string _botId = botId;
    private readonly ILogger<QqGatewayConnection> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly BotInstanceStore _store = store;
    private readonly BotConnectionRegistry _registry = registry;
    private readonly BotStatus _botStatus = botStatus;
    private readonly WebUiBridge? _webUi = webUi;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string? _sessionId;
    private int _lastSeq;
    private int _heartbeatIntervalMs;
    private Timer? _heartbeatTimer;
    private bool _isHeartbeatAckReceived = true;
    private int _missedHeartbeatCount;
    private const int MaxMissedHeartbeats = 3;
    private readonly Lock _heartbeatLock = new();

    /// <summary>当前实例的 QQ 官方配置（实时取，WebUI 改完立即生效）</summary>
    private QqOfficialConfig Credentials => _store.Get(_botId)?.Qq ?? new QqOfficialConfig();

    /// <summary>网关地址（官方统一域名 api.bot.qq.com，沙箱不再有独立域名）</summary>
    private string GatewayHost => BotConfig.ApiBaseUrl;

    /// <summary>主重连循环</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetConnected(false);
                _logger.LogError(ex, "[{BotId}] WebSocket 连接异常崩溃，5秒后重连...", _botId);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        StopHeartbeatTimer();

        QqOfficialConfig credentials = Credentials;
        if (string.IsNullOrWhiteSpace(credentials.AppId) || string.IsNullOrWhiteSpace(credentials.Secret))
        {
            _logger.LogInformation("[{BotId}] QQ 官方凭据未配置，等待配置后连接", _botId);
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            return;
        }

        string gatewayUrl = await GetGatewayUrlAsync(stoppingToken);
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 获取网关地址: {GatewayUrl}", _botId, gatewayUrl);

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(gatewayUrl), stoppingToken);
        SetConnected(true);
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] WebSocket 已连接", _botId);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        byte[] buffer = new byte[4096];
        StringBuilder messageBuilder = new();
        try
        {
            while (!stoppingToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("[{BotId}] 服务端主动关闭连接，准备重连", _botId);
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string message = messageBuilder.ToString();
                messageBuilder.Clear();

                await ProcessMessageAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[{BotId}] WebSocket 连接已主动断开，准备重连", _botId);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error)) _logger.LogError(ex, "[{BotId}] WebSocket 接收消息异常", _botId);
        }
        finally
        {
            SetConnected(false);
            StopHeartbeatTimer();
        }
    }

    private void SetConnected(bool connected)
    {
        _registry.SetConnected(_botId, connected);
        // 兼容旧的单实例状态（/health 与 WebUI 状态页）
        _botStatus.WebSocketConnected = connected;
        if (connected)
        {
            _botStatus.LastConnectedAt = DateTimeOffset.UtcNow;
        }
        PublishStatus();
    }

    /// <summary>推送连接状态给 WebUI 实时事件流</summary>
    private void PublishStatus()
        => _webUi?.PublishStatus(_botStatus.WebSocketConnected, _botStatus.LastConnectedAt, _botStatus.ReceivedMessages);

    private async Task ProcessMessageAsync(string message, CancellationToken stoppingToken)
    {
        try
        {
            Payload? payload = JsonSerializer.Deserialize<Payload>(message);
            if (payload == null) return;

            if (payload.SequenceNumber > 0)
                _lastSeq = payload.SequenceNumber;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                string dataText = payload.Data?.ToString() ?? "";
                _logger.LogDebug("[{BotId}] WS 帧：op={Op} t={EventType} s={Seq} d={Data}",
                    _botId, payload.Op, payload.EventType, payload.SequenceNumber, dataText.Length <= 600 ? dataText : dataText[..600] + "…");
            }

            switch (payload.Op)
            {
                case 10: // Hello
                    {
                        WebSocketHelloData? hello = JsonSerializer.Deserialize<WebSocketHelloData>(payload.Data.ToString() ?? "");
                        if (hello != null)
                        {
                            _heartbeatIntervalMs = hello.HeartbeatInterval;
                            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 收到 Hello，心跳间隔: {Interval}ms", _botId, _heartbeatIntervalMs);
                            StartHeartbeatTimer();
                            if (!string.IsNullOrEmpty(_sessionId))
                                await SendResumeAsync(stoppingToken);
                            else
                                await SendIdentifyAsync(stoppingToken);
                        }
                        break;
                    }

                case 0: // Dispatch
                    if (payload.Data is JsonElement dataElement)
                    {
                        await HandleDispatchAsync(payload.EventType, dataElement);
                    }
                    break;

                case 11: // Heartbeat ACK
                    lock (_heartbeatLock)
                    {
                        _isHeartbeatAckReceived = true;
                        _missedHeartbeatCount = 0;
                    }
                    _logger.LogTrace("[{BotId}] 收到心跳 ACK", _botId);
                    break;

                case 7: // Reconnect
                    _logger.LogWarning("[{BotId}] 收到 Op 7 (Reconnect)，主动重连", _botId);
                    await ReconnectAsync();
                    break;

                case 9: // Invalid Session
                    _logger.LogWarning("[{BotId}] 收到 Op 9 (Invalid Session)，服务器将关闭连接", _botId);
                    _sessionId = null;
                    _lastSeq = 0;
                    break;

                default:
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("[{BotId}] 收到未处理的 Op: {Op}", _botId, payload.Op);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[{BotId}] 解析消息失败: {Msg}", _botId, message);
        }
    }

    private void StartHeartbeatTimer()
    {
        StopHeartbeatTimer();
        _heartbeatTimer = new Timer(async _ =>
        {
            lock (_heartbeatLock)
            {
                if (!_isHeartbeatAckReceived)
                {
                    _missedHeartbeatCount++;
                    _logger.LogWarning("[{BotId}] 心跳超时，未收到 ACK 次数: {Count}", _botId, _missedHeartbeatCount);
                    if (_missedHeartbeatCount >= MaxMissedHeartbeats)
                    {
                        _logger.LogError("[{BotId}] 连续 {Max} 次心跳无响应，主动重连", _botId, MaxMissedHeartbeats);
                        _ = ReconnectAsync();
                        return;
                    }
                }
                _isHeartbeatAckReceived = false;
            }
            await SendHeartbeatAsync();
        }, null, 0, _heartbeatIntervalMs);
    }

    private void StopHeartbeatTimer()
    {
        lock (_heartbeatLock)
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _isHeartbeatAckReceived = true;
            _missedHeartbeatCount = 0;
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        await SendJsonAsync(new Payload { Op = 1, Data = _lastSeq });
    }

    private async Task SendIdentifyAsync(CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        string accessToken = await GetAccessTokenAsync();
        WebSocketIdentifyData identify = new()
        {
            Token = $"QQBot {accessToken}",
            // 1<<30 公域频道消息 | 1<<25 群聊+C2C | 1<<12 按钮回调
            Intents = (1L << 30) | (1L << 25) | (1L << 12),
            Properties = new Dictionary<string, string>
            {
                ["$os"] = "linux",
                ["$browser"] = "rainbot_csharp",
                ["$device"] = "rainbot_csharp"
            }
        };
        await SendJsonAsync(new Payload { Op = 2, Data = identify });
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 已发送 Identify 鉴权", _botId);
    }

    private async Task SendResumeAsync(CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        string accessToken = await GetAccessTokenAsync();
        WebSocketResumeData resume = new()
        {
            Token = $"QQBot {accessToken}",
            SessionId = _sessionId ?? "",
            Seq = _lastSeq
        };
        await SendJsonAsync(new Payload { Op = 6, Data = resume });
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 已发送 Resume，session={SessionId}, seq={Seq}", _botId, _sessionId, _lastSeq);
    }

    private async Task SendJsonAsync(Payload payload)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{BotId}] 发送 WebSocket 消息失败", _botId);
        }
    }

    private async Task ReconnectAsync()
    {
        _logger.LogWarning("[{BotId}] 执行主动重连", _botId);
        StopHeartbeatTimer();
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            _webSocket?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{BotId}] 关闭 WebSocket 时出错", _botId);
        }
        finally
        {
            _webSocket = null;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Stop()
    {
        StopHeartbeatTimer();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _webSocket?.Dispose();
    }

    private async Task<string> GetGatewayUrlAsync(CancellationToken ct)
    {
        using HttpClient? httpClient = _httpClientFactory.CreateClient();
        string accessToken = await GetAccessTokenAsync(false);
        httpClient.DefaultRequestHeaders.Add("Authorization", $"QQBot {accessToken}");
        HttpResponseMessage resp = await httpClient.GetAsync($"{GatewayHost}/gateway", ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("url").GetString() ?? throw new Exception("无法获取网关地址");
    }

    private async Task<string> GetAccessTokenAsync(bool useCache = true)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        QQBotService qqBotService = scope.ServiceProvider.GetRequiredService<QQBotService>();
        return await qqBotService.GetAccessTokenAsync(useCache, Credentials);
    }

    private async Task HandleDispatchAsync(string eventType, JsonElement data)
    {
        using IServiceScope? scope = _serviceProvider.CreateScope();

        switch (eventType)
        {
            case "READY":
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("session_id", out JsonElement sid))
                {
                    _sessionId = sid.GetString();
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 获取 Session ID: {SessionId}", _botId, _sessionId);
                }
                break;
            case "RESUMED":
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("[{BotId}] 会话恢复成功 (RESUMED)", _botId);
                break;
            case "C2C_MESSAGE_CREATE":
            case "GROUP_AT_MESSAGE_CREATE":
            case "GROUP_MESSAGE_CREATE":
                MessageDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<MessageDispatcher>();
                await dispatcher.HandleDispatchAsync(_botId, eventType, data);
                break;
            default:
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("[{BotId}] 未处理事件: {Event}", _botId, eventType);
                break;
        }
    }
}

/// <summary>
/// QQ 官方网关管理：为每个启用的 QqOfficial 实例维护一条 WebSocket 连接；
/// 实例在 WebUI 增删改/启停后自动核对（新增即连、删除即断）。
/// </summary>
public class QqGatewayService(
    BotInstanceStore store,
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory,
    ILogger<QqGatewayService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, (QqGatewayConnection Connection, CancellationTokenSource Cts)> _running = [];
    private readonly BotInstanceStore _store = store;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<QqGatewayService> _logger = logger;
    private readonly Lock _lock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.InstancesChanged += Reconcile;
        Reconcile();
        using PeriodicTimer timer = new(ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Reconcile();
        }
    }

    /// <summary>按当前实例配置启动/停止各 QQ 官方连接</summary>
    private void Reconcile()
    {
        lock (_lock)
        {
            HashSet<string> wanted = [];
            foreach (BotInstance instance in _store.All)
            {
                if (instance.Platform != BotPlatform.QqOfficial || !instance.Enabled)
                {
                    continue;
                }
                wanted.Add(instance.Id);
                if (_running.ContainsKey(instance.Id))
                {
                    continue;
                }
                CancellationTokenSource cts = new();
                QqGatewayConnection connection = ActivatorUtilities.CreateInstance<QqGatewayConnection>(
                    _serviceProvider, instance.Id, _loggerFactory.CreateLogger<QqGatewayConnection>());
                _running[instance.Id] = (connection, cts);
                _ = Task.Run(() => connection.RunAsync(cts.Token), CancellationToken.None);
                _logger.LogInformation("QQ 官方实例 {BotId} 网关已启动", instance.Id);
            }

            foreach (string stale in _running.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                if (_running.Remove(stale, out (QqGatewayConnection Connection, CancellationTokenSource Cts) entry))
                {
                    entry.Cts.Cancel();
                    entry.Connection.Stop();
                    entry.Cts.Dispose();
                    _logger.LogInformation("QQ 官方实例 {BotId} 网关已停止", stale);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach ((QqGatewayConnection connection, CancellationTokenSource cts) in _running.Values)
            {
                cts.Cancel();
                connection.Stop();
                cts.Dispose();
            }
            _running.Clear();
        }
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>机器人连接状态（/health 与运维使用，兼容旧的单实例视图）</summary>
public class BotStatus
{
    public bool WebSocketConnected { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public long ReceivedMessages { get; set; }
}
