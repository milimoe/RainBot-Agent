using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using RainBot.Models;
using RainBot.Services.Bots;

namespace RainBot.Services.OneBot;

/// <summary>
/// OneBot11 网关：托管「WS 正向」连接（本服务主动连 OneBot），
/// 并注册「HTTP 上报」与「WS 反向」两个本服务侧端点（供 OneBot 实现接入）。
/// 端点路径按约定：/onebot/v11/event/{实例Id} 与 /onebot/v11/ws/{实例Id}。
/// </summary>
public static class OneBotGateway
{
    /// <summary>注册本服务侧的两个接入端点</summary>
    public static void MapEndpoints(WebApplication app)
    {
        // HTTP 上报：OneBot 实现把事件 POST 到这里
        app.MapPost(OneBotRoutes.DefaultReportPath + "/{botId}", async (HttpContext context, string botId) =>
        {
            (bool ok, string body, string reason) = await ReadEventAsync(context, botId);
            if (!ok)
            {
                return Results.Json(new { status = "failed", retcode = 1403, message = reason }, statusCode: 403);
            }
            await context.RequestServices.GetRequiredService<OneBotManager>().HandleEventAsync(botId, body, context.RequestAborted);
            // 统一立即返回 ok；真正的回复走异步发送队列（否则会阻塞 OneBot 上报线程）
            return Results.Json(new { status = "ok", retcode = 0 });
        });

        // WS 反向：OneBot 实现主动连入
        app.MapGet(OneBotRoutes.DefaultReverseWsPath + "/{botId}", async (HttpContext context, string botId) =>
        {
            BotInstanceStore store = context.RequestServices.GetRequiredService<BotInstanceStore>();
            BotInstance? instance = store.Get(botId);
            if (instance == null || instance.Platform != BotPlatform.OneBot11)
            {
                return Results.Json(new { error = $"未知 OneBot 实例：{botId}" }, statusCode: 404);
            }
            if (!CheckToken(context, instance.OneBot.WsReverse.Token))
            {
                return Results.Json(new { error = "令牌校验失败" }, statusCode: 403);
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.Json(new { error = "仅支持 WebSocket 升级请求" }, statusCode: 400);
            }

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            ILogger<OneBotWsSession> logger = context.RequestServices.GetRequiredService<ILogger<OneBotWsSession>>();
            OneBotWsSession session = new("ws-reverse", socket, logger);
            context.RequestServices.GetRequiredService<OneBotManager>().RegisterSession(botId, session);
            await session.RunAsync(context.RequestAborted);
            return Results.Empty;
        });
    }

    private static async Task<(bool Ok, string Body, string Reason)> ReadEventAsync(HttpContext context, string botId)
    {
        BotInstanceStore store = context.RequestServices.GetRequiredService<BotInstanceStore>();
        BotInstance? instance = store.Get(botId);
        if (instance == null || instance.Platform != BotPlatform.OneBot11)
        {
            return (false, "", $"未知 OneBot 实例：{botId}");
        }
        if (!CheckToken(context, instance.OneBot.Http.Token))
        {
            return (false, "", "令牌校验失败");
        }
        using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(context.RequestAborted);
        return (true, body, "");
    }

    /// <summary>令牌校验：未配置令牌时不校验；配置了则要求 Authorization: Bearer 或 X-OneBot-Token</summary>
    private static bool CheckToken(HttpContext context, string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return true;
        }
        string? header = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(header))
        {
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                header = header["Bearer ".Length..];
            }
            if (header == configuredToken)
            {
                return true;
            }
        }
        string? alt = context.Request.Headers["X-OneBot-Token"].FirstOrDefault();
        return alt == configuredToken;
    }
}

/// <summary>
/// WS 正向连接管理：每个启用该通道的 OneBot 实例一条长连接，断开自动重连。
/// 实例增删改（WebUI 热管理）后由 InstancesChanged 事件触发重新核对。
/// </summary>
public class OneBotWsForwardService(
    BotInstanceStore store,
    OneBotManager manager,
    ILoggerFactory loggerFactory,
    ILogger<OneBotWsForwardService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ILogger<OneBotWsForwardService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.InstancesChanged += Reconcile;
        Reconcile();
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Reconcile();
        }
    }

    /// <summary>按当前实例配置启动/停止各正向连接</summary>
    private void Reconcile()
    {
        HashSet<string> wanted = [];
        foreach (BotInstance instance in store.All)
        {
            if (instance.Platform != BotPlatform.OneBot11
                || !instance.Enabled
                || !instance.OneBot.WsForward.Enabled
                || string.IsNullOrWhiteSpace(instance.OneBot.WsForward.Url))
            {
                continue;
            }
            wanted.Add(instance.Id);
            if (_running.ContainsKey(instance.Id))
            {
                continue;
            }
            CancellationTokenSource cts = new();
            _running[instance.Id] = cts;
            _ = Task.Run(() => RunLoopAsync(instance.Id, cts.Token), CancellationToken.None);
        }

        foreach (string stale in _running.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            if (_running.TryRemove(stale, out CancellationTokenSource? cts))
            {
                cts.Cancel();
                cts.Dispose();
                _logger.LogInformation("OneBot 实例 {BotId} 正向 WS 已停止（实例变更或禁用）", stale);
            }
        }
    }

    private async Task RunLoopAsync(string botId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            BotInstance? instance = store.Get(botId);
            string? url = instance?.OneBot.WsForward.Url;
            if (instance == null || !instance.Enabled || string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            try
            {
                using ClientWebSocket socket = new();
                if (!string.IsNullOrWhiteSpace(instance.OneBot.WsForward.Token))
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {instance.OneBot.WsForward.Token}");
                }
                await socket.ConnectAsync(new Uri(url), ct);
                _logger.LogInformation("OneBot 实例 {BotId} 正向 WS 已连接：{Url}", botId, url);
                OneBotWsSession session = new("ws-forward", socket, loggerFactory.CreateLogger<OneBotWsSession>());
                manager.RegisterSession(botId, session);
                await session.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OneBot 实例 {BotId} 正向 WS 连接失败，{Seconds}s 后重连", botId, ReconnectDelay.TotalSeconds);
            }
            try
            {
                await Task.Delay(ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
