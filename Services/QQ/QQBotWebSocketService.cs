using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RainBot.Models;

namespace RainBot.Services.QQ;

/// <summary>
/// QQ 官方 WebSocket 网关连接（复用参考项目状态机：Op 2/6/1/10/11/7/9、
/// 心跳 ACK 检测、session 断线续传、5 秒重连；事件分发改为 MessageDispatcher）
/// </summary>
public class QQBotWebSocketService(
    ILogger<QQBotWebSocketService> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<BotConfig> botConfig,
    IServiceProvider serviceProvider,
    BotStatus botStatus) : BackgroundService
{
    private readonly ILogger<QQBotWebSocketService> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly BotConfig _botConfig = botConfig.Value;
    private readonly BotStatus _botStatus = botStatus;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private string? _sessionId;
    private int _lastSeq;
    private int _heartbeatIntervalMs;
    private Timer? _heartbeatTimer;
    /// <summary>是否收到上次心跳的 ACK</summary>
    private bool _isHeartbeatAckReceived = true;
    /// <summary>连续未收到 ACK 的次数</summary>
    private int _missedHeartbeatCount;
    /// <summary>最大容忍连续无 ACK 次数</summary>
    private const int MaxMissedHeartbeats = 3;
    private readonly Lock _heartbeatLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 主重连循环
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
                _botStatus.WebSocketConnected = false;
                _logger.LogError(ex, "WebSocket 连接异常崩溃，5秒后重连...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        StopHeartbeatTimer();

        string gatewayUrl = await GetGatewayUrlAsync(stoppingToken);
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("获取网关地址: {GatewayUrl}", gatewayUrl);

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(gatewayUrl), stoppingToken);
        _botStatus.WebSocketConnected = true;
        _botStatus.LastConnectedAt = DateTimeOffset.UtcNow;
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("WebSocket 已连接");

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
                        _logger.LogWarning("服务端主动关闭连接，准备重连");
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
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error)) _logger.LogError(ex, "WebSocket 接收消息异常");
        }
        finally
        {
            _botStatus.WebSocketConnected = false;
            StopHeartbeatTimer();
        }
    }

    private async Task ProcessMessageAsync(string message, CancellationToken stoppingToken)
    {
        try
        {
            Payload? payload = JsonSerializer.Deserialize<Payload>(message);
            if (payload == null) return;

            if (payload.SequenceNumber > 0)
                _lastSeq = payload.SequenceNumber;

            switch (payload.Op)
            {
                case 10: // Hello
                    {
                        WebSocketHelloData? hello = JsonSerializer.Deserialize<WebSocketHelloData>(payload.Data.ToString() ?? "");
                        if (hello != null)
                        {
                            _heartbeatIntervalMs = hello.HeartbeatInterval;
                            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("收到 Hello，心跳间隔: {Interval}ms", _heartbeatIntervalMs);
                            StartHeartbeatTimer();
                            if (!string.IsNullOrEmpty(_sessionId))
                                await SendResumeAsync(stoppingToken);
                            else
                                await SendIdentifyAsync(stoppingToken);
                        }
                        break;
                    }

                case 0: // Dispatch
                    {
                        if (payload.Data is JsonElement dataElement)
                        {
                            await HandleDispatchAsync(payload.EventType, dataElement);
                        }
                        break;
                    }

                case 11: // Heartbeat ACK
                    {
                        lock (_heartbeatLock)
                        {
                            _isHeartbeatAckReceived = true;
                            _missedHeartbeatCount = 0;
                        }
                        _logger.LogTrace("收到心跳 ACK");
                        break;
                    }

                case 7: // Reconnect
                    {
                        _logger.LogWarning("收到 Op 7 (Reconnect)，主动重连");
                        await ReconnectAsync();
                        break;
                    }

                case 9: // Invalid Session
                    {
                        _logger.LogWarning("收到 Op 9 (Invalid Session)，服务器将关闭连接");
                        _sessionId = null;
                        _lastSeq = 0;
                        break;
                    }

                default:
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("收到未处理的 Op: {Op}", payload.Op);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "解析消息失败: {Msg}", message);
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
                    _logger.LogWarning("心跳超时，未收到 ACK 次数: {Count}", _missedHeartbeatCount);
                    if (_missedHeartbeatCount >= MaxMissedHeartbeats)
                    {
                        _logger.LogError("连续 {Max} 次心跳无响应，主动重连", MaxMissedHeartbeats);
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
        Payload payload = new() { Op = 1, Data = _lastSeq };
        await SendJsonAsync(payload);
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
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("已发送 Identify 鉴权");
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
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("已发送 Resume，session={SessionId}, seq={Seq}", _sessionId, _lastSeq);
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
            _logger.LogError(ex, "发送 WebSocket 消息失败");
        }
    }

    private async Task ReconnectAsync()
    {
        _logger.LogWarning("执行主动重连");
        StopHeartbeatTimer();
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            _webSocket?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭 WebSocket 时出错");
        }
        finally
        {
            _webSocket = null;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        // 外层循环自动重连
    }

    private async Task<string> GetGatewayUrlAsync(CancellationToken ct)
    {
        using HttpClient? httpClient = _httpClientFactory.CreateClient();
        string accessToken = await GetAccessTokenAsync(false);
        httpClient.DefaultRequestHeaders.Add("Authorization", $"QQBot {accessToken}");
        HttpResponseMessage resp = await httpClient.GetAsync($"{_botConfig.GatewayHost}/gateway", ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("url").GetString() ?? throw new Exception("无法获取网关地址");
    }

    private async Task<string> GetAccessTokenAsync(bool useCache = true)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        QQBotService qqBotService = scope.ServiceProvider.GetRequiredService<QQBotService>();
        return await qqBotService.GetAccessTokenAsync(useCache);
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
                    if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("获取 Session ID: {SessionId}", _sessionId);
                }
                break;
            case "RESUMED":
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("会话恢复成功 (RESUMED)");
                break;
            case "C2C_MESSAGE_CREATE":
            case "GROUP_AT_MESSAGE_CREATE":
            case "GROUP_MESSAGE_CREATE":
                MessageDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<MessageDispatcher>();
                await dispatcher.HandleDispatchAsync(eventType, data);
                break;
            default:
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("未处理事件: {Event}", eventType);
                break;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeatTimer?.Dispose();
        _cts?.Cancel();
        if (_webSocket?.State == WebSocketState.Open)
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>机器人连接状态（/health 与运维使用）</summary>
public class BotStatus
{
    public bool WebSocketConnected { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public long ReceivedMessages { get; set; }
}
