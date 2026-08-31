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

> `BOT__APPID` / `BOT__SECRET` 仅作**旧版迁移兼容**（首次启动自动迁移为默认实例 `qq`）；新部署请直接在 WebUI「机器人」页按实例维护凭据（多实例架构，无全局网关配置）。`RAIN__PUBLICBASEURL` 为公网域名，只需设置一次，OSM 梗图等静态资源自动以「域名 + wwwroot 相对路径」对外提供。

> 机器人自身群内 OpenID 无需配置：开启「接收所有消息」后官方推送的 content 已去除 @ 机器人前缀，机器人首次被 @ 时会从事件中**自动学习**并落库，用于精确比对是否被 @（兼容历史 `<@!{bot_openid}>` 标签）。

3. 运行：

```bash
dotnet run                       # 开发（沙箱在 appsettings.Development.json）
dotnet publish -c Release -o publish && cd publish && dotnet RainBot.dll
curl http://localhost:8080/health   # {"status":"ok"} 即连接成功
```

## WebUI 控制台（React + Tailwind）

浏览器打开 `http://localhost:8080/webui/` 即可（根路径 `/` 自动跳转）。

| 页面 | 功能 |
| :--- | :--- |
| 消息 | **仿 NTQQ 聊天窗口**：会话列表 + 头像/气泡/昵称/日期分隔线；多实例时支持按实例下拉筛选会话；SSE 实时推送群消息与机器人回复；内置「WebUI 试聊群」可模拟群友发言——消息走真实处理链（统计→风控→历史→随机互动→LLM），机器人回复只显示在网页并落库回看，不发送到 QQ；真实群在右上角「⋯」菜单开启「试聊拦截」后同样可在网页试聊 |
| 机器人 | **多实例管理**：注册/编辑/启停机器人实例（QQ 官方 AppID+Secret 或 OneBot 接入），连接状态与 OneBot 接入地址；**QQ 官方实例另支持「自定义菜单 + 指令面板」在线配置**（单聊窗口底部按钮、按场景生效的指令面板，保存即调官方 API 生效） |
| 配置 | 全部可热改参数分组编辑（LLM/触发/上下文/风控/随机互动/通用），类型化控件 + 一键保存（落库即时生效）+ 覆盖标记与恢复默认；含公网域名（静态资源基址，只需设置一次） |
| 设置 | 人设 `persona.md` 在线编辑（保存热重载）、SayNo 词表增删（写回 sayno.json）、管理员 OpenID 维护、OSM 梗图目录（域名 + 自动扫描 `wwwroot/osm/`，路径零配置）；**机器人凭据请到「机器人」页按实例维护** |
| 状态 | QQ 网关连接状态 / 运行时长 / 队列深度 / 缓存命中率成本仪表 / **DeepSeek 账户余额（首次打开自动查询一次 + 手动刷新 + 最后刷新时间）** / **MCP 工具服务（各 server 连接状态与工具数）** / 每群统计，支持静默与重置上下文操作 |
| 日志 | 双视图：「日志」流（与 ILogger 同源，级别过滤 / 搜索 / 暂停 / 异常展开）+「工具调用」视图（内置与 MCP 工具调用记录：名称 / 参数 / 结果 / 耗时 / 成败，内存保留最近 200 条） |

### 鉴权

配置段 `Rain:WebUi`（appsettings 或环境变量）：

- `Enabled`（默认 `true`）：是否启用控制台；
- `Token`（默认空）：访问令牌。非空时所有 `/api/webui/*` 接口要求请求头 `X-WebUi-Token`（SSE 用 `?token=`），浏览器首次访问弹窗输入并保存在 localStorage。**公网部署务必设置**（环境变量 `RAIN__WEBUI__TOKEN`）。

`/health` 与静态页面保持公开，不影响宝塔探活与反代。

### 前端开发

```bash
cd webui
npm install
npm run dev      # Vite 开发服务器（5173，/api 自动代理到 8080）
npm run build    # 产物输出到 wwwroot/webui；Release 发布时自动执行（无 Node 环境则用仓库内已提交产物）
```

### 试聊（仿真）说明

