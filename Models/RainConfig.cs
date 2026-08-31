namespace RainBot.Models;

/// <summary>
/// RainBot 运行配置（appsettings "Rain" 段，管理员可通过 /admin 指令热修改，
/// 修改值存 SQLite 的 settings 表覆盖默认值）
/// </summary>
public class RainConfig
{
    /// <summary>LLM（DeepSeek）配置</summary>
    public LlmConfig Llm { get; set; } = new();

    /// <summary>触发与节流配置</summary>
    public TriggerConfig Trigger { get; set; } = new();

    /// <summary>上下文与缓存配置</summary>
    public ContextConfig Context { get; set; } = new();

    /// <summary>风控与平台合规配置</summary>
    public SafetyConfig Safety { get; set; } = new();

    /// <summary>随机互动娱乐配置（移植自原版 RainBOT）</summary>
    public FunConfig Fun { get; set; } = new();

    /// <summary>MCP 工具服务器配置（Model Context Protocol）</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>
    /// 机器人实例种子（首次运行写入 bot_instances 表；之后以数据库为准，可在 WebUI 热管理）。
    /// 老版本 Bot 段（AppId/Secret）会自动迁移为默认实例 qq。
    /// </summary>
    public List<BotInstance> Bots { get; set; } = [];

    /// <summary>存储配置</summary>
    public StorageConfig Storage { get; set; } = new();

    /// <summary>人设文件路径（相对运行目录）</summary>
    public string PersonaPath { get; set; } = "Persona/persona.md";

    /// <summary>SayNo 反驳不词汇表 JSON 路径（相对运行目录，缺失时自动生成默认词表，编辑后热重载）</summary>
    public string SayNoPath { get; set; } = "sayno.json";

    /// <summary>
    /// 公网访问基址（域名，如 https://bot.example.com），只需设置一次。
    /// 所有静态资源（如 OSM 梗图）都以该域名 + wwwroot 相对路径对外提供。
    /// 留空时 OSM 梗图等依赖公网地址的功能自动禁用。
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// 调试模式：开启后在每次对话输出末尾追加一行统计「x tokens, x tools」
    /// （x = 本轮输入+输出 token 总数、工具调用次数）。适合排查成本与工具行为。
    /// </summary>
    public bool DebugMode { get; set; } = false;

    /// <summary>
    /// Markdown 回复：开启后所有文本回复以 Markdown 消息（msg_type=2）发送到 QQ 网关，
    /// 而不是纯文本（msg_type=0）。调试模式的统计行同时改为块引用格式「&gt; x tokens, x tools」。
    /// </summary>
    public bool MarkdownReply { get; set; } = false;

    /// <summary>机器人自我维护的管理员 OpenID 列表（初始值来自配置，之后由 /admin 指令维护入库）</summary>
    public List<string> AdminOpenIds { get; set; } = [];
}

public class LlmConfig
{
    /// <summary>DeepSeek API 地址（OpenAI 兼容）</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>API Key（推荐用环境变量 RAIN__LLM__APIKEY 注入，不写入配置文件）</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>模型名（本项目只使用这一个模型）</summary>
    public string Model { get; set; } = "deepseek-v4-flash";

    /// <summary>采样温度</summary>
    public double Temperature { get; set; } = 0.9;

    /// <summary>单次请求超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>ReAct 工具调用最大轮次（防死循环）</summary>
    public int MaxToolRounds { get; set; } = 3;

    /// <summary>输出强制最多行数（超出截断）</summary>
    public int MaxOutputLines { get; set; } = 2;

    /// <summary>输出强制最多字符数（超出截断）</summary>
    public int MaxOutputChars { get; set; } = 160;
}

public class TriggerConfig
{
    /// <summary>被动触发后的群冷却期（秒），冷却期内无视 @ 消息但仍入队统计</summary>
    public int PassiveCooldownSeconds { get; set; } = 30;

    /// <summary>密度唤醒：时间窗口（分钟）</summary>
    public int DensityWindowMinutes { get; set; } = 1;

    /// <summary>密度唤醒：窗口内消息数阈值</summary>
    public int DensityThreshold { get; set; } = 10;

    /// <summary>沉默唤醒：群聊静默阈值（分钟）</summary>
    public int SilenceMinutes { get; set; } = 30;

    /// <summary>单群每小时主动发言次数上限</summary>
    public int ActivePerHour { get; set; } = 1;

    /// <summary>话题存活时间（分钟）：窗口内有消息且距最后消息不超过该值才可密度唤醒</summary>
    public int TopicAliveMinutes { get; set; } = 10;

    /// <summary>搜索结果本地缓存时长（分钟）</summary>
    public int SearchCacheMinutes { get; set; } = 10;

    /// <summary>互动增强：群友在该时间窗内出现过才会被 @（分钟）</summary>
    public int AtRecentWindowMinutes { get; set; } = 30;
}

public class ContextConfig
{
    /// <summary>上下文水位线（tokens，192k 的 80%）</summary>
    public int WatermarkTokens { get; set; } = 153600;

    /// <summary>每群保留的最大历史消息数（数据库与内存上限）</summary>
    public int MaxHistoryPerGroup { get; set; } = 500;

    /// <summary>组装时 Block E 的历史 token 预算（低于水位，压缩前防御第一步优先删 E 头部）</summary>
    public int HistoryAssembleCapTokens { get; set; } = 128000;

    /// <summary>L1 锚点最大数量（Top 高互动用户）</summary>
    public int MaxAnchorCount { get; set; } = 5;

