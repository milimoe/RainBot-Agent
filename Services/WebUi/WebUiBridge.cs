using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RainBot.Models;
using RainBot.Services.Storage;
using RainBot.Services.Profile;

namespace RainBot.Services.WebUi;

/// <summary>
/// WebUI 与机器人核心链路之间的桥：
/// - 试聊（仿真）模式登记：开启后该群的机器人回复只推给网页、不发送到 QQ；
/// - 把群消息 / 机器人回复 / 连接状态发布到实时事件总线；
/// - 机器人回复落库（messages 表 user_openid = BotMarker），供聊天页回看。
/// </summary>
public class WebUiBridge(WebUiEventBus bus, Database db, UserIdentityResolver identities, ILogger<WebUiBridge> logger)
{
    /// <summary>数据库 messages 表中标记"机器人回复"的 user_openid 哨兵值</summary>
    public const string BotMarker = "$bot";

    /// <summary>内置试聊群的群 ID（网页里仿真对话使用的虚拟群）</summary>
    public const string SimGroupId = "webui-sim";

    private readonly WebUiEventBus _bus = bus;
    private readonly Database _db = db;
    private readonly ILogger<WebUiBridge> _logger = logger;

    /// <summary>开启试聊拦截的真实群（虚拟试聊群始终开启）</summary>
    private readonly ConcurrentDictionary<string, byte> _simulationGroups = new();

    /// <summary>该群的发送是否被 WebUI 拦截（内置试聊群恒为 true）</summary>
    public bool IsSimulationEnabled(string groupOpenId)
        => groupOpenId == SimGroupId || _simulationGroups.ContainsKey(groupOpenId);

    /// <summary>开启/关闭某真实群的试聊拦截</summary>
    public void SetSimulation(string groupOpenId, bool enabled)
    {
        if (groupOpenId == SimGroupId)
        {
            return; // 虚拟试聊群恒开启
        }
        if (enabled)
        {
            _simulationGroups[groupOpenId] = 1;
        }
        else
        {
            _simulationGroups.TryRemove(groupOpenId, out _);
        }
        _logger.LogInformation("群 {Group} 试聊拦截已{State}", groupOpenId, enabled ? "开启（机器人回复仅推送到 WebUI）" : "关闭");
        Publish("simulation", new JsonObject
        {
            ["group"] = groupOpenId,
            ["enabled"] = enabled
        });
    }

    /// <summary>当前开启试聊拦截的真实群列表</summary>
    public IReadOnlyList<string> SimulationGroups() => _simulationGroups.Keys.ToList();

    /// <summary>当前 SSE 订阅者数量（状态页展示）</summary>
    public int SubscriberCount => _bus.SubscriberCount;

    /// <summary>群友消息进入处理链时推送（MessageProcessor 调用）</summary>
    public async Task PublishMemberMessageAsync(IncomingMessage message)
    {
        Dictionary<string, string> names = await identities.GetDisplayNamesAsync(message.GroupOpenId,
            [message.SenderOpenId], [message.DisplayContent]);
        string? nickname = !string.IsNullOrWhiteSpace(message.Username) ? message.Username.Trim() : names.GetValueOrDefault(message.SenderOpenId);
        if (nickname != null) names[message.SenderOpenId] = nickname;
        Publish("message", new JsonObject
        {
            ["msgId"] = message.MsgId,
            ["group"] = message.GroupOpenId,
            ["sender"] = message.SenderOpenId,
            ["username"] = nickname,
            ["shortId"] = UserIdentityResolver.ShortId(message.SenderOpenId),
            ["names"] = System.Text.Json.JsonSerializer.SerializeToNode(names),
            ["content"] = message.DisplayContent,
            ["isAt"] = message.IsAtRobot,
            ["isAdmin"] = message.IsAdmin,
            ["time"] = message.ReceivedAt,
            ["simulated"] = IsSimulationEnabled(message.GroupOpenId)
        });
    }

    /// <summary>机器人回复：落库（可回看）+ 推送网页</summary>
    public async Task PublishBotMessageAsync(string groupOpenId, string content)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.InsertBotMessageAsync(groupOpenId, content, now);
        Publish("bot_message", new JsonObject
        {
            ["group"] = groupOpenId,
            ["sender"] = BotMarker,
            ["content"] = content,
            ["time"] = now,
            ["simulated"] = IsSimulationEnabled(groupOpenId)
        });
    }

    /// <summary>QQ 网关连接状态推送</summary>
    public void PublishStatus(bool wsConnected, DateTimeOffset? lastConnectedAt, long receivedMessages)
    {
        Publish("status", new JsonObject
        {
            ["wsConnected"] = wsConnected,
            ["lastConnectedAt"] = lastConnectedAt,
            ["receivedMessages"] = receivedMessages
        });
    }

    private void Publish(string type, JsonObject? data)
    {
        try
        {
            _bus.Publish(type, data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebUI 事件推送失败（忽略）");
        }
    }
}
