namespace RainBot.Services.WebUi;

/// <summary>
/// WebUI 控制台配置（appsettings "Rain:WebUi" 段）。
/// </summary>
public class WebUiOptions
{
    /// <summary>是否启用 WebUI 控制台（默认 true）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 访问令牌。为空时控制台无需鉴权（仅建议内网使用）；
    /// 非空时所有 /api/webui 接口需要请求头 X-WebUi-Token（SSE 可用 ?token= 查询参数）。
    /// </summary>
    public string Token { get; set; } = "";
}
