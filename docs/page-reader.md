# 页面读取与 B 站文本链接

模型可调用 `open_page({"url":"https://example.com/article"})`，读取公开 HTML 的标题和页面声明的摘要。返回状态包括 `ok`、`blocked`、`timeout`、`unavailable` 和 `risk`（B 站风控拦截，详见下文兜底）。资料来自外部页面，不应作为指令执行；标题和摘要不代表完整文章或视频内容。

已支持 B 站 `/video/BV…` 完整链接及 `b23.tv` 文本短链。完整链接直接查询视频元信息接口；短链逐跳校验，展开到 BV 视频地址后查询同一接口，不下载视频页。返回标题、UP 主、时长、分 P、简介和播放点赞数据，不包含视频画面、音频或字幕。

有效聊天消息自动读取本轮文本中的视频链接，冷却中或未命中插嘴概率时也会解析。读取优先当前消息、引用消息，再读取本批其他消息；只有本批新消息自身携带的链接按发送开关出卡，引用和历史回溯仅供模型读取。指代性追问（如「这个咋样」）没有显式链接时，可跨发送者回溯群内最近视频链接。普通话题不回溯，图片回溯行为不变。资料及失败状态只注入本轮 Block F，不写入历史。默认两秒总预算、最多两条；超时取消网络读取并明确告知模型。

官 Q 图文卡片（`ark_type=tuwen`）读取显式 `fields.jump_url`，仅接受 B 站 BV 视频地址或 b23 短链；用于当前回复、批次合并、引用和最近链接回溯。卡片正文中的封面、来源图标不作为目标。官 Q 小程序卡片和 OneBot 卡片暂不接入。`open_page` 也通过同一 B 站读取器处理两种视频链接，其他公开 HTML 页面仍读取标题和摘要。

明确禁止微博、小红书、知乎及相关短链和素材域名；重定向目的地同样检查。不执行 JavaScript，不下载图片或视频，不使用系统代理。仅允许 HTTP(S) 的 80/443 端口；实际连接前检查所有 DNS 结果并直接连接已验证的公网 IP。默认不携带 Cookie；B 站支持手动配置或 WebUI 扫码登录，缺失的 `buvid3`/`buvid4` 从 spi 补齐，设备 Cookie 缓存 24 小时。账号凭据只发往 HTTPS B 站域名，b23 短链只携带设备 Cookie，跳转后重新检查发送范围。带登录或手动身份遇 412 不重复相同请求；匿名模式可重新领取设备 Cookie 后延迟重试一次。持续风控进入 60 秒冷却。登录与凭据变化会使旧 B 站缓存失效，详见 [B 站扫码登录](bilibili-login.md)。

B 站返回 HTTP 412 或错误码 -352/-412 时，读取结果为 `risk` 状态：资料照常注入本轮上下文并说明被风控拦截，不得猜测视频内容。若开启了 Markdown 回复（`Rain.MarkdownReply`）且来源能解析出 BV 号（`b23.tv` 短链在风控发生在展开跳转阶段时无法提供 BV 号，则不触发），仅当来源为本批新消息自身携带的链接时，额外直接发送一条「B 站链接解析失败」提示，附「立即播放」（`player.bilibili.com` 内嵌播放器，自动播放第一 P）与「原网页」可点击链接，与成功卡片的播放入口格式一致；该消息不进入历史与上下文。纯文本模式不发送兜底消息。

默认配置（`Rain.Page`）：

```json
{
  "PrefetchEnabled": true,
  "SendParsedContent": true,
  "BudgetSeconds": 2,
  "MaxLinksPerTurn": 2,
  "LinkLookbackSeconds": 180,
  "UsePublicDns": false,
  "ToolEnabled": true,
  "RequestTimeoutSeconds": 8,
  "CacheMinutes": 10,
  "MaxResponseKb": 512,
  "MaxChars": 400,
  "BilibiliCookies": ""
}
```

`ToolEnabled` 仅在启动时决定工具是否注册，修改 appsettings 或环境变量后重启生效。`UsePublicDns` 也是启动期选项：系统 DNS 返回代理假 IP 时可显式启用，使用阿里公共 DNS 的 HTTPS JSON 解析接口，通过固定公网地址 223.5.5.5 连接，继续验证 TLS 证书、DNS 答案及目标 IP；不会放行假 IP 或内网地址。正常服务器可保持关闭。环境变量为 `Rain__Page__UsePublicDns=true`。

其余十项可在 WebUI「页面读取」或 `/admin set Page.<键> <值>` 调整（`BilibiliCookies` 含分号与等号，建议直接改 appsettings 或环境变量 `Rain__Page__BilibiliCookies`）。超时覆盖并发排队及整条跳转链；跳转最多五次。成功缓存默认十分钟，失败按 URL 缓存六十秒，缓存最多 512 项，多群共享最多四个并发读取。缓存保留完整摘要片段，工具返回时应用最新字符上限。B 站 BV 视频地址按 BV 号规范化，忽略分享追踪参数；其他 URL 保留查询参数，片段标识移除。

`SendParsedContent` 默认开启：B 站预处理完成后，仅对本批新消息自身携带的链接发送卡片，按实际来源消息合并成功资料并关联原消息，无需 LLM 调用工具。Markdown 模式使用标题、引用正文和原链接，否则发送纯文本。正文总上限 1800 字符，超出标记截断；不受 LLM 两行限制。只包含实际视频元信息，仍标注未观看视频；失败项不进入成功卡片，但本批新消息的 `risk` 风控项按上文兜底逻辑额外发送。引用回复与后续单独追问不重复出卡，也不重复发风控提示。关闭本项只关闭资料消息，资料仍注入本轮上下文。LLM 原回复流程保留，`open_page` 工具调用不额外发送。

直接发送现使用视频卡片：Markdown 模式展示封面、播放/弹幕/点赞、时长、UP 主、北京时间发布日期、标题、实际简介，以及「立即播放」「原网页」链接。封面只接受 B 站 `*.hdslb.com` 的 HTTPS 地址；机器人不下载封面，QQ 平台按 [Markdown 图片协议](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/type/markdown.html) 转存显示。普通文本模式不嵌入图片；多链接分摊预算，空间不足时省略封面。没有字幕或视频内容，因此不生成视频 AI 总结。模型侧仍保留未观看视频的说明，卡片数据不注入模型文本。

后续定制读取器实现 `ISiteReader` 并注册到依赖注入；注册表优先使用第一个匹配的定制读取器，否则使用 `OpenGraphReader`。站点专用 API 应继续复用同等安全的网络读取边界。

卡片「立即播放」使用 `https://player.bilibili.com/player.html?bvid=<对应BV号>&p=1&autoplay=true`，打开第一 P 并请求自动播放；「原网页」仍使用规范化的视频页面地址。

回复前自动解析扩展请实现 `IMessagePreprocessor`；现有 `LinkPrefetcher` 已接入通用管线。接口、注册方式和执行边界见 [自动消息预处理扩展](message-preprocessing.md)。

