namespace RainBot.Services.WebUi;

/// <summary>配置项元数据（网页配置页展示用：分组 / 中文名 / 说明 / 控件类型）</summary>
public sealed record ConfigMeta(string Key, string Section, string Label, string Description, string Type, double? Min = null, double? Max = null, double? Step = null);

/// <summary>全部可热改参数的元数据目录（键名与 RuntimeConfig.AllKeys 对应）</summary>
public static class ConfigMetadata
{
    /// <summary>类型：text / number / double / percent / bool / secret / stringlist</summary>
    public static readonly IReadOnlyList<ConfigMeta> All =
    [
        // ---------- LLM ----------
        new("Llm.BaseUrl", "LLM", "接口地址", "DeepSeek API 地址（OpenAI 兼容）", "text"),
        new("Llm.ApiKey", "LLM", "API Key", "DeepSeek API Key（推荐用环境变量 RAIN__LLM__APIKEY 注入）", "secret"),
        new("Llm.Model", "LLM", "模型名", "本项目只使用这一个模型", "text"),
        new("Llm.Temperature", "LLM", "采样温度", "越高越发散，越低越稳定", "double", 0, 2, 0.1),
        new("Llm.TimeoutSeconds", "LLM", "请求超时（秒）", "单次 LLM 请求超时", "number", 1, 600),
        new("Llm.MaxToolRounds", "LLM", "工具最大轮次", "ReAct 工具调用最大轮次（防死循环）", "number", 1, 20),
        new("Llm.MaxOutputLines", "LLM", "输出最大行数", "超出截断（铁律：最多 2 行）", "number", 1, 20),
        new("Llm.MaxOutputChars", "LLM", "输出最大字符", "超出截断", "number", 1, 4000),

        // ---------- 触发 ----------
        new("Trigger.PassiveCooldownSeconds", "触发", "被动冷却（秒）", "被动触发后群冷却期，冷却期内 @ 消息不唤醒", "number", 1, 3600),
        new("Trigger.DensityWindowMinutes", "触发", "密度窗口（分钟）", "密度唤醒：统计时间窗口", "number", 1, 60),
        new("Trigger.DensityThreshold", "触发", "密度阈值（条）", "密度唤醒：窗口内消息数达标即暖群", "number", 2, 100),
        new("Trigger.SilenceMinutes", "触发", "沉默阈值（分钟）", "沉默唤醒：群聊静默该时长后暖群", "number", 1, 1440),
        new("Trigger.ActivePerHour", "触发", "主动发言上限", "单群每小时主动暖群次数上限", "number", 1, 24),
        new("Trigger.TopicAliveMinutes", "触发", "话题存活（分钟）", "窗口内有消息且距最后消息不超过该值才可密度唤醒", "number", 1, 1440),
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

        // ---------- 通用 ----------
        new("PublicBaseUrl", "通用", "公网域名", "静态资源基址（如 https://bot.example.com），只需设置一次；OSM 梗图等自动以「域名 + wwwroot 相对路径」对外提供，留空则相关功能禁用", "text"),
        new("DebugMode", "通用", "调试模式", "开启后在每次对话输出末尾追加一行「x tokens, x tools」统计（输入+输出 token 总数、工具调用次数），排查成本与工具行为用", "bool"),
        new("MarkdownReply", "通用", "Markdown 回复", "开启后所有文本回复以 Markdown 消息（msg_type=2）发送到 QQ 网关而非纯文本；调试统计行显示为「> x tokens, x tools」块引用", "bool"),
        new("PersonaPath", "通用", "人设文件路径", "相对运行目录；编辑保存即热重载", "text"),
        new("SayNoPath", "通用", "SayNo 词表路径", "反驳不词表 JSON 路径；编辑保存即热重载", "text")
    ];

    public static ConfigMeta? Find(string key)
        => All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Sections => ["LLM", "触发", "上下文", "风控", "随机互动", "通用"];
}