- 内置「WebUI 试聊群」的机器人回复**永远**只推送到网页（拦截点在发送队列），并落库回看（`messages` 表 `user_openid = '$bot'`），不会发送到 QQ；
- 真实群需先开启「试聊拦截」才允许注入试聊消息；注入的消息会进入该群真实上下文缓存与历史统计，请谨慎使用；
- 关闭「试聊拦截」即恢复真实发送。已开启拦截的群会在状态页醒目提示。

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
| `PersonaPath` | Persona/persona.md | 人设文件（机器人实例可覆盖，留空用全局） |
| `Fun.EnableReplyYes` / `ReplyYesProbability` | true / 40 | 随机反驳是（消息=「是」时概率反驳「是你的头」） |
| `Fun.EnableReplyNo` / `ReplyNoProbability` | true / 16 | 随机反驳不（词表抬杠，词表存于 `sayno.json` 可热更新） |
| `SayNoPath` | sayno.json | 反驳不词表 JSON 路径（缺失自动生成默认，编辑保存即热重载） |
| `Fun.ReplyNoMemeUrl` / `ReplyNoMemeProbability` | 空 / 30 | 反驳不命中时按该概率改用烂梗 API 回复（**默认留空，留空不触发该分支**，始终用词表）；失败自动回退词表 |
| `Fun.EnableRepeat` / `RepeatProbability` | true / 7 | 随机复读（延迟 30-80s，50% 加 desuwa～） |
| `Fun.EnableOsm` / `OsmProbability` | true / 2 | 随机 OSM 梗图（**图片无需配置路径**：自动扫描 `wwwroot/osm/` 目录，URL = 公网域名 + 相对路径） |
| `PublicBaseUrl` | 空 | **公网域名（只需设置一次）**：所有静态资源（OSM 梗图等）以「域名 + wwwroot 相对路径」对外提供；留空则 OSM 自动禁用 |
| `DebugMode` | false | **调试模式**：开启后在每次对话输出末尾追加一行「x tokens, x tools」（输入+输出 token 总数、工具调用次数），排查成本与工具行为用 |
| `MarkdownReply` | false | **Markdown 回复**：开启后所有文本回复以 Markdown 消息（msg_type=2）发送到 QQ 网关而非纯文本（msg_type=0）；调试统计行显示为「> x tokens, x tools」块引用 |
| `Fun.EnableReverseAt` / `ReverseAtProbability` | true / 70 | 反向艾特（@ 机器人时把 @ 弹回发送者，不阻断 AI 回复） |
| `Fun.EnableCallBrother` / `CallBrotherProbability` | true / 4 | 随机叫哥（@+名字截取+随机后缀，延迟 30s） |

**OSM 图片配置（域名设置一次，路径零配置）**：把梗图放入 `wwwroot/osm/`（支持子目录，如 `osm.jpg`、`osm/shide/sd1.gif`），然后在 `PublicBaseUrl` 填入公网域名（如 `http://你的域名`）。OSM 发送时会自动扫描目录并拼接 `http://你的域名/osm/xxx.jpg`；未设置域名或无图片时该功能自动禁用。所有静态资源（OSM 图片、`wwwroot/` 下的任何文件）都复用这一个域名。

**机器人凭据（多实例，WebUI「机器人」页维护）**：每个实例在「机器人」页独立维护 AppID/Secret（QQ 官方）或 OneBot 接入配置；实例凭据存 `bot_instances` 表，改动即时生效。`Bot.AppId` / `Bot.Secret` 环境变量与设置页旧「QQ 网关」卡仅作旧版迁移兼容，不再提供写入入口。

**QQ 官方功能菜单与指令面板（WebUI「机器人」页在线配置）**：基于官方[菜单面板 API](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/menu-panel/)，按实例凭据直连官方接口（沙箱跟随实例设置）：
- **自定义菜单**：单聊窗口底部按钮，支持 `send_message`（发送消息）/ `link`（链接跳转，https://）/ `switch`（开关，用户切换后消息 ext 携带 `{switch_id}=1`）/ `menu`（子菜单，最多 5 项）；最多 10 个一级按钮，设置后对所有用户生效；
- **指令面板**：面板形式展示指令/链接，按 `c2c`（单聊）/ `group`（群聊）/ `channel`（文字子频道）/ `dm`（频道私信）场景生效；c2c/group 可指定用户/群（`specific`）生效，最多 20 个面板、每个最多 20 个元素；支持创建/编辑/删除与关联对象管理。

