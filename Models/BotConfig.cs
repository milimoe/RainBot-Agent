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

    /// <summary>是否使用沙箱环境（自 2026-08-10 起官方统一接口域名，沙箱仅靠凭据/管理端配置区分）</summary>
    public bool UseSandbox { get; set; } = false;

    /// <summary>接口调用统一域名（官方自 2026-08-10 起统一为 api.bot.qq.com，不再区分沙箱/正式域名）</summary>
    public const string ApiBaseUrl = "https://api.bot.qq.com";

    /// <summary>网关地址（统一域名 + /gateway 拉取 wss 地址）</summary>
    public string GatewayHost => ApiBaseUrl;

    /// <summary>HTTP API 地址（发送消息、上传媒体、菜单面板等全部统一域名）</summary>
    public string ApiHost => ApiBaseUrl;
}
