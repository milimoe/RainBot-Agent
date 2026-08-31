using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainBot.Models;
using RainBot.Services.QQ;

namespace RainBot.Services.Bots;

/// <summary>QQ 官方 API 调用结果（WebUI 层透传错误用）</summary>
public record QqApiResult<T>(bool Ok, int? StatusCode, string? Error, T? Data)
{
    public static QqApiResult<T> Success(T data) => new(true, null, null, data);
    public static QqApiResult<T> Fail(int? statusCode, string error) => new(false, statusCode, error, default);
}

/// <summary>
/// QQ 官方「自定义菜单 + 指令面板」配置服务（官方文档：
/// https://bot.q.qq.com/wiki/develop/api-v2/server-inter/menu-panel/）。
/// 多实例架构：凭据按实例传入（QqOfficialConfig），Access Token 复用 QQBotService 按 AppId 缓存。
/// 仅 QQ 官方平台（BotPlatform.QqOfficial）实例支持；OneBot 无对应能力。
/// </summary>
public class QqMenuPanelService(QQBotService qqBotService, IHttpClientFactory httpClientFactory, ILogger<QqMenuPanelService> logger)
{
    private readonly QQBotService _qqBotService = qqBotService;
    private readonly HttpClient _http = httpClientFactory.CreateClient();
    private readonly ILogger<QqMenuPanelService> _logger = logger;

    /// <summary>官方返回与请求均为 camelCase；null 字段不序列化（避免空字符串误伤校验）；中文直接输出便于排查</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>沙箱/正式 API Host（跟随实例 UseSandbox，与 BotConfig.ApiHost 一致）</summary>
    private static string ApiHost(QqOfficialConfig? credentials)
        => credentials?.UseSandbox == true ? "https://sandbox.api.sgroup.qq.com" : "https://api.sgroup.qq.com";

    // ---------- 全局自定义菜单 ----------

    /// <summary>查询全局自定义菜单（GET /v2/menu）</summary>
    public async Task<QqApiResult<MenuResponse>> GetMenuAsync(QqOfficialConfig? credentials = null)
        => await CallAsync<MenuResponse>(HttpMethod.Get, $"{ApiHost(credentials)}/v2/menu", null, credentials);

    /// <summary>修改全局自定义菜单（PUT /v2/menu，覆盖完整菜单配置；传空 items 即清空菜单）</summary>
    public async Task<QqApiResult<VersionResponse>> UpdateMenuAsync(MenuDefinition menu, QqOfficialConfig? credentials = null)
        => await CallAsync<VersionResponse>(HttpMethod.Put, $"{ApiHost(credentials)}/v2/menu", new { menu }, credentials);

    // ---------- 指令面板 ----------

    /// <summary>查询指令面板列表（GET /v2/panels?scope=...，必传 scope）</summary>
    public async Task<QqApiResult<PanelListResponse>> ListPanelsAsync(string scope, string? cursor = null, int? limit = null, QqOfficialConfig? credentials = null)
    {
        string url = $"{ApiHost(credentials)}/v2/panels?scope={Uri.EscapeDataString(scope)}";
        if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (limit.HasValue) url += $"&limit={limit.Value}";
        return await CallAsync<PanelListResponse>(HttpMethod.Get, url, null, credentials);
    }

    /// <summary>创建指令面板（POST /v2/panels）</summary>
    public async Task<QqApiResult<CreatePanelResponse>> CreatePanelAsync(CreatePanelRequest request, QqOfficialConfig? credentials = null)
        => await CallAsync<CreatePanelResponse>(HttpMethod.Post, $"{ApiHost(credentials)}/v2/panels", request, credentials);

    /// <summary>查询指令面板详情（GET /v2/panels/{panel_id}）</summary>
    public async Task<QqApiResult<PanelRecord>> GetPanelAsync(string panelId, QqOfficialConfig? credentials = null)
        => await CallAsync<PanelRecord>(HttpMethod.Get, $"{ApiHost(credentials)}/v2/panels/{Uri.EscapeDataString(panelId)}", null, credentials);

    /// <summary>修改指令面板内容（PUT /v2/panels/{panel_id}，覆盖元素与备注，不影响已关联对象）</summary>
    public async Task<QqApiResult<VersionResponse>> UpdatePanelAsync(string panelId, PanelDefinition panel, QqOfficialConfig? credentials = null)
        => await CallAsync<VersionResponse>(HttpMethod.Put, $"{ApiHost(credentials)}/v2/panels/{Uri.EscapeDataString(panelId)}", new { panel }, credentials);

    /// <summary>删除指令面板（DELETE /v2/panels/{panel_id}）</summary>
    public async Task<QqApiResult<bool>> DeletePanelAsync(string panelId, QqOfficialConfig? credentials = null)
        => await CallAsync<bool>(HttpMethod.Delete, $"{ApiHost(credentials)}/v2/panels/{Uri.EscapeDataString(panelId)}", null, credentials);

    /// <summary>修改指令面板关联对象（PUT /v2/panels/{panel_id}/target，op=add/del）</summary>
    public async Task<QqApiResult<bool>> UpdatePanelTargetsAsync(string panelId, UpdatePanelTargetsRequest request, QqOfficialConfig? credentials = null)
        => await CallAsync<bool>(HttpMethod.Put, $"{ApiHost(credentials)}/v2/panels/{Uri.EscapeDataString(panelId)}/target", request, credentials);

    // ---------- 通用请求 ----------

    private async Task<QqApiResult<T>> CallAsync<T>(HttpMethod method, string url, object? body, QqOfficialConfig? credentials)
    {
        string accessToken;
        try
        {
            accessToken = await _qqBotService.GetAccessTokenAsync(true, credentials);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "获取 Access Token 网络异常（菜单/面板接口）");
            return QqApiResult<T>.Fail(null, $"获取 Access Token 失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 Access Token 失败（菜单/面板接口，appId={AppId}）", credentials?.AppId);
            return QqApiResult<T>.Fail(null, $"获取 Access Token 失败：{ex.Message}");
        }

        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("QQBot", accessToken);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "菜单/面板接口请求网络异常：{Url}", url);
            return QqApiResult<T>.Fail(null, $"请求网络异常：{ex.Message}");
        }

        string responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError("菜单/面板接口失败：{Method} {Url} → {StatusCode} {Body}", method, url, (int)response.StatusCode, responseBody);
            }
            return QqApiResult<T>.Fail((int)response.StatusCode, ExtractError(responseBody));
        }
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            // DELETE / target 等接口成功时返回空体
            return typeof(T) == typeof(bool)
                ? QqApiResult<T>.Success((T)(object)true)
                : QqApiResult<T>.Success(default!);
        }
        try
        {
            T? data = JsonSerializer.Deserialize<T>(responseBody, JsonOpts);
            return QqApiResult<T>.Success(data ?? default!);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "菜单/面板接口响应解析失败：{Url}", url);
            return QqApiResult<T>.Fail(null, $"响应解析失败：{ex.Message}");
        }
    }

    /// <summary>官方错误体为 JSON（{code, message, ...}）时提取 message，非 JSON 原样返回</summary>
    private static string ExtractError(string responseBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                string? text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体（如网关 HTML），原样透传
        }
        return responseBody;
    }
}