**反驳不词表（sayno.json）**：首次运行自动生成默认词表文件（13 张表，字段名与原版 RainBOT 一致：`Trigger`、`TriggerBeforeNo`、`IgnoreTriggerAfterNo`、`IgnoreTriggerBeforeCan`、`TriggerAfterYes`、`WillNotSayNo`、`SayNoWords`、`SayDontHaveWords`、`SayNotYesWords`、`SayDontWords`、`SayWantWords`、`SayThinkWords`、`SaySpecialNoWords`）。直接编辑保存即热重载，也可用 `/admin sayno` 指令增删（写回 JSON）。

## MCP 工具接入（Model Context Protocol）

机器人可把外部 MCP server 的工具接入 LLM 工具链（基于官方 [ModelContextProtocol.Core](https://www.nuget.org/packages/ModelContextProtocol.Core) SDK，支持 stdio 与 streamable HTTP 传输）。启动时一次性连接并注册，工具名统一为 `mcp__{server}__{tool}` 与内置工具隔离；单个 server 连接失败只告警跳过，不影响启动。**改配置需重启生效**（保证 Block B 前缀稳定、缓存命中率不受影响）。

配置段 `Rain:Mcp`（appsettings 或环境变量）：

```json
"Mcp": {
  "Enabled": true,
  "Servers": [
    {
      "Name": "everything",
      "Transport": "stdio",
      "Command": "npx",
      "Arguments": ["-y", "@modelcontextprotocol/server-everything"]
    },
    {
      "Name": "my-http-server",
      "Transport": "http",
      "Url": "https://example.com/mcp",
      "Headers": { "Authorization": "Bearer xxx" },
      "TimeoutSeconds": 60
    }
  ]
}
```

- `Transport`：`stdio`（本地进程，需 `Command`+`Arguments`）或 `http`（需 `Url`，可加 `Headers`）；
- Windows 下 stdio 若直接命令无法解析，可用 `Command: "cmd"`、`Arguments: ["/c", "npx", "-y", "..."]`；
- 工具数量与各 server 连接状态可在 `/health` 的 `mcp` 字段查看；
- 提示：MCP server 相当于授予机器人该 server 的全部工具权限，只接入可信来源。

## 多机器人与 OneBot11 接入

机器人以**实例**为单位管理（WebUI「机器人」页）：一个实例 = 一个机器人身份（QQ 官方 AppID 或 OneBot 机器人 QQ 号）+ 若干监听/发送通道。实例可随时增删改、启停，改动即时生效。

- **数据隔离**：内部群键 = `{实例Id}:{原始群号}`，群画像 / 用户画像 / 历史 / 统计按实例天然隔离，不同机器人互不串味；老数据已由一次性迁移归入默认实例 `qq`。
- **QQ 官方**：每个启用的 QqOfficial 实例一条 WebSocket 网关连接，凭据按实例维护（旧版 `Bot` 段自动迁移为默认实例）；支持群聊（@/全量双事件去重）与 **C2C 私聊**（`C2C_MESSAGE_CREATE` 入站 → 会话键 `{实例Id}:p{用户OpenID}`；出站 `POST /v2/users/{openid}/messages` 支持被动回复引用；官方 C2C 不支持发图，OSM 等自动降级纯文本；需在开放平台开通私聊权限）。
- **OneBot11**（go-cqhttp / NapCat / Lagrange 等，当前支持群聊）：
  - HTTP：本服务接收上报 `POST /onebot/v11/event/{实例Id}` + 调用实现的 HTTP API 发消息；
  - WS 正向：本服务主动连接 OneBot 实现；WS 反向：OneBot 实现连入 `WS /onebot/v11/ws/{实例Id}`；
  - 发送通道按优先级选路（WS 会话优先，HTTP 兜底）；@ 用 at 消息段、引用回复用 reply 段；
  - 管理页会给出可直接复制的上报地址与反向 WS 地址；机器人 QQ 号收到首条事件自动学习，无需手填；
  - OneBot11 无原生 Markdown 消息 → `MarkdownReply` 在该平台自动降级为纯文本。
- **管理员名单**为全局（两平台 ID 格式不同，天然不冲突）；`/health` 的 `bots` 字段透出各实例连接状态。

## 指令表（管理员为机器人自我维护的 OpenID 列表，与群管理员无关）

> 指令**无需 @ 机器人**，群里直接发送即可（@ 发送同样有效）；管理员指令按权限放行，非管理员会收到提示。全量消息模式下回复不携带 msg_id（官方约束：被动回复仅适用于 @ 事件消息），以主动消息形式发送。

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
WS 网关 ──▶ 双事件去重（@/全量同 msg_id 协调，@ 语义绝不丢失）──▶ Channel 队列 ──▶ MessageProcessor
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

**双事件去重（@ 判定权威）**：开启「接收所有消息」后，@ 消息会同时推送 `GROUP_AT_MESSAGE_CREATE` 与 `GROUP_MESSAGE_CREATE`（同 msg_id，官方注明相同 msg_id 可能重复推送）；部分环境下 @ 事件不再单独推送，此时以全量事件 payload 的 **`mentions` 数组（`is_you=true` 即 @ 了本机器人）** 为权威信号，content 中的 @ 标签（新格式 `<@openid>` / 历史格式 `<@!openid>`）作为兜底。分发层按事件类型分别去重，并保证 @ 语义绝不丢失：@ 事件先到则全量事件跳过；全量事件先到则 @ 事件仍补执行风控→指令→触发→工作流（跳过已完成的统计/历史，避免重复计数与重复入库）。机器人 openid 无需配置，自动从 mentions/@ 事件学习。

**消息结构（缓存关键）**：`messages[0]=system(A 人设)` → `messages[1]=user(B 工具+C 群画像+D 锚点，静态)` → `messages[2]=user(E 历史，尾部增长)` → `messages[3]=user(F 当前)`。A~D 跨请求前缀不变 → DeepSeek 硬盘缓存命中（命中 ¥0.1/百万 vs 未命中 ¥1/百万）。

## 验收对照（PRD 第 7 节）

| 指标 | 实现与验证方式 |
| :--- | :--- |
| 缓存命中率 ≥ 90%（正常模式） | BlockComposer 前缀稳定（测试 `上下文组装_前缀稳定` 断言 A/B+C+D 不变）；`/admin stats` 与日志可查实时命中率 |
| @响应率 > 95%（扣除冷却） | 双事件去重（@/全量同 msg_id 协调，@ 语义优先）+ 冷却期队列化，被动触发链路全覆盖；测试覆盖冷却边界与双事件到达顺序 |
| 暖群自然度 ≥ 60% | 决策 Prompt（Block F 暖群提示：话题/情绪）+ 互动增强（锚点 @ 仅在 30 分钟活跃窗口内） |
| 搜索硬错误率 ≤ 5% | 10 分钟话题缓存 + 失败兜底文案；ReAct 工具结果截断 |
| 153k 水位不崩溃不死循环 | WatermarkManager 三级治理（删 E 头部→缩 D→蒸馏+降级），蒸馏后重建上下文；测试覆盖蒸馏/降级/静默重置 |

## 测试

```bash
cd Tests && dotnet test
# 129 个用例：前缀稳定、水位压缩与降级恢复、冷却/频控/双事件去重（@/全量协调）、输出风控、
# ReAct 死循环防护、指令解析、发送队列包体与失败重试（5xx 退避重试/429 不重试/超限丢弃）、
# MCP 工具桥接（命名/Schema/注册顺序）与工具调用记录器、多机器人实例（键规则/私聊入站/发送统计）、
# C2C 私聊（入站解析/去重/出站降级）
```

## 后续计划（Roadmap）

Roadmap 1-4 已全部完成（C2C 私聊 / 聊天页实例筛选 / 发送失败重试 / MCP 工具调用统计与状态页透出）。新需求将另行规划。

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
Services/WebUi/   WebUI 控制台 API（事件总线/试聊拦截/SSE）
Persona/persona.md 人设（热重载）
webui/            WebUI 前端源码（React + Tailwind + Vite）
wwwroot/webui/    WebUI 前端构建产物（UseStaticFiles 直接托管）
deploy/           宝塔部署指南
Tests/            xUnit 测试
```