    /// <summary>压缩前防御第二步：锚点缩减到的数量</summary>
    public int MinAnchorCount { get; set; } = 2;

    /// <summary>蒸馏压缩后保留的最近消息条数</summary>
    public int DistillKeepMessages { get; set; } = 3;

    /// <summary>降级运行：群聊静默该时长（分钟）后彻底重置上下文、重建缓存</summary>
    public int DegradeResetSilenceMinutes { get; set; } = 120;

    /// <summary>缓存命中率告警阈值（低于触发告警）</summary>
    public double CacheAlertThreshold { get; set; } = 0.6;

    /// <summary>蒸馏摘要最大字符数</summary>
    public int DistillMaxChars { get; set; } = 600;
}

public class SafetyConfig
{
    /// <summary>单群每分钟发送上限（官方 20 qpm，留余量）</summary>
    public int MaxQpmPerGroup { get; set; } = 15;

    /// <summary>消息去重窗口（秒），同一 msg_id 重复推送忽略</summary>
    public int DedupeWindowSeconds { get; set; } = 600;
}

/// <summary>
/// MCP（Model Context Protocol）工具服务器配置。
/// 配置段 Rain:Mcp（appsettings 或环境变量），启动时一次性连接并注册工具，
/// 改配置需重启生效（保 Block B 前缀稳定）。
/// </summary>
public class McpConfig
{
    /// <summary>是否启用 MCP 工具（默认启用；未配置任何 server 则自动跳过）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>MCP server 列表</summary>
    public List<McpServerConfig> Servers { get; set; } = [];
}

/// <summary>单个 MCP server 连接配置</summary>
public class McpServerConfig
{
    /// <summary>server 名称（唯一；工具名前缀 mcp__{名称}__{工具名}）</summary>
    public string Name { get; set; } = "";

    /// <summary>传输类型：stdio（本地进程）或 http（streamable http / sse 端点）</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>stdio 传输：可执行文件（如 npx、python、dotnet）</summary>
    public string Command { get; set; } = "";

    /// <summary>stdio 传输：启动参数（如 -y @modelcontextprotocol/server-everything）</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>http 传输：MCP 端点 URL</summary>
    public string Url { get; set; } = "";

    /// <summary>http 传输：请求头（如 Authorization: Bearer xxx）</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>工具调用超时（秒，0 = 用默认 60s）</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

public class StorageConfig
{
    /// <summary>SQLite 数据库文件路径（相对运行目录）</summary>
    public string SqlitePath { get; set; } = "Data/rainbot.db";
}

/// <summary>
/// 随机互动配置（原版 RainBOT 概率：反驳是 40 / 反驳不 16 / 复读 7 / OSM 2 / 反向艾特 70 / 叫哥 4）
/// </summary>
public class FunConfig
{
    /// <summary>随机反驳是：消息 ==「是」时概率反驳</summary>
    public bool EnableReplyYes { get; set; } = true;
    public int ReplyYesProbability { get; set; } = 40;

    /// <summary>随机反驳不：词表抬杠（不/没/是/别/太/可以/能/可能/要/想）</summary>
    public bool EnableReplyNo { get; set; } = true;
    public int ReplyNoProbability { get; set; } = 16;

    /// <summary>
    /// 反驳不命中时，以该概率改用烂梗 API 回复（另一种随机表现形式）。
    /// 默认为空：留空不触发该分支，始终使用词表回复。
    /// </summary>
    public string ReplyNoMemeUrl { get; set; } = "";
    public int ReplyNoMemeProbability { get; set; } = 30;

    /// <summary>随机复读：延迟后原样复读（50% 概率加 desuwa～）</summary>
    public bool EnableRepeat { get; set; } = true;
    public int RepeatProbability { get; set; } = 7;
    public int RepeatDelayMinSeconds { get; set; } = 30;
    public int RepeatDelayMaxSeconds { get; set; } = 80;
    /// <summary>复读忽略内容（含这些词的文本不复读）</summary>
    public List<string> RepeatIgnoreWords { get; set; } = [];

    /// <summary>
    /// 随机 OSM：概率发送一张 OSM 梗图。
    /// 图片不再手动配置路径：自动扫描 wwwroot/osm/（含子目录），
    /// 对外地址 = Rain.PublicBaseUrl + 相对路径（域名只需设置一次）。
    /// </summary>
    public bool EnableOsm { get; set; } = true;
    public int OsmProbability { get; set; } = 2;

    /// <summary>反向艾特：@ 机器人时把 @ 弹回发送者（不阻断 AI 回复）</summary>
    public bool EnableReverseAt { get; set; } = true;
    public int ReverseAtProbability { get; set; } = 70;
    /// <summary>反向艾特忽略用户 openid（完整或前 8 位短 ID）</summary>
    public List<string> ReverseAtIgnoreOpenIds { get; set; } = [];

    /// <summary>随机叫哥：@ 发送者 + 名字随机截取 + 随机后缀</summary>
    public bool EnableCallBrother { get; set; } = true;
    public int CallBrotherProbability { get; set; } = 4;
    /// <summary>叫哥延迟（秒，原版用复读延迟区间）</summary>
    public int CallBrotherDelaySeconds { get; set; } = 30;
    /// <summary>叫哥忽略用户 openid（完整或前 8 位短 ID）</summary>
    public List<string> CallBrotherIgnoreOpenIds { get; set; } = [];
}
