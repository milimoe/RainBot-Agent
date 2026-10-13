# B 站扫码登录

在 WebUI「配置」页的「B 站账号」卡片点击「扫码登录 / 重新登录」，使用 B 站 App 扫描二维码并在手机上确认。二维码在浏览器本地渲染，不发送给外部二维码服务。后端每两秒至多轮询一次，180 秒后结束会话；取消或离开页面会停止轮询。

登录确认后，后端从响应头 `Set-Cookie` 取得凭据，通过 `x/web-interface/nav` 验证账号确实已登录，再加密保存到 SQLite 的独立设置项。Cookie 和 refresh_token 不返回 WebUI、聊天或模型上下文。无法取得完整凭据或登录态检查失败时不会覆盖已有凭据。

身份优先级为 `Rain.Page.BilibiliCookies` 手动配置、扫码保存凭据、匿名设备 Cookie。已有 buvid3/buvid4 优先使用；缺失设备键从 spi 获取并在进程内缓存 24 小时。配置查询与日志中的手动 Cookie 已脱敏。

「检查登录状态」会区分有效、已失效、风控拦截与未知状态。已确认失效的自动凭据停止用于解析，重新扫码即可恢复。「清除扫码凭据」删除本地保存的自动凭据，并取消当前登录会话；不会注销 B 站其他设备，也不会删除手动配置。手动配置存在时可「恢复默认 Cookie 配置」，但如果默认值来自 appsettings 或环境变量，它仍会继续生效。

账号 Cookie 仅发送到 HTTPS 的 B 站域名。b23.tv 仅携带设备 Cookie，跨域重定向逐跳重新选择 Cookie。B 站读取独立串行，起始请求间隔至少 500 毫秒。带手动或登录身份遇 HTTP 412 时不立即重复相同请求；匿名身份可更换设备 Cookie 后延迟重试一次。持续 HTTP 412 或业务码 -352/-412 会进入 60 秒冷却，其他 BV 也暂停请求。登录、清除或凭据失效会改变缓存身份，使旧的 B 站失败缓存与冷却不再阻止新身份解析。

## 持久化与部署

加密使用 ASP.NET Core Data Protection，密钥存储在 SQLite 文件同级的 `bilibili-keys/` 目录。Windows 使用当前运行账号的 DPAPI 保护密钥；Linux 目录权限设为仅当前用户读写和访问，密钥文件依靠该目录的访问权限保护。部署、迁移和备份时应保存数据库及密钥目录；容器需要将数据库所在目录作为持久化卷。Windows 换运行账号或丢失密钥后，原凭据无法解密，需要重新扫码。

登录端点沿用现有 WebUI Enabled / Token 鉴权，并设置 `Cache-Control: no-store`。登录 HTTP 客户端禁用默认 URL 日志，禁止自动重定向与自动 Cookie 回放；限制响应大小和请求超时。

## 申请二维码报错

二维码申请接口在 `passport.bilibili.com`，二维码目标地址目前可使用 `account.bilibili.com`；后端明确允许这两个官方 HTTPS 主机。旧实现只允许 passport，会将合法的 account 二维码误判为格式异常并返回 502。

502 响应现在带有具体 `error` 与 `code`：`qr_format_invalid` 表示二维码域名或密钥格式异常；`upstream_http_error` 的 `upstreamCode` 是 B 站实际返回的 HTTP 状态；`upstream_api_error` 的 `upstreamCode` 是业务码；`upstream_not_json` / `upstream_invalid_json` 表示响应内容异常；`network_error` 的 `networkError` 区分 DNS、连接和 HTTPS 错误。不会返回上游正文、Cookie 或扫码密钥。服务端网络诊断日志仅记录接口路径、错误类别与异常类型。

## 当前范围

本期支持扫码登录、凭据恢复、状态检查和失效重登。refresh_token 已加密保存，暂未接入自动 Cookie 续期。登录状态检查由用户触发，未运行后台定时校验。登录态不能保证消除所有 412，仍保留原有 risk 兜底；实际部署出口的成功率需要真实账号验证。

测试使用模拟 HTTP 响应，覆盖响应头提取与编码保留、加密保存与恢复、扫码取消/过期/限频、风控与登录失效分流、设备身份补齐、短链跨域 Cookie 范围以及登录后失败缓存失效。
