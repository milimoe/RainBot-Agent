using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Bots;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// QQ 官方「自定义菜单 + 指令面板」服务测试：
/// 用 FakeHttpClientFactory 模拟官方 API（含 Access Token 端点），
/// 验证请求 URL / 请求体序列化 / 响应解析 / 错误透传。
/// </summary>
public class QqMenuPanelTests
{
    private static readonly QqOfficialConfig Credentials = new() { AppId = "test-appid", Secret = "test-secret" };

    /// <summary>组装服务 + 记录请求的应答器</summary>
    private static (QqMenuPanelService Svc, List<(string Method, string Url, string Body)> Requests) Build(
        Func<HttpRequestMessage, HttpResponseMessage> apiResponder)
    {
        var requests = new List<(string, string, string)>();
        HttpResponseMessage Responder(HttpRequestMessage req)
        {
            string body = req.Content == null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((req.Method.Method.ToUpperInvariant(), req.RequestUri!.ToString(), body));

            if (req.RequestUri!.AbsolutePath.EndsWith("/app/getAppAccessToken"))
            {
                return Json(HttpStatusCode.OK, """{"access_token":"tok_123","expires_in":"7200"}""");
            }
            return apiResponder(req);
        }

        ServiceProvider sp = TestHost.Build(Responder).Provider;
        return (sp.GetRequiredService<QqMenuPanelService>(), requests);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK)
        => new(status);

    // ---------- 自定义菜单 ----------

    [Fact]
    public async Task 查询全局自定义菜单_解析响应()
    {
        (QqMenuPanelService svc, _) = Build(req => Json(HttpStatusCode.OK, """
            {"menu":{"items":[
              {"type":"send_message","name":"帮助","send_message":"/help"},
              {"type":"link","name":"官网","link":"https://example.com"},
              {"type":"menu","name":"更多","sub_menu_items":[{"type":"send_message","name":"设置","send_message":"/settings"}]}
            ]},"version":3}
            """));

        QqApiResult<MenuResponse> r = await svc.GetMenuAsync(Credentials);

        Assert.True(r.Ok, r.Error ?? "no error");
        Assert.NotNull(r.Data);
        Assert.Equal(3, r.Data!.Version);
        Assert.Equal(3, r.Data.Menu!.Items!.Count);
        Assert.Equal("帮助", r.Data.Menu.Items[0].Name);
        Assert.Equal("send_message", r.Data.Menu.Items[0].Type);
        Assert.Equal("/help", r.Data.Menu.Items[0].SendMessage);
        Assert.Equal("https://example.com", r.Data.Menu.Items[1].Link);
        Assert.Single(r.Data.Menu.Items[2].SubMenuItems!);
        Assert.Equal("/settings", r.Data.Menu.Items[2].SubMenuItems![0].SendMessage);
    }

    [Fact]
    public async Task 修改全局自定义菜单_请求URL与序列化正确()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(
            req => Json(HttpStatusCode.OK, """{"version":2}"""));

        var menu = new MenuDefinition
        {
            Items =
            [
                new MenuButton { Type = "switch", Name = "开关", Switch = new SwitchConfig { SwitchId = "search", Default = true } },
                new MenuButton { Type = "link", Name = "官网", Link = "https://example.com" }
            ]
        };
        QqApiResult<VersionResponse> r = await svc.UpdateMenuAsync(menu, Credentials);

        Assert.True(r.Ok, r.Error ?? "no error");
        Assert.Equal(2, r.Data!.Version);

