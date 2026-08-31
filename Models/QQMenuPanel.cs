using System.Text.Json.Serialization;

namespace RainBot.Models;

// ===================================================================
// QQ 官方机器人「自定义菜单 + 指令面板」（菜单面板 API，官方文档
// https://bot.q.qq.com/wiki/develop/api-v2/server-inter/menu-panel/）
// 官方字段为 snake_case，全部用 JsonPropertyName 显式标注（序列化/反序列化一致）。
// ===================================================================

/// <summary>全局自定义菜单（单聊窗口底部按钮，设置后对所有用户生效）</summary>
public class MenuDefinition
{
    /// <summary>菜单项列表，最多 10 个，按列表顺序从左到右展示</summary>
    [JsonPropertyName("items")]
    public List<MenuButton>? Items { get; set; } = [];
}

/// <summary>菜单项（一级按钮）</summary>
public class MenuButton
{
    /// <summary>按钮名称，最多 10 个字符（一个中文汉字算 2 个字符）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>按钮类型：switch（开关）/ send_message（发送消息）/ link（链接跳转）/ menu（含子菜单的折叠项）</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "send_message";

    /// <summary>子菜单列表，仅 type=menu 时有效。子菜单最多 5 个，不支持再嵌套子菜单</summary>
    [JsonPropertyName("sub_menu_items")]
    public List<SubMenuButton>? SubMenuItems { get; set; }

    /// <summary>发送的内容，仅 type=send_message 时有效。用户点击后该文本自动填入聊天输入框</summary>
    [JsonPropertyName("send_message")]
    public string? SendMessage { get; set; }

    /// <summary>跳转链接 URL，仅 type=link 时有效，必须以 https:// 开头</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    /// <summary>开关配置，仅 type=switch 时有效。定义开关标识与默认状态</summary>
    [JsonPropertyName("switch")]
    public SwitchConfig? Switch { get; set; }
}

/// <summary>二级菜单项</summary>
public class SubMenuButton
{
    /// <summary>按钮名称，最多 14 个字符（约 7 个中文汉字）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>按钮类型：send_message（发送消息）/ link（链接跳转），二级菜单不支持 menu</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "send_message";

    /// <summary>发送的内容，仅 type=send_message 时有效</summary>
    [JsonPropertyName("send_message")]
    public string? SendMessage { get; set; }

    /// <summary>跳转链接 URL，仅 type=link 时有效，必须以 https:// 开头</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }
}

/// <summary>开关配置（用户切换后消息 ext 字段携带 switch_id=1 标识）</summary>
public class SwitchConfig
{
    /// <summary>开关唯一标识。用户打开后消息 ext 携带 {switch_id}=1，关闭后不携带</summary>
    [JsonPropertyName("switch_id")]
    public string SwitchId { get; set; } = "";

    /// <summary>开关初始状态：true 默认打开，false 默认关闭</summary>
    [JsonPropertyName("default")]
    public bool Default { get; set; }
}

/// <summary>指令面板内容（一个面板最多 20 个元素）</summary>
public class PanelDefinition
{
    /// <summary>面板元素列表，最多 20 个</summary>
    [JsonPropertyName("items")]
    public List<PanelItem>? Items { get; set; } = [];

    /// <summary>面板备注，最多 255 个字符，不对用户展示</summary>
    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    /// <summary>当前版本号（修改时需带，用于并发控制）</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }
}

/// <summary>指令面板元素</summary>
public class PanelItem
{
    /// <summary>元素名称。type=command 时点击后填入输入框；type=link 时仅展示。最多 14 个字符</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>元素描述，展示给用户。最多 30 个字符</summary>
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    /// <summary>元素类型：command（指令）/ link（链接跳转）</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "command";

    /// <summary>是否仅管理员可操作：true 仅频道/群管理员可点击</summary>
    [JsonPropertyName("only_admin")]
    public bool? OnlyAdmin { get; set; }

    /// <summary>跳转链接 URL，仅 type=link 时有效</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }
}

