# Agent Note: .NET 移植 Phase 5 第 1 波——回环认证 fence 与 directoryPicker stub 落地

Status: implemented

[English](2026-09-01-dotnet-port-phase5-auth-fence-and-directory-picker.md) | 中文

## 问题

Phase 5 移植规范（§1.6）要求 Wave 1 就把认证 fence 形态「就位」——不可信 Host/Origin 返回 403，缺失或无效的浏览器会话返回 401，索引授权通过进程令牌交换或持久 cookie 完成，升级请求以纯 HTTP 401/403 拒绝——而完整的加固仍保持延后。移植端口还把 directoryPicker namespace 记录为 stub，应答 `directory-picker/unavailable`，因为原生目录选择器被延后。在这一轮之前，C# gateway、hub 与 mux 为任何能触达端口的人服务，没有任何 fence。

## 决策

`Dsh.Web.Host` 中的 `WebAuthFence` 在回环范围内忠实移植 TS 的 `browser-auth` + `api-request-trust` 组合：

- **信任 fence**（`IsTrustedRequest`）：Host 头必须指名回环 authority（`localhost`、`[::1]` 或任何 127/8 IPv4——TS 谓词），显式的 `sec-fetch-site: cross-site` 标记被拒绝，存在的 Origin 必须通过 URL 规范化等于 Host authority（`null` origin 被拒绝）。未移植任何 `trustedHosts` 部署 authority：回环绑定就是 Wave-1 表面（已记录的缩减）。
- **浏览器会话 cookie**（`IsAuthenticated`）：绑定 authority 的 HMAC-SHA256 签名 cookie（`dsh-auth-<base64url(sha256(authority))> = v1.<payload>.<signature>`，24 小时最长有效期，HttpOnly，SameSite=Strict），沿用 TS 的过期窗口与时序安全比较。签名密钥按主机实例持有——TS 将其持久化在凭据记录中，因此 cookie 能在主机重启后存活；C# 凭据 seam 没有记录 API，因此重启会使所有 cookie 失效，操作员需重新打开 `dsh web` 打印的 URL（已记录的缩减）。
- **启动令牌交换**（`AuthorizeIndex`）：`GET /?token=<launch>` 铸造 cookie 并 303 重定向到干净的 `/`；有效 cookie 提供索引；其余一切收到 TS 的 401 文本。中间件门控索引、gateway（`/api`）、hub、mux（升级在 WebSocket accept 之前以纯 HTTP 401/403 拒绝）与 Blazor circuit；静态资源保持开放（它们不携带机密，与 TS 只门控索引与 API 表面一致）。`dsh web` 在启动时打印认证后的 URL。

`directoryPicker` stub 注册全部三个动词（`pick` 需要原生能力，`list`/`createDirectory` 需要浏览能力），并以带能力详情的 `directory-picker/unavailable` 应答；创建名称语法（单个非空白段，不含 `/` 或 `\`）仍在能力拒绝之前强制，镜像 TS 的校验顺序。

## 影响

在 `dsh web` 上，gateway、hub、mux 与 shell circuit 现在都在 fence 之后；host 套件从 44 增至 59（真实 Kestrel 主机上的 10 个 fence 用例——401/403 词汇、交换、绑定 authority 与篡改的 cookie、hub 与 mux 门控——外加 4 个 directoryPicker 用例），全部通过，完整解决方案以 0 错误构建。针对真实 profile 的 HTTP smoke 验证了启动打印的 URL、401/403 fence、交换与被门控 API 的往返。`$events` waterfall/`$events/result` 结算机制保持延后，且有具名理由：移植端口尚不发出任何 waterfall 模式事件（approval/request 与 user-questions/request 未移植），因此在存在 waterfall 事件之前，该机制只会是死代码。

## 备选方案

- 通过凭据托管存储持久化签名密钥：C# 凭据 seam 没有 record/modifyRecord API（授权记录那一半是流程形态），因此诚实的回环方案按实例持有密钥并记录重启行为。
- 默认关闭 fence、由 web profile 选择开启：产品表面将依赖 bundle 配置标志来保障安全；fence 默认开启，承载测试的主机显式退出。
- 现在就用合成 waterfall 源移植 `$events/result` 结算：没有真实 C# 事件驱动它，该机制只会是无法在生产中测试的死代码；它随第一个 waterfall 事件（ask-user/approval 表面）一起落地。
