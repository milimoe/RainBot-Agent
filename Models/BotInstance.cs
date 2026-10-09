namespace RainBot.Models;

/// <summary>机器人实例所属平台（决定 ID 命名空间与协议）</summary>
public enum BotPlatform
{
    /// <summary>QQ 官方机器人：群/用户使用 openid，官方 WebSocket 网关</summary>
    QqOfficial,

    /// <summary>OneBot11（go-cqhttp / NapCat / Lagrange 等）：群/用户使用 QQ 号</summary>
    OneBot11
}

/// <summary>
/// 一个机器人实例：有自己的身份、监听通道与独立的数据命名空间（群键 = {实例Id}:{原始群号}）。
/// 同一实例可同时挂多条传输通道（如 OneBot 用 WS 反向收事件 + HTTP API 发消息）。
/// </summary>
public class BotInstance
{
    /// <summary>实例 Id（稳定、唯一；同时作为存储前缀，如 qq-main / ob-napcat）</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名</summary>
    public string Name { get; set; } = "";

    /// <summary>平台类型</summary>
    public BotPlatform Platform { get; set; } = BotPlatform.QqOfficial;

    /// <summary>是否启用（禁用后不启动监听、不发送）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>人设标识名，留空使用默认模板；优先于旧路径配置。</summary>
    public string PersonaName { get; set; } = "";

    /// <summary>旧版独立人设路径，保留兼容；选择库中人设后清空。</summary>
    public string PersonaPath { get; set; } = "";

    /// <summary>
    /// 本实例的管理员列表（可用全部 /admin 指令）。
    /// ID 命名空间随平台：QQ 官方 = 群内 openid（同一 QQ 用户在不同机器人下 openid 不同），
    /// OneBot11 = QQ 号。旧版全局 AdminOpenIds 仍作为兜底兼容。
    /// </summary>
    public List<string> Admins { get; set; } = [];

    /// <summary>QQ 官方平台配置</summary>
    public QqOfficialConfig Qq { get; set; } = new();

    /// <summary>OneBot11 平台配置</summary>
    public OneBotConfig OneBot { get; set; } = new();
}

/// <summary>QQ 官方实例配置</summary>
public class QqOfficialConfig
{
    /// <summary>开放平台 AppId</summary>
    public string AppId { get; set; } = "";

    /// <summary>开放平台 AppSecret</summary>
    public string Secret { get; set; } = "";

    /// <summary>是否沙箱环境</summary>
    public bool UseSandbox { get; set; }

    /// <summary>机器人在群内的 openid：首次被 @ 时从事件自动学习并落库，也可手填</summary>
    public string SelfOpenId { get; set; } = "";
}

/// <summary>OneBot11 实例配置（四条通道按优先级选路：WS 优先，HTTP 兜底）</summary>
public class OneBotConfig
{
    /// <summary>机器人自身 QQ 号：收到事件时由 self_id 自动学习，配置项作兜底与 @ 判定预置</summary>
    public string SelfQq { get; set; } = "";

    /// <summary>HTTP 通道：上报（本服务侧接收端点）与 API（调用 OneBot 实现发消息）</summary>
    public OneBotHttpConfig Http { get; set; } = new();

    /// <summary>WS 正向：本服务主动连接 OneBot 实现的 WS 服务</summary>
    public OneBotWsConfig WsForward { get; set; } = new();

    /// <summary>WS 反向：OneBot 实现主动连入本服务</summary>
    public OneBotWsConfig WsReverse { get; set; } = new();
}

/// <summary>OneBot11 HTTP 通道配置</summary>
public class OneBotHttpConfig
{
    /// <summary>是否启用该通道</summary>
    public bool Enabled { get; set; }

    /// <summary>OneBot 实现的 HTTP API 基址（如 http://127.0.0.1:3000）</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>访问令牌（Authorization: Bearer {Token}，留空不鉴权）</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// HTTP 上报端点（OneBot 实现把事件 POST 到该地址）。
    /// 由系统按约定生成：{公网基址}/onebot/v11/event/{实例Id}，管理页直接展示可复制的完整 URL。
    /// </summary>
    public string ReportPath { get; set; } = OneBotRoutes.DefaultReportPath;
}

/// <summary>OneBot11 WebSocket 通道配置（正向用 Url，反向用 Path）</summary>
public class OneBotWsConfig
{
    /// <summary>是否启用该通道</summary>
    public bool Enabled { get; set; }

    /// <summary>正向 WS：OneBot 实现的 WS 地址（如 ws://127.0.0.1:3001）</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 反向 WS 的完整地址（OneBot 实现主动连入）。
    /// 由系统按约定生成：ws://{公网域名}/onebot/v11/ws/{实例Id}，管理页展示可复制的完整 URL。
    /// </summary>
    public string Path { get; set; } = OneBotRoutes.DefaultReverseWsPath;

    /// <summary>访问令牌（留空不校验）</summary>
    public string Token { get; set; } = "";
}

/// <summary>OneBot11 在本服务侧的固定路由约定（实例 Id 走路由参数，支持 WebUI 热增删）</summary>
public static class OneBotRoutes
{
    public const string DefaultReportPath = "/onebot/v11/event";
    public const string DefaultReverseWsPath = "/onebot/v11/ws";

    /// <summary>HTTP 上报端点：/onebot/v11/event/{botId}</summary>
    public static string Report(string botId) => $"{DefaultReportPath}/{botId}";

    /// <summary>反向 WS 端点：/onebot/v11/ws/{botId}</summary>
    public static string ReverseWs(string botId) => $"{DefaultReverseWsPath}/{botId}";
}

/// <summary>机器人实例连接状态（WebUI 管理页与 /health 透出）</summary>
public class BotInstanceStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public bool Enabled { get; init; }

    /// <summary>是否已连上（QQ 官方 = WS 网关已连；OneBot = 至少一条通道可用）</summary>
    public bool Connected { get; init; }

    /// <summary>通道明细，如 "ws" / "http" / "ws-reverse"</summary>
    public List<string> ActiveTransports { get; init; } = [];

    /// <summary>备注/错误信息</summary>
    public string? Error { get; init; }
}
