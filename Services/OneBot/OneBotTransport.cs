using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RainBot.Services.OneBot;

/// <summary>OneBot11 传输通道：负责把 API 调用送到 OneBot 实现</summary>
public interface IOneBotTransport
{
    /// <summary>通道名（http / ws-forward / ws-reverse），用于状态展示</summary>
    string Name { get; }

    bool IsConnected { get; }

    Task<OneBotApiResponse?> SendApiAsync(string action, object? parameters, CancellationToken ct = default);
}

/// <summary>
/// HTTP 通道：调用 OneBot 实现的 HTTP API（POST {ApiUrl}/{action}）。
/// 仅用于发送；事件由 HTTP 上报端点（本服务侧）接收。
/// </summary>
public sealed class OneBotHttpTransport(
    string apiUrl,
    string? token,
    IHttpClientFactory httpClientFactory,
    ILogger logger) : IOneBotTransport
{
    private readonly string _baseUrl = (apiUrl ?? "").TrimEnd('/');
    private readonly string? _token = string.IsNullOrWhiteSpace(token) ? null : token;
    private readonly HttpClient _http = httpClientFactory.CreateClient();
    private readonly ILogger _logger = logger;

    public string Name => "http";
    public bool IsConnected => _baseUrl.Length > 0;

    public async Task<OneBotApiResponse?> SendApiAsync(string action, object? parameters, CancellationToken ct = default)
    {
        if (_baseUrl.Length == 0)
        {
            return null;
        }
        using HttpRequestMessage request = new(HttpMethod.Post, $"{_baseUrl}/{action}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { action, @params = parameters }), Encoding.UTF8, "application/json")
        };
        if (_token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException ex)
        {
            // 通道不可达/超时是常见故障（OneBot 实现没启动、地址填错）。
            // 这里吃掉异常按失败返回，交给上层统计，避免异常穿透打断发送队列。
            _logger.LogWarning("OneBot HTTP 通道不可达：{Action} → {Message}", action, ex.Message);
            return null;
        }
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OneBot HTTP API 调用失败：{Action} → {Status} {Body}", action, response.StatusCode, body);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<OneBotApiResponse>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OneBot HTTP 响应解析失败：{Action}", action);
            return null;
        }
    }
}

/// <summary>
/// WebSocket 会话（正向与反向共用）：收事件、发 API 请求并用 echo 关联响应。
/// 正向 = 本服务连接 OneBot；反向 = OneBot 连入本服务，协议完全一致，只是 socket 来源不同。
/// </summary>
public sealed class OneBotWsSession : IOneBotTransport, IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OneBotApiResponse?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    public OneBotWsSession(string name, WebSocket socket, ILogger logger)
    {
        Name = name;
        _socket = socket;
        _logger = logger;
    }

    public string Name { get; }

    public bool IsConnected => _socket.State == WebSocketState.Open;

    /// <summary>收到非 API 响应（即事件）时触发</summary>
    public event Func<string, Task>? EventReceived;

    /// <summary>会话断开时触发（管理器清理用）</summary>
    public event Action<OneBotWsSession>? Closed;

    /// <summary>持续读取循环（调用方应在后台运行）</summary>
    public async Task RunAsync(CancellationToken stoppingToken = default)
    {
        byte[] buffer = new byte[8192];
        StringBuilder sb = new();
        try
        {
            while (_socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string json = sb.ToString();
                if (json.Length == 0)
                {
                    continue;
                }
                await HandleFrameAsync(json);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "OneBot WebSocket 连接断开（{Name}）", Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneBot WebSocket 读取异常（{Name}）", Name);
        }
        finally
        {
            FailAllPending();
            Closed?.Invoke(this);
        }
    }

    private async Task HandleFrameAsync(string json)
    {
        string? echo = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("echo", out JsonElement echoElement)
                && echoElement.ValueKind == JsonValueKind.String)
            {
                echo = echoElement.GetString();
            }
        }
        catch (JsonException)
        {
            echo = null;
        }

        if (echo != null && _pending.TryRemove(echo, out TaskCompletionSource<OneBotApiResponse?>? tcs))
        {
            try
            {
                OneBotApiResponse? response = JsonSerializer.Deserialize<OneBotApiResponse>(json);
                tcs.TrySetResult(response);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "OneBot API 响应解析失败（echo={Echo}）", echo);
                tcs.TrySetResult(null);
            }
            return;
        }

        if (EventReceived != null)
        {
            await EventReceived(json);
        }
    }

    public async Task<OneBotApiResponse?> SendApiAsync(string action, object? parameters, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return null;
        }
        string echo = Guid.NewGuid().ToString("N");
        TaskCompletionSource<OneBotApiResponse?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = tcs;

        string payload = JsonSerializer.Serialize(new OneBotApiRequest { Action = action, Params = parameters, Echo = echo });
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);
            using CancellationTokenRegistration reg = timeout.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(echo, out _);
            return null;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(echo, out _);
            _logger.LogWarning(ex, "OneBot WS API 调用失败：{Action}", action);
            return null;
        }
    }

    private void FailAllPending()
    {
        foreach (string key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out TaskCompletionSource<OneBotApiResponse?>? tcs))
            {
                tcs.TrySetResult(null);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _sendLock.Dispose();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // 忽略关闭异常
            }
        }
        _socket.Dispose();
    }
}
