# 页面读取与 B 站文本链接

模型可调用 `open_page({"url":"https://example.com/article"})`，读取公开 HTML 的标题和页面声明的摘要。返回状态包括 `ok`、`blocked`、`timeout` 和 `unavailable`。资料来自外部页面，不应作为指令执行；标题和摘要不代表完整文章或视频内容。

已支持 B 站 `/video/BV…` 完整链接及 `b23.tv` 文本短链。完整链接直接查询视频元信息接口；短链逐跳校验，展开到 BV 视频地址后查询同一接口，不下载视频页。返回标题、UP 主、时长、分 P、简介和播放点赞数据，不包含视频画面、音频或字幕。

被动回复或随机插嘴通过触发判定后，自动读取本轮文本中的视频链接，优先当前消息、引用消息，再读取本批其他消息。指代性追问（如「这个咋样」）没有显式链接时，可跨发送者回溯群内最近视频链接。普通话题不回溯，图片回溯行为不变。资料及失败状态只注入本轮 Block F，不写入历史。默认两秒总预算、最多两条；超时取消网络读取并明确告知模型。

官 Q 图文卡片（`ark_type=tuwen`）读取显式 `fields.jump_url`，仅接受 B 站 BV 视频地址或 b23 短链；用于当前回复、批次合并、引用和最近链接回溯。卡片正文中的封面、来源图标不作为目标。官 Q 小程序卡片和 OneBot 卡片暂不接入。`open_page` 也通过同一 B 站读取器处理两种视频链接，其他公开 HTML 页面仍读取标题和摘要。

明确禁止微博、小红书、知乎及相关短链和素材域名；重定向目的地同样检查。不执行 JavaScript，不下载图片或视频，不携带 Cookie，不使用系统代理。仅允许 HTTP(S) 的 80/443 端口；实际连接前检查所有 DNS 结果并直接连接已验证的公网 IP。

默认配置（`Rain.Page`）：

```json
{
  "PrefetchEnabled": true,
  "BudgetSeconds": 2,
  "MaxLinksPerTurn": 2,
  "LinkLookbackSeconds": 180,
  "UsePublicDns": false,
  "ToolEnabled": true,
  "RequestTimeoutSeconds": 8,
  "CacheMinutes": 10,
  "MaxResponseKb": 512,
  "MaxChars": 400
}
```

`ToolEnabled` 仅在启动时决定工具是否注册，修改 appsettings 或环境变量后重启生效。`UsePublicDns` 也是启动期选项：系统 DNS 返回代理假 IP 时可显式启用，使用阿里公共 DNS 的 HTTPS JSON 解析接口，通过固定公网地址 223.5.5.5 连接，继续验证 TLS 证书、DNS 答案及目标 IP；不会放行假 IP 或内网地址。正常服务器可保持关闭。环境变量为 `Rain__Page__UsePublicDns=true`。

其余八项可在 WebUI「页面读取」或 `/admin set Page.<键> <值>` 调整。超时覆盖并发排队及整条跳转链；跳转最多五次。成功缓存默认十分钟，失败按 URL 缓存六十秒，缓存最多 512 项，多群共享最多四个并发读取。缓存保留完整摘要片段，工具返回时应用最新字符上限。B 站 BV 视频地址按 BV 号规范化，忽略分享追踪参数；其他 URL 保留查询参数，片段标识移除。

后续定制读取器实现 `ISiteReader` 并注册到依赖注入；注册表优先使用第一个匹配的定制读取器，否则使用 `OpenGraphReader`。站点专用 API 应继续复用同等安全的网络读取边界。