/// <summary>创建指令面板请求体</summary>
public class CreatePanelRequest
{
    /// <summary>生效场景：c2c（单聊）/ group（群聊）/ channel（文字子频道）/ dm（频道私信）</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "c2c";

    /// <summary>作用范围：all（全局）/ specific（指定用户/群）。channel/dm 仅支持 all</summary>
    [JsonPropertyName("target_type")]
    public string? TargetType { get; set; } = "all";

    /// <summary>用户 openid 列表，仅 c2c + specific 有效，一次最多 20 个</summary>
    [JsonPropertyName("user_openids")]
    public List<string>? UserOpenIds { get; set; }

    /// <summary>群 openid 列表，仅 group + specific 有效，一次最多 20 个</summary>
    [JsonPropertyName("group_openids")]
    public List<string>? GroupOpenIds { get; set; }

    /// <summary>面板配置内容（必填）</summary>
    [JsonPropertyName("panel")]
    public PanelDefinition Panel { get; set; } = new();
}

/// <summary>修改指令面板关联对象请求体（c2c 操作用户 openid，group 操作群 openid）</summary>
public class UpdatePanelTargetsRequest
{
    /// <summary>操作类型：add（添加关联对象）/ del（移除关联对象）</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "add";

    /// <summary>用户 openid 列表，仅 c2c 场景有效，一次最多 20 个</summary>
    [JsonPropertyName("user_openids")]
    public List<string>? UserOpenIds { get; set; }

    /// <summary>群 openid 列表，仅 group 场景有效，一次最多 20 个</summary>
    [JsonPropertyName("group_openids")]
    public List<string>? GroupOpenIds { get; set; }
}

/// <summary>查询全局自定义菜单响应</summary>
public class MenuResponse
{
    /// <summary>当前菜单的版本号</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>当前生效的菜单配置（未设置过时为空）</summary>
    [JsonPropertyName("menu")]
    public MenuDefinition? Menu { get; set; }
}

/// <summary>修改菜单 / 修改面板的响应（版本号）</summary>
public class VersionResponse
{
    /// <summary>本次修改后的版本号</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }
}

/// <summary>创建指令面板响应</summary>
public class CreatePanelResponse
{
    /// <summary>新创建的面板 ID</summary>
    [JsonPropertyName("panel_id")]
    public string? PanelId { get; set; }
}

/// <summary>查询指令面板列表响应</summary>
public class PanelListResponse
{
    /// <summary>面板记录列表，按设置时间倒序</summary>
    [JsonPropertyName("records")]
    public List<PanelRecord>? Records { get; set; }

    /// <summary>下一页游标，空串表示已到最后一页</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    /// <summary>是否已拉取到最后一页</summary>
    [JsonPropertyName("is_end")]
    public bool IsEnd { get; set; }
}

/// <summary>指令面板记录（列表 / 详情）</summary>
public class PanelRecord
{
    /// <summary>面板 ID</summary>
    [JsonPropertyName("panel_id")]
    public string? PanelId { get; set; }

    /// <summary>生效场景：c2c / group / channel / dm</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    /// <summary>作用范围：all / specific</summary>
    [JsonPropertyName("target_type")]
    public string? TargetType { get; set; }

    /// <summary>面板配置内容</summary>
    [JsonPropertyName("panel")]
    public PanelDefinition? Panel { get; set; }

    /// <summary>创建时间（RFC3339）</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>更新时间（RFC3339）</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    /// <summary>面板版本号</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>关联的用户 openid 列表。仅 c2c 场景且 target_type=specific 时返回，最多 1000 条</summary>
    [JsonPropertyName("user_openids")]
    public List<string>? UserOpenIds { get; set; }

    /// <summary>关联的群 openid 列表。仅 group 场景且 target_type=specific 时返回，最多 1000 条</summary>
    [JsonPropertyName("group_openids")]
    public List<string>? GroupOpenIds { get; set; }
}