        (string method, string url, string body) = requests.First(x => x.Url.Contains("/v2/menu"));
        Assert.Equal("PUT", method);
        Assert.EndsWith("/v2/menu", url);
        Assert.Contains("\"menu\"", body);
        Assert.Contains("\"switch_id\":\"search\"", body);    // snake_case 序列化（与官方一致）
        Assert.Contains("\"link\":\"https://example.com\"", body);
        Assert.DoesNotContain("\"sub_menu_items\":null", body); // null 字段不序列化
        Assert.DoesNotContain("\"send_message\":null", body);
    }

    // ---------- 指令面板 ----------

    [Fact]
    public async Task 查询面板列表_携带scope参数()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(
            req => Json(HttpStatusCode.OK, """
                {"records":[
                  {"panel_id":"p_001","scope":"c2c","target_type":"all",
                   "panel":{"items":[{"type":"command","name":"查询天气","desc":"查询当前天气"}],"remark":"C2C面板"},
                   "version":1}
                ],"next_cursor":"","is_end":true}
                """));

        QqApiResult<PanelListResponse> r = await svc.ListPanelsAsync("c2c", limit: 10, credentials: Credentials);

        Assert.True(r.Ok, r.Error ?? "no error");
        Assert.Single(r.Data!.Records!);
        Assert.Equal("p_001", r.Data.Records[0].PanelId);
        Assert.Equal("查询天气", r.Data.Records[0].Panel!.Items![0].Name);
        Assert.Equal("C2C面板", r.Data.Records[0].Panel.Remark);
        Assert.True(r.Data.IsEnd);

        (string method, string url, _) = requests.First(x => x.Url.Contains("/v2/panels"));
        Assert.Equal("GET", method);
        Assert.Contains("scope=c2c", url);
        Assert.Contains("limit=10", url);
    }

    [Fact]
    public async Task 创建指定群面板_请求体含目标与元素()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(
            req => Json(HttpStatusCode.OK, """{"panel_id":"p_new_001"}"""));

        var request = new CreatePanelRequest
        {
            Scope = "group",
            TargetType = "specific",
            GroupOpenIds = ["openid_group_001"],
            Panel = new PanelDefinition
            {
                Remark = "群签到面板",
                Items = [new PanelItem { Type = "command", Name = "群签到", Desc = "每日签到", OnlyAdmin = true }]
            }
        };
        QqApiResult<CreatePanelResponse> r = await svc.CreatePanelAsync(request, Credentials);

        Assert.True(r.Ok, r.Error ?? "no error");
        Assert.Equal("p_new_001", r.Data!.PanelId);

        (string method, string url, string body) = requests.First(x => x.Url.Contains("/v2/panels"));
        Assert.Equal("POST", method);
        Assert.EndsWith("/v2/panels", url);
        Assert.Contains("\"scope\":\"group\"", body);
        Assert.Contains("\"target_type\":\"specific\"", body);
        Assert.Contains("\"group_openids\":[\"openid_group_001\"]", body);
        Assert.Contains("\"only_admin\":true", body);
    }

    [Fact]
    public async Task 修改面板与修改关联对象_路径正确()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.EndsWith("/target"))
            {
                return Empty();
            }
            return Json(HttpStatusCode.OK, """{"version":4}""");
        });

        QqApiResult<VersionResponse> update = await svc.UpdatePanelAsync("p_001",
            new PanelDefinition { Items = [new PanelItem { Type = "command", Name = "新指令" }], Remark = "更新备注" }, Credentials);
        Assert.True(update.Ok);
        Assert.Equal(4, update.Data!.Version);

        QqApiResult<bool> targets = await svc.UpdatePanelTargetsAsync("p_001",
            new UpdatePanelTargetsRequest { Op = "add", GroupOpenIds = ["openid_group_003"] }, Credentials);
        Assert.True(targets.Ok);
        Assert.True(targets.Data);

        (string m1, string u1, string b1) = requests.First(x => x.Method == "PUT" && x.Url.Contains("/v2/panels/p_001") && !x.Url.EndsWith("/target"));
        Assert.Contains("\"panel\"", b1);
        (string m2, string u2, string b2) = requests.First(x => x.Url.EndsWith("/v2/panels/p_001/target"));
        Assert.Equal("PUT", m2);
        Assert.Contains("\"op\":\"add\"", b2);
        Assert.Contains("\"group_openids\":[\"openid_group_003\"]", b2);
        _ = (m1, u1);
    }

    [Fact]
    public async Task 查询面板详情_解析关联对象列表()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(
            req => Json(HttpStatusCode.OK, """
                {"panel_id":"p_001","scope":"group","target_type":"specific",
                 "panel":{"items":[{"type":"command","name":"群签到","desc":"每日签到"}],"remark":"群面板"},
                 "version":1,"user_openids":[],"group_openids":["openid_group_001","openid_group_002"]}
                """));

        QqApiResult<PanelRecord> r = await svc.GetPanelAsync("p_001", Credentials);

        Assert.True(r.Ok);
        Assert.Equal("p_001", r.Data!.PanelId);
        Assert.Equal("group", r.Data.Scope);
        Assert.Equal("specific", r.Data.TargetType);
        Assert.Single(r.Data.Panel!.Items!);
        Assert.Equal(2, r.Data.GroupOpenIds!.Count);
        Assert.Equal("openid_group_002", r.Data.GroupOpenIds[1]);

        (string method, string url, _) = requests.First(x => x.Url.Contains("/v2/panels/p_001"));
        Assert.Equal("GET", method);
        Assert.EndsWith("/v2/panels/p_001", url);
    }

    [Fact]
    public async Task 删除面板_空响应体返回成功()
    {
        (QqMenuPanelService svc, _) = Build(req => Empty());

        QqApiResult<bool> r = await svc.DeletePanelAsync("p_001", Credentials);

        Assert.True(r.Ok, r.Error ?? "no error");
        Assert.True(r.Data);
    }

    [Fact]
    public async Task 官方错误透传_状态码与错误体()
    {
        (QqMenuPanelService svc, _) = Build(req => Json(HttpStatusCode.BadRequest, """{"code":40030020,"message":"内容存在安全风险，请修改后重试"}"""));

        var menu = new MenuDefinition { Items = [new MenuButton { Type = "link", Name = "不良", Link = "https://bad.example" }] };
        QqApiResult<VersionResponse> r = await svc.UpdateMenuAsync(menu, Credentials);

        Assert.False(r.Ok);
        Assert.Equal(400, r.StatusCode);
        Assert.Contains("40030020", r.Error);
    }

    [Fact]
    public async Task 沙箱实例_使用沙箱Host()
    {
        (QqMenuPanelService svc, List<(string Method, string Url, string Body)> requests) = Build(
            req => Json(HttpStatusCode.OK, """{"menu":null}"""));

        var sandboxCreds = new QqOfficialConfig { AppId = "sb-appid", Secret = "sb-secret", UseSandbox = true };
        await svc.GetMenuAsync(sandboxCreds);

        (_, string url, _) = requests.First(x => x.Url.Contains("/v2/menu"));
        Assert.StartsWith("https://sandbox.api.sgroup.qq.com", url);
    }
}
