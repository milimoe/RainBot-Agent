# RainBot Agent「雨」🌧️

基于 **DeepSeek API**（单一 `deepseek-v4-flash` 模型）与 **QQ 官方机器人接口**构建的 QQ 群聊机器人智能体（ReAct）。核心策略：**节流触发**（非全量监听）与**极致缓存优化**（前缀稳定 → DeepSeek 硬盘缓存命中），只为降低 API 成本。

- 语言/框架：C# / .NET 10（ASP.NET Core）
- QQ 接入：纯手写 WebSocket 协议（无第三方 SDK，基于 [QQBot-WebSocket](https://github.com/tencent-connect/botpy) 同源协议，官方文档 [bot.q.qq.com](https://bot.q.qq.com)）
- 存储：SQLite 单文件（画像 / 历史 / 配置 / 统计，零依赖）
- 搜索：DuckDuckGo（免费无 Key，可替换）
- 部署：宝塔面板 .NET 项目 / systemd / Docker（见 [deploy/宝塔部署指南.md](deploy/宝塔部署指南.md)）

## 核心特性

| 模块 | 说明 |
| :--- | :--- |
| 双轨触发 | 被动（@/回复，即时响应 + 每群 30s 冷却，冷却期消息仍入队统计）；主动暖群（密度唤醒 1min≥10 条 / 沉默唤醒 30min / 每小时 ≤1 次） |
| 上下文缓存 | Block A-F 固定顺序组装，可变内容只追加尾部、头部整条丢弃；153.6k 水位三级治理（删 E → 缩 D → 蒸馏压缩 + 降级运行） |
| 群友画像 | L0 全量画像存库不进上下文；L1 锚点 Top 5 常驻（<20 tokens）；L2 触发式召回进尾部块，下轮即弃 |
| ReAct 工具 | `web_search`（10 分钟话题缓存）、`get_user_profile`、`update_user_profile`（仅暖群）、管理员工具；轮次上限防死循环 |
| 风控 | 输入（广告/涉政/引流不回应）、输出（openid 泄露替换、≤2 行截断）、平台频控（15 qpm 留余量）、成本监控（缓存命中率 <60% 告警） |
| 随机互动 | 移植原版 RainBOT：随机反驳是/不、随机复读（延迟防刷屏）、随机OSM（梗图）、反向艾特、随机叫哥；纯规则概率触发不耗 Token，一次消息最多命中一个（防刷屏） |
| 人设系统 | `Persona/persona.md` 随时改写，保存即热重载，无需重启 |

## 快速开始

1. 申请：QQ 开放平台创建机器人（群聊权限 +「接收所有消息」）、DeepSeek 获取 API Key
2. 配置（环境变量或 `appsettings.Production.json`）：

```bash
export BOT__APPID="你的AppID"
export BOT__SECRET="你的AppSecret"
export RAIN__LLM__APIKEY="你的DeepSeekKey"
export RAIN__ADMINOPENIDS__0="你的OpenID"   # 第一个管理员
```

> `RAIN__BOTOPENID`（机器人群内 OpenID）：可选。开启「接收所有消息」后官方推送的 content 已去除 @ 机器人前缀，判断是否 @ 机器人需要机器人自身 openid 精确比对（兼容历史 `<@!{bot_openid}>` 标签）。**不填也能用**——机器人首次被 @ 时会自动学习并落库；也可 `/admin set Bot.OpenId` 手动设置（配置优先）。

3. 运行：

```bash
dotnet run                       # 开发（沙箱在 appsettings.Development.json）
dotnet publish -c Release -o publish && cd publish && dotnet RainBot.dll
curl http://localhost:8080/health   # {"status":"ok"} 即连接成功
```

## 配置参数（`/admin list` 查看，`/admin set` 热改）

| 参数 | 默认值 | 说明 |
| :--- | :--- | :--- |
| `Trigger.PassiveCooldownSeconds` | 30 | 被动触发后群冷却（秒） |
| `Trigger.DensityWindowMinutes` / `DensityThreshold` | 1 / 10 | 密度唤醒窗口与阈值 |
| `Trigger.SilenceMinutes` | 30 | 沉默唤醒阈值（分钟） |
| `Trigger.ActivePerHour` | 1 | 单群每小时主动发言上限 |
| `Trigger.SearchCacheMinutes` | 10 | 搜索结果本地缓存时长 |
| `Context.WatermarkTokens` | 153600 | 水位线（192k×80%） |
| `Context.HistoryAssembleCapTokens` | 128000 | Block E 组装预算 |
| `Context.MaxAnchorCount` / `MinAnchorCount` | 5 / 2 | L1 锚点数量（压缩时缩减） |
| `Context.DistillKeepMessages` | 3 | 蒸馏后保留的最近消息数 |
| `Context.DegradeResetSilenceMinutes` | 120 | 降级后静默多久彻底重置 |
| `Context.CacheAlertThreshold` | 0.6 | 缓存命中率告警阈值 |
| `Safety.MaxQpmPerGroup` | 15 | 单群发送频控（官方 20 留余量，勿调大） |
| `Llm.Model` | deepseek-v4-flash | 单一模型名 |
| `PersonaPath` / `BotName` | Persona/persona.md / 雨 | 人设文件与机器人名 |
| `Bot.OpenId` | 空 | 机器人群内 OpenID（全量消息 @ 判定，不填自动学习） |
| `Fun.EnableReplyYes` / `ReplyYesProbability` | true / 40 | 随机反驳是（消息=「是」时概率反驳「是你的头」） |
| `Fun.EnableReplyNo` / `ReplyNoProbability` | true / 16 | 随机反驳不（词表抬杠，词表存于 `sayno.json` 可热更新） |
| `SayNoPath` | sayno.json | 反驳不词表 JSON 路径（缺失自动生成默认，编辑保存即热重载） |
| `Fun.ReplyNoMemeUrl` / `ReplyNoMemeProbability` | hguofichp.cn:10086 / 30 | 反驳不命中时按该概率改用烂梗 API 回复（原版行为），失败自动回退词表；URL 置空可关闭 |
| `Fun.EnableRepeat` / `RepeatProbability` | true / 7 | 随机复读（延迟 30-80s，50% 加 desuwa～） |
| `Fun.EnableOsm` / `OsmProbability` / `OsmImages` | true / 2 / [] | 随机 OSM 梗图（URL 列表，空则禁用，见下） |
| `Fun.EnableReverseAt` / `ReverseAtProbability` | true / 70 | 反向艾特（@ 机器人时把 @ 弹回发送者，不阻断 AI 回复） |
| `Fun.EnableCallBrother` / `CallBrotherProbability` | true / 4 | 随机叫哥（@+名字截取+随机后缀，延迟 30s） |

**OSM 图片配置**：把梗图放入 `wwwroot/osm/`（如 `osm.jpg`、`osm.gif`、`newosm.jpg`），然后在 `Fun.OsmImages` 填公网访问地址（如 `http://你的域名/osm/osm.jpg`），或直接填任意公网图片 URL。未配置图片时该功能自动禁用。

**反驳不词表（sayno.json）**：首次运行自动生成默认词表文件（13 张表，字段名与原版 RainBOT 一致：`Trigger`、`TriggerBeforeNo`、`IgnoreTriggerAfterNo`、`IgnoreTriggerBeforeCan`、`TriggerAfterYes`、`WillNotSayNo`、`SayNoWords`、`SayDontHaveWords`、`SayNotYesWords`、`SayDontWords`、`SayWantWords`、`SayThinkWords`、`SaySpecialNoWords`）。直接编辑保存即热重载，也可用 `/admin sayno` 指令增删（写回 JSON）。

## 指令表（管理员为机器人自我维护的 OpenID 列表，与群管理员无关）

| 指令 | 权限 | 说明 |
| :--- | :--- | :--- |
| `/忘掉我` | 所有人 | 清除自己的画像 |
| `/status` | 管理员 | 本群运行状态 |
| `/fun` | 所有人 | 随机互动开关与概率一览 |
| `/admin list` | 管理员 | 列出全部参数 |
| `/admin set 参数 值` | 管理员 | 热改参数（即时生效、落库持久化） |
| `/admin mute [分钟]` / `/admin unmute` | 管理员 | 一键静默 / 解除 |
| `/admin stats` | 管理员 | 本群消息量与缓存命中率 |
| `/admin admin add|remove openid` | 管理员 | 维护管理员列表 |
| `/admin forget 短id` | 管理员 | 清除指定用户画像 |
| `/admin sayno list` | 管理员 | 列出反驳不全部词表 |
| `/admin sayno 表名 add\|remove 词` | 管理员 | 增删词表词条（写回 sayno.json，即时生效） |
| `/admin help` | 管理员 | 帮助 |

## 架构速览

```
WS 网关 ──▶ 去重(msg_id) ──▶ Channel 队列 ──▶ MessageProcessor
                                                     │  统计 → 输入风控 → 历史入库 → 命令 → 被动触发判定
                                                     ▼
                                              WorkflowRunner（全局串行）
                                                     │  BlockComposer 组装 A→F（前缀稳定）
                                                     │  WatermarkManager 水位治理（蒸馏/降级/重置）
                                                     │  ReActLoop（工具调用追加尾部）
                                                     ▼
                                        OutputFilter → SendQueue（15 qpm 滑动窗口）
WarmupScheduler（30s 扫描）──▶ 密度/沉默/频控判定 ──▶ 暖群工作流（允许画像沉淀）
```

**消息结构（缓存关键）**：`messages[0]=system(A 人设)` → `messages[1]=user(B 工具+C 群画像+D 锚点，静态)` → `messages[2]=user(E 历史，尾部增长)` → `messages[3]=user(F 当前)`。A~D 跨请求前缀不变 → DeepSeek 硬盘缓存命中（命中 ¥0.1/百万 vs 未命中 ¥1/百万）。

## 验收对照（PRD 第 7 节）

| 指标 | 实现与验证方式 |
| :--- | :--- |
| 缓存命中率 ≥ 90%（正常模式） | BlockComposer 前缀稳定（测试 `上下文组装_前缀稳定` 断言 A/B+C+D 不变）；`/admin stats` 与日志可查实时命中率 |
| @响应率 > 95%（扣除冷却） | msg_id 去重 + 冷却期队列化，被动触发链路全覆盖；测试覆盖冷却边界 |
| 暖群自然度 ≥ 60% | 决策 Prompt（Block F 暖群提示：话题/情绪）+ 互动增强（锚点 @ 仅在 30 分钟活跃窗口内） |
| 搜索硬错误率 ≤ 5% | 10 分钟话题缓存 + 失败兜底文案；ReAct 工具结果截断 |
| 153k 水位不崩溃不死循环 | WatermarkManager 三级治理（删 E 头部→缩 D→蒸馏+降级），蒸馏后重建上下文；测试覆盖蒸馏/降级/静默重置 |

## 测试

```bash
cd Tests && dotnet test
# 33 个用例：前缀稳定、水位压缩与降级恢复、冷却/频控/去重、输出风控、ReAct 死循环防护、指令解析
```

## 目录结构

```
Models/           QQ 协议模型 + 运行配置 + 内部消息模型
Services/QQ/      WebSocket 协议层、消息分发/队列/处理
Services/Trigger/ 双轨触发、群状态、发送频控
Services/Context/  Block 组装、历史、水位、蒸馏
Services/Llm/     DeepSeek 客户端、ReAct 循环、缓存监控
Services/Profile/ 画像（L0/L1/L2）、锚点、召回
Services/Tools/   工具注册表与执行器
Services/Search/  DuckDuckGo 搜索实现
Services/Fun/     随机互动（反驳/复读/OSM/反向艾特/叫哥）
Services/Safety/  输入/输出风控
Services/Commands/ 指令解析与执行
Persona/persona.md 人设（热重载）
deploy/           宝塔部署指南
Tests/            xUnit 测试
```
