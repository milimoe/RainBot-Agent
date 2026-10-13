# B 站登录态解析调研

调研日期：2026-10-12。范围：现有 BV 视频元信息解析与 b23 短链；本次仅调研，未修改运行代码，也未使用真实账号进行验证。

## 结论

建议增加 Web 扫码登录与凭据管理。GitHub 已有登录、Cookie 注入、续期的实现与协议记录，可以在现有 C# 服务内接入，不必增加 Python/Java 常驻服务。

“有效登录态能改善部分接口的可用性”有社区案例支持；“登录后稳定绕过所有 412”没有充分证据。接口、出口 IP、设备身份和请求频率仍可能影响结果。其他项目的动态接口经验不能直接证明本项目 `x/web-interface/view` 的表现，需要在部署出口验证。

## 可参考案例

| 来源 | 可参考内容 | 使用边界 |
| --- | --- | --- |
| [bilibili-api-collect：Web 扫码登录](https://github.com/bilibili-plugins/bilibili-api-collect/blob/master/docs/login/login_action/QR.md) | 二维码申请、轮询状态、登录 Cookie 与 refresh_token | 社区协议记录，接入前核实实际响应 |
| [bilibili-api-collect：Cookie 刷新](https://github.com/bilibili-plugins/bilibili-api-collect/blob/master/docs/login/cookie_refresh.md) | 检查刷新需求、获取 refresh_csrf、更新与确认 | 刷新机制与请求风控是不同问题 |
| [abcLiyew/BiliBili-API](https://github.com/abcLiyew/BiliBili-API) | 扫码登录后注入 Cookie、凭据校验、设备 Cookie 优先级、412 排障 | README 的运行结果是作者报告，未在本项目复现 |
| [Iseenope：login.ts](https://github.com/Iseenope/bilibili-mcp-server/blob/main/src/api/login.ts) | 二维码状态与轮询实现 | 核对到的版本从 data.cookie_info 取凭据；Web 文档记录的是 Set-Cookie，不能直接照搬 |

Java 案例报告，动态接口补充匿名设备 Cookie 后仍可能返回业务码 -352，需要真实登录 Cookie；同时其 README 也记录了凭据已携带仍出现 412 的排障情况。因此登录态是可行的改善措施，不能作为成功保证。[来源](https://github.com/abcLiyew/BiliBili-API)

## 现有项目接入点

- `Services/Page/BilibiliCookieProvider.cs`：已有 `Rain.Page.BilibiliCookies` 手动配置优先、匿名 spi 领取与 24 小时缓存；没有扫码登录、登录态校验或续期。手动配置只有 SESSDATA 时会跳过 spi，不会自动补齐 buvid。
- `Services/Page/BilibiliReader.cs`：调用 `x/web-interface/view`；HTTP 412 时清匿名缓存并立即重试一次。若使用手动 Cookie，Invalidate 不改变实际凭据，重试仍携带相同 Cookie。业务码 -352/-412 直接返回 risk。
- `Services/Page/PageReaderHttp.cs`：已设置 UA、Referer 并注入 Cookie。目前也向 b23.tv 发送整串 Cookie；增加登录态时应按域过滤，仅给匹配的 B 站域发送账号凭据，短链只携带允许的设备信息。
- `Services/Page/PageService.cs`：最多四个页面请求并发，失败缓存 60 秒。登录或凭据更新后应清除 B 站失败缓存，避免继续显示旧的风控结果。
- `Services/WebUi/ConfigMetadata.cs`：目前没有 B 站凭据管理入口。

## 建议功能设计

1. WebUI 增加“扫码登录、登录状态、重新登录、清除本地凭据”。后台申请二维码，用户用 B 站 App 扫码并确认；登录轮询独立于聊天解析的短时预算。
2. 申请接口：`GET https://passport.bilibili.com/x/passport-login/web/qrcode/generate`。二维码有效期按社区文档为 180 秒，超时结束任务。[协议](https://github.com/bilibili-plugins/bilibili-api-collect/blob/master/docs/login/login_action/QR.md)
3. 轮询接口：`GET https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=…`。检查外层 code 与 data.code：86101 未扫码，86090 待确认，86038 已过期，0 成功。建议约两秒轮询一次，支持取消。
4. 登录成功读取每条响应头 Set-Cookie，保存 SESSDATA、bili_jct、DedeUserID、DedeUserID__ckMd5，同时保存正文 refresh_token。使用 Cookie 解析器处理域与过期属性，不对 SESSDATA 反复 URL 编解码。核心凭据缺失时不能标记为可用。[协议](https://github.com/bilibili-plugins/bilibili-api-collect/blob/master/docs/login/login_action/QR.md)
5. 扩展 Cookie Provider：手动配置优先，其次自动登录凭据，再次匿名模式；按键合并设备 Cookie，已有 buvid 优先且保持稳定，缺少时补齐。登录 HTTP 客户端与通用页面客户端分开，限制访问 B 站固定域名；避免自动 Cookie 容器与手写 Cookie 头重复叠加。
6. 凭据仅保存在服务端受保护存储，WebUI 返回脱敏状态；不将凭据或含凭据的登录跳转 URL 写入日志、聊天消息或模型上下文。
7. 首期提供凭据校验和失效重登，后续加入按需刷新。刷新流程为检查 cookie/info → 生成 correspond 路径 → 获取 refresh_csrf → cookie/refresh → confirm/refresh；更新时需要新 Cookie 与旧 refresh_token，持久化更新应串行、原子化。[刷新协议](https://github.com/bilibili-plugins/bilibili-api-collect/blob/master/docs/login/cookie_refresh.md)
8. 412/-352/-412 按风控处理，不能一律认定为登录过期。给 B 站请求增加独立限流、退避和短期熔断；保留 risk 兜底，避免每条消息都触发相同凭据的立即重试。凭据更新后使 B 站失败缓存失效。

## 验证方案

在实际部署机器、同一出口、同一批公开 BV 上，以低频请求分别比较：匿名 buvid、手动完整登录 Cookie、自动扫码获得并补齐设备 Cookie 的登录态。分开记录 HTTP 412、业务码 -352/-412、登录失效、成功与超时，只记录 Cookie 键名及脱敏状态。

分时段重复观测，并验证服务重启后凭据恢复、二维码过期/取消、账号登出、凭据更新后的缓存失效，以及 b23 跳转不泄露账号 Cookie。只有自动登录链路确实取得有效凭据且目标接口成功率改善，才能认定功能有效；单次成功不足以支持“稳定绕过 412”。

实现测试应覆盖：Set-Cookie 提取与编码保留、设备 Cookie 合并优先级、凭据存储与脱敏、刷新并发互斥、风控与登录失效分流、跨域重定向的 Cookie 范围。
