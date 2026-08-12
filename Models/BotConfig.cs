namespace RainBot.Models;

/// <summary>
/// QQ 机器人接入配置（appsettings "Bot" 段）
/// </summary>
public class BotConfig
{
    /// <summary>开放平台 AppID</summary>
    public string AppId { get; set; } = "";

    /// <summary>开放平台 AppSecret</summary>
    public string Secret { get; set; } = "";

    /// <summary>是否使用沙箱环境</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>WebSocket 网关地址（沙箱/正式）</summary>
    public string GatewayHost => UseSandbox ? "https://sandbox.api.sgroup.qq.com" : "https://api.sgroup.qq.com";

    /// <summary>HTTP API 地址（沙箱/正式，发送消息、上传媒体）</summary>
    public string ApiHost => UseSandbox ? "https://sandbox.api.sgroup.qq.com" : "https://api.sgroup.qq.com";
}
