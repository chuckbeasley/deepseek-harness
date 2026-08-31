# Agent Note: .NET 移植 Phase 6 wave 3——SDK 客户端与运行时 profile

Status: implemented

[English](2026-09-01-dotnet-port-phase6-sdk-client.md) | 中文

## 问题

运行时服务器可以通过传输层驱动，但移植没有客户端：TypeScript SDK 客户端（spawn 运行时子进程、讲 stdio 协议、把通知扇出到订阅、把子进程拆除到完全停稳）与高层 run API 在 .NET 侧没有对应实现，也没有随附 profile 通过进程的 stdio 启动 SDK 服务器。

## 决策

- `src/Dsh/Dsh.Sdk.Client` 移植 `packages/sdk/client`：`HarnessClient` 把运行时作为子进程 spawn 并拥有它——启动解析（`SdkLaunch.ResolveLaunch`，即 `resolveDshLaunch` 的移植：profile、有序 patch、home、进程 cwd、以及带 `DSH_HOME` 的环境覆盖；`.dll` 入口经 `dotnet` spawn；默认入口是当前可执行文件，其他宿主显式命名 `DshBin`）、带协议格式身份与消息 id 校验的类型化 `initialize`/`session/prompt` 表面（`SdkProtocolError`）、作为放弃的按请求超时（`RequestTimeoutError`；传输层丢弃挂起条目，而服务端工作会继续运行到关闭）、带 filter/queue/waiter/failure 语义的通知订阅、从 `subagent.started` 谱系边推导的会话树作用域、以及关闭阶梯——由 `shutdownTimeoutMs` 限界的尽力而为 `shutdown`，然后是带 EOF 宽限期的 stdin EOF，再然后是强制进程树 kill（Windows 没有优雅信号，因此阶梯与 TS 在 win32 上一样跳过 SIGTERM）。高层 `DeepSeekHarness`（记忆化的握手，在验证清理后重试、`session()` 句柄、返回自有 `RunResult` 活动间隔的 `RunAsync`）与协议格式辅助函数（`NormalizeInput`、`ValidatedSessionEvent`、`FinalResponse`）补全了这一表面。
- 服务器现在把每条会话记录投影到 SDK 协议格式信封（`{type, seq, timeMs, data}`——两个 SDK 客户端与 subagent 输出 fold 都读取的 TS `SessionEvent` 形态）。移植的会话记录保持载荷内联，因此投影剥掉信封字段并把其余内容包裹在 `data` 之下；客户端中的协议格式边界探针只校验它们读取的变体（`assistant/message` 内容、`turn/end` 原因），并把客户端进程未知的插件事件类型按其信封形态透传。
- `sdk` 运行时 profile 以 `dsh-sdk` 组合包交付（`sdkRuntime` spine 行在 console stdio 上启动 `SdkJsonRpcServer`，在客户端关闭 stdin 时退出——进程的退出就是客户端的阶梯一级）。profile 模板注册在 `ProfileTemplates` 下，subagent seam 的 `sdkSubagent` 行已把 `sdk` 命名为其默认子 profile，因此它现在运行真实的运行时。
- 客户端的入队回执是携带排队消息 id 的 `user/message` 会话事件：移植的 inbox seam 不记录 `agent/inbox/spliced` 事件（已记录的偏差），持久化的 splice 就是用户消息本身。

## 影响

.NET 消费方可以端到端地针对真实 harness 运行时运行 agent 轮次：11 个客户端套件证明了启动解析、协议格式语义、以及真实进程往返（握手身份、未知方法错误、带会话树作用域的轮次流式输出到空闲、超时放弃与关闭阶梯、以及带插件事件透传的 `DeepSeekHarness` 活动间隔）。54 个 console 套件全部绿灯；完整解决方案以 0 错误构建。ACP 服务器、钩子桥接与 python/ 退役在后面的 Phase-6 wave 中跟进。

## 备选方案

- 复用 subagent seam 的 `SdkChildConnection`：该驱动器是提供方内部的，带自己的失败事实包装；客户端 SDK 是带订阅、会话树作用域与高层 run API 的公开表面，因此移植保持二者分离。
- 在协议格式上内联发出移植的会话记录：SDK 协议格式约定是 TS 信封，两个 SDK 客户端与 subagent fold 都读取 `data`；服务器选择投影而不是改变约定。
