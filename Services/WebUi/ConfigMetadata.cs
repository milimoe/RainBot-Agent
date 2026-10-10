namespace RainBot.Services.WebUi;

/// <summary>配置项元数据（网页配置页展示用：分组 / 中文名 / 说明 / 控件类型）</summary>
public sealed record ConfigMeta(string Key, string Section, string Label, string Description, string Type, double? Min = null, double? Max = null, double? Step = null);

/// <summary>全部可热改参数的元数据目录（键名与 RuntimeConfig.AllKeys 对应）</summary>
public static class ConfigMetadata
{
    /// <summary>路径保留后端兼容，仅隐藏 WebUI 日常配置入口。</summary>
    public static bool IsVisible(string key) => !key.Equals("PromptPath", StringComparison.OrdinalIgnoreCase)
        && !key.Equals("PersonaPath", StringComparison.OrdinalIgnoreCase);
    /// <summary>类型：text / number / double / percent / bool / secret / stringlist</summary>
    public static readonly IReadOnlyList<ConfigMeta> All =
    [
        new("Page.PrefetchEnabled", "页面读取", "B 站链接自动读取", "回复前读取文本及官 Q 图文卡片中的 BV 视频链接和 b23 短链，不处理小程序卡片", "bool"),
        new("Page.BudgetSeconds", "页面读取", "自动读取预算（秒）", "本轮全部预取的总等待上限，超时明确告知模型；实际限制 1–10 秒", "number", 1, 10),
        new("Page.MaxLinksPerTurn", "页面读取", "每轮视频链接上限", "超过上限的链接不预取；实际限制 1–4 条", "number", 1, 4),
        new("Page.LinkLookbackSeconds", "页面读取", "视频链接回溯（秒）", "指代性追问时回溯群内最近视频链接；0 关闭，图片回溯保持原行为", "number", 0, 3600),
        new("Page.RequestTimeoutSeconds", "页面读取", "读取超时（秒）", "open_page 单次读取总预算，包含排队、跳转及下载；实际限制 1–60 秒", "number", 1, 60),
        new("Page.CacheMinutes", "页面读取", "页面缓存（分钟）", "成功结果缓存时长；失败仅按当前链接缓存 60 秒", "number", 1, 1440),
        new("Page.MaxResponseKb", "页面读取", "页面大小上限（KB）", "解压后的响应体上限；仅接受 HTML 页面", "number", 1, 2048),
        new("Page.MaxChars", "页面读取", "摘要字符上限", "open_page 返回摘要的长度，标题和状态另计", "number", 100, 800),
        // ---------- LLM ----------
        new("Llm.BaseUrl", "LLM", "接口地址", "DeepSeek API 地址（OpenAI 兼容）", "text"),
        new("Llm.ApiKey", "LLM", "API Key", "DeepSeek API Key（推荐用环境变量 RAIN__LLM__APIKEY 注入）", "secret"),
        new("Llm.Model", "LLM", "模型名", "本项目只使用这一个模型", "text"),
        new("Llm.Temperature", "LLM", "采样温度", "越高越发散，越低越稳定（对话/收口轮）", "double", 0, 2, 0.1),
        new("Llm.ToolTemperature", "LLM", "工具轮温度", "进入工具链中段（已产生工具结果）后的采样温度，宜低于主温度以稳定工具参数", "double", 0, 2, 0.1),
        new("Llm.TimeoutSeconds", "LLM", "请求超时（秒）", "单次 LLM 请求超时", "number", 1, 600),
        new("Llm.MaxToolRounds", "LLM", "工具最大轮次", "ReAct 工具调用最大轮次（防死循环；触顶后自动补一次禁用工具的收口请求）", "number", 1, 20),
        new("Llm.ToolRoundMaxTokens", "LLM", "工具轮输出上限", "工具轮 max_tokens，需容纳工具调用参数 JSON；过小会把多参数调用截断", "number", 64, 8192),
        new("Llm.MaxOutputLines", "LLM", "输出最大行数", "超出截断（铁律：最多 2 行）", "number", 1, 20),
        new("Llm.MaxOutputChars", "LLM", "输出最大字符", "超出截断", "number", 1, 4000),
        new("Llm.EnableVision", "LLM", "视觉识图", "开启后带图消息会把图片内联进多模态请求（DeepSeek 视觉格式，支持 JPEG/PNG/GIF/WebP，单图 ≤8MB）；模型不支持图片时关闭，否则该轮请求会失败", "bool"),

        new("PromptPath", "通用", "默认提示词路径", "默认人设的提示词配置 JSON，保存后热重载", "text"),
        new("Profile.DailyUpdateLimit", "画像", "每日画像更新配额", "每群每用户每日更新上限；0 禁止普通对话更新，暖群不受限", "number", 0, 100),
        new("Profile.ActiveWindowHours", "画像", "身份活跃窗口（小时）", "艾特工具仅允许本群最近活跃用户", "number", 1, 8760),
        new("Trigger.BacklogMaxAgeSeconds", "触发", "积压消息最大时效（秒）", "超时消息仅入库统计，不触发回复；0 禁用", "number", 0, 86400),
        // ---------- 触发 ----------
        new("Trigger.PassiveCooldownSeconds", "触发", "被动冷却（秒）", "被动触发后群冷却期，冷却期内 @ 消息不唤醒", "number", 1, 3600),
        new("Trigger.ImageLookbackSeconds", "触发", "图片回溯（秒）", "触发消息没带图时向前回溯触发者本人最近一张图一起识图，覆盖「先发图、再 @ 机器人分析」；0 = 关闭", "number", 0, 3600),
        new("Trigger.DensityWindowMinutes", "触发", "密度窗口（分钟）", "密度唤醒：统计时间窗口", "number", 1, 60),
        new("Trigger.DensityThreshold", "触发", "密度阈值（条）", "密度唤醒：窗口内消息数达标即暖群", "number", 2, 100),
        new("Trigger.SilenceMinutes", "触发", "沉默阈值（分钟）", "沉默唤醒：群聊静默该时长后暖群", "number", 1, 1440),
        new("Trigger.ActivePerHour", "触发", "主动发言上限", "单群每小时主动暖群次数上限", "number", 1, 24),
        new("Trigger.TopicAliveMinutes", "触发", "话题存活（分钟）", "窗口内有消息且距最后消息不超过该值才可密度唤醒", "number", 1, 1440),
        new("Trigger.RandomChatProbability", "触发", "随机插嘴概率%", "普通群消息（未 @）按此概率触发人设回复，像群友一样搭话；0 = 关闭。命中后还会受插嘴冷却、被动冷却与静默群限制", "number", 0, 100),
        new("Trigger.RandomChatCooldownSeconds", "触发", "插嘴冷却（秒）", "同群两次随机插嘴的最小间隔（与被动冷却独立），调大更克制", "number", 1, 86400),
        new("Trigger.SearchCacheMinutes", "触发", "搜索缓存（分钟）", "搜索结果本地缓存时长", "number", 1, 1440),
        new("Trigger.AtRecentWindowMinutes", "触发", "互动窗口（分钟）", "群友在该时间窗内出现过才会被 @（互动增强）", "number", 1, 1440),

        // ---------- 上下文 ----------
        new("Context.WatermarkTokens", "上下文", "上下文水位线", "tokens 水位线（192k 的 80%），超限触发三级治理", "number", 1000, 1000000),
        new("Context.MaxHistoryPerGroup", "上下文", "每群历史上限", "内存与数据库保留的最大历史消息数", "number", 10, 10000),
        new("Context.HistoryAssembleCapTokens", "上下文", "历史组装预算", "Block E 历史 token 预算（压缩前防御第一步）", "number", 1000, 1000000),
        new("Context.MaxAnchorCount", "上下文", "锚点最大数", "L1 锚点 Top 高互动用户数量", "number", 1, 50),
        new("Context.MinAnchorCount", "上下文", "锚点最小数", "压缩时锚点缩减到的数量", "number", 1, 50),
        new("Context.DistillKeepMessages", "上下文", "蒸馏保留条数", "蒸馏压缩后保留的最近消息数", "number", 1, 100),
        new("Context.DegradeResetSilenceMinutes", "上下文", "降级重置静默", "降级后静默该时长（分钟）彻底重置上下文", "number", 1, 10080),
        new("Context.CacheAlertThreshold", "上下文", "缓存告警阈值", "缓存命中率低于该值触发成本告警", "percent"),
        new("Context.DistillMaxChars", "上下文", "蒸馏摘要长度", "蒸馏摘要最大字符数", "number", 50, 10000),

        // ---------- 风控 ----------
        new("Safety.MaxQpmPerGroup", "风控", "单群频控（qpm）", "官方 20 qpm 留余量，勿调大", "number", 1, 20),
        new("Safety.DedupeWindowSeconds", "风控", "去重窗口（秒）", "同一 msg_id 重复推送忽略", "number", 10, 86400),

        // ---------- 随机互动 ----------
        new("Fun.EnableReplyYes", "随机互动", "反驳「是」开关", "消息 =「是」时概率反驳「是你的头」", "bool"),
        new("Fun.ReplyYesProbability", "随机互动", "反驳「是」概率%", "0 永不，100 必中", "number", 0, 100),
        new("Fun.EnableReplyNo", "随机互动", "反驳「不」开关", "词表抬杠（词表存于 sayno.json）", "bool"),
        new("Fun.ReplyNoProbability", "随机互动", "反驳「不」概率%", "命中抬杠词后按此概率反驳", "number", 0, 100),
        new("Fun.ReplyNoMemeUrl", "随机互动", "烂梗 API 地址", "默认留空：留空不触发烂梗分支，始终用词表；填 URL 后按概率改用烂梗 API（失败自动回退词表）", "text"),
        new("Fun.ReplyNoMemeProbability", "随机互动", "烂梗概率%", "烂梗 API 命中概率（失败自动回退词表）", "number", 0, 100),
        new("Fun.EnableRepeat", "随机互动", "随机复读开关", "延迟 30-80s 复读，50% 加 desuwa～", "bool"),
        new("Fun.RepeatProbability", "随机互动", "复读概率%", "0 永不，100 必中", "number", 0, 100),
        new("Fun.RepeatDelayMinSeconds", "随机互动", "复读最小延迟", "延迟区间下限（秒）", "number", 1, 3600),
        new("Fun.RepeatDelayMaxSeconds", "随机互动", "复读最大延迟", "延迟区间上限（秒）", "number", 1, 3600),
        new("Fun.EnableOsm", "随机互动", "随机 OSM 开关", "概率发送 OSM 梗图（自动扫描 wwwroot/osm 目录）", "bool"),
        new("Fun.OsmProbability", "随机互动", "OSM 概率%", "0 永不，100 必中", "number", 0, 100),
        new("Fun.EnableReverseAt", "随机互动", "反向艾特开关", "@ 机器人时把 @ 弹回发送者（不阻断 AI 回复）", "bool"),
        new("Fun.ReverseAtProbability", "随机互动", "反向艾特概率%", "0 永不，100 必中", "number", 0, 100),
        new("Fun.EnableCallBrother", "随机互动", "随机叫哥开关", "@+名字截取+随机后缀，延迟 30s", "bool"),
        new("Fun.CallBrotherProbability", "随机互动", "叫哥概率%", "0 永不，100 必中", "number", 0, 100),
        new("Fun.CallBrotherDelaySeconds", "随机互动", "叫哥延迟（秒）", "延迟后发送", "number", 1, 3600),

        // ---------- 搜索 ----------
        new("Search.Provider", "搜索", "搜索后端", "bing = cn.bing.com（默认，国内可直连）；tavily = 商业 API（需填 Key，有每日额度，用尽自动回退 bing）；duckduckgo = 部分网络不可达；searxng = 自建实例（需填基址）", "text"),
        new("Search.TavilyApiKey", "搜索", "Tavily API Key", "app.tavily.com 获取（tvly- 开头）；Provider 选 tavily 时使用，未填则回退 bing", "text"),
        new("Search.TavilyDailyLimit", "搜索", "Tavily 每日上限", "每日调用次数上限（按服务器本地日期，跨天自动重置）；达到上限自动回退 bing；0 = 不限制", "number", 0, 100000),
        new("Search.SearxngBaseUrl", "搜索", "SearXNG 基址", "后端选 searxng 时必填，如 https://searx.example.com（实例需开启 json 输出格式）", "text"),
        new("Search.TimeoutSeconds", "搜索", "搜索超时（秒）", "单次搜索超时；网络不通时快速失败，避免拖住整条回复", "number", 3, 60),
        new("Search.MaxResults", "搜索", "结果条数", "进上下文的搜索结果条数", "number", 1, 8),

        // ---------- 通用 ----------
        new("PublicBaseUrl", "通用", "公网域名", "静态资源基址（如 https://bot.example.com），只需设置一次；OSM 梗图等自动以「域名 + wwwroot 相对路径」对外提供，留空则相关功能禁用", "text"),
        new("DebugMode", "通用", "调试模式", "开启后在每次对话输出末尾追加一行「x tokens, x tools」统计（输入+输出 token 总数、工具调用次数），排查成本与工具行为用", "bool"),
        new("DebugShowReasoning", "通用", "思维显示", "需先开启调试模式：把模型的思维内容用代码块包起来，与回复内容一起发送，便于直接观察推理过程（仅推理型模型有效）", "bool"),
        new("MarkdownReply", "通用", "Markdown 回复", "开启后所有文本回复以 Markdown 消息（msg_type=2）发送到 QQ 网关而非纯文本；调试统计行显示为「> x tokens, x tools」块引用", "bool"),
        new("PersonaPath", "通用", "人设文件路径", "相对运行目录；编辑保存即热重载", "text"),
        new("SayNoPath", "通用", "SayNo 词表路径", "反驳不词表 JSON 路径；编辑保存即热重载", "text")
    ];

    public static ConfigMeta? Find(string key)
        => All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Sections => ["LLM", "画像", "触发", "上下文", "风控", "随机互动", "搜索", "通用"];
}
