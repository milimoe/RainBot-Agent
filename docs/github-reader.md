# GitHub 链接自动解析

`GitHubLinkPreprocessor` 实现 `IMessagePreprocessor`，已在启动 DI 中注册。有效聊天消息自动读取公开 GitHub 链接；通过回复触发判定时，将文本资料放入本轮 Block F，LLM 无需调用工具。资料不写入历史。`GitHubReader` 同时实现 `ISiteReader`，所以现有 `open_page` 也复用同一读取能力和缓存。

| 链接 | 实际读取内容 |
| --- | --- |
| `https://github.com/owner/repo` | 仓库描述、语言、Star/Fork、Issue/PR 数量、默认分支、许可证、归档状态、更新时间及 README 文本片段 |
| `.../issues/123` | 标题、状态、作者、时间、评论数和主帖正文 |
| `.../pull/123` | 标题、描述、状态、草稿/合并状态、源/目标分支和变更统计 |
| `.../commit/abcdef123` | SHA、提交说明、GitHub 作者及最多 20 个变更文件名 |
| `.../releases` 或 `.../releases/latest` | 最新正式 Release 的版本、名称、作者、时间及发布说明 |
| `.../releases/tag/v1.0` | 指定版本发布说明 |
| `.../blob/main/src/app.cs` | 指定 ref 的 UTF-8 文本文件片段 |

不读取评论、评审或完整 diff，不扫描整个仓库，不执行代码，也不下载 Release 附件。文件读取只支持 UTF-8 文本，解码后最多 512 KB，同时受单次响应体大小限制；正文片段最多保存 6000 字符，注入时再应用 `GitHub.MaxChars`。文件链接支持单段 branch/tag/SHA；含 `/` 的 ref 需要在 ref 段编码为 `%2F`，普通的 `blob/feature/fix/...` 会按 `feature` 作为 ref 处理，请使用 commit 固定链接避免歧义。行号、评论锚点和分享查询参数会移除，读取主帖或文件片段，不会定位该评论/行号。Discussions、Actions、目录、用户主页、Gist、Enterprise 域名和附件下载链接暂不自动处理。

来源顺序为当前消息、引用正文、本批其他消息（从新到旧）。相同来源去重，`www.github.com`、HTTP、仓库的 `.git` 后缀、大小写、分享参数和锚点会规范化；仅 owner/repo 大小写归一，文件路径和 ref 保留大小写。不跨历史回溯 GitHub 链接，也不从卡片展示正文猜测目标地址。

独立配置位于 `Rain.GitHub`：

```json
{
  "Enabled": true,
  "SendParsedContent": true,
  "BudgetSeconds": 3,
  "MaxLinksPerTurn": 2,
  "MaxChars": 3000
}
```

五项均可在 WebUI「GitHub 解析」或 `/admin set GitHub.<键> <值>` 热修改。实际限制分别为 1–10 秒、1–4 条、200–4000 字符；配置键使用以上大小写。`Page.PrefetchEnabled` 只控制 B 站，GitHub 由自己的 `Enabled` 控制。GitHub 读取复用 `Page.RequestTimeoutSeconds`、`Page.CacheMinutes`、`Page.MaxResponseKb` 与启动期 DNS 选项；失败按 URL 缓存 60 秒，共享缓存上限 512 项、网络读取并发上限 4。`open_page` 输出仍遵循它自己的摘要长度限制。

`SendParsedContent` 默认开启：自动预处理成功后，直接向触发会话发送一条本轮 GitHub 资料消息，包含标题、正文片段和来源；多链接合并，失败项不发送，不经过 LLM。`Rain.MarkdownReply=true` 时使用标题、引用正文和原链接排版，否则使用纯文本；发送正文总上限 1800 字符，超出标记截断。关闭本项只关闭额外资料消息，资料仍注入本轮 LLM 上下文，LLM 回复继续保留。冷却中或未命中插嘴概率时仍自动解析并直接发送，只有 LLM 回复受触发判定限制；`open_page` 工具调用不额外发送资料消息。

直接发送现使用独立元信息卡片：仓库展示名称、简介、语言、Star/Fork、Issue/PR 数量、默认分支、许可证、状态和北京时间更新日期；其他资源展示类型、标题及对应作者/状态/统计等元信息。发送卡片不含图片、README、Issue/PR 正文、发布说明或文件源码；这些实际解析文本仍提供给 LLM。卡片数据保存在 `DisplayCard`，不参与工具 JSON 序列化，与模型文本分开。

通过公开 REST API 匿名访问，不需要 Token、不携带登录 Cookie。GitHub 的匿名请求存在限流；403/429 会明确返回限流或拒绝访问，404 会说明资源不存在、私有或文件 ref/路径不支持。某条链接失败不影响其他链接；总预算到期返回 `timeout`，取消实际网络请求；外部取消继续传播。README 缺失、返回异常或格式无法读取时，保留已取得的仓库元信息。

网络请求复用 `PageReaderHttp` 和 `PageUrlPolicy` 的 DNS、公网 IP、端口、响应大小及跳转限制。GitHub API 请求和跳转仅允许 HTTPS 的 `api.github.com:443`，不跟随响应中的下载地址，正文视为外部非可信数据。

如果本地代理的 Fake-IP DNS 把 `api.github.com` 解析为 `198.18.x.x` 等保留地址，读取会被安全检查拒绝。可在实际运行目录的 appsettings 中设置 `Rain.Page.UsePublicDns=true`（或环境变量 `Rain__Page__UsePublicDns=true`），重启生效。该选项通过现有固定公网 DoH 解析取得真实 IP，继续验证公网地址和 TLS，不放行代理假 IP；正常服务器无需开启。

API 格式依据 GitHub 官方文档：[仓库](https://docs.github.com/en/rest/repos/repos)、[Contents/README](https://docs.github.com/en/rest/repos/contents)、[Issue](https://docs.github.com/en/rest/issues/issues)、[PR](https://docs.github.com/en/rest/pulls/pulls)、[Commit](https://docs.github.com/en/rest/commits/commits)、[Release](https://docs.github.com/en/rest/releases/releases)。
