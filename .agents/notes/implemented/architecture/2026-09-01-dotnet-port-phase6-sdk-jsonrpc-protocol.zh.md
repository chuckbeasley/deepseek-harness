# Agent Note: .NET 移植 Phase 6 wave 1——SDK JSON-RPC 协议

Status: implemented

[English](2026-09-01-dotnet-port-phase6-sdk-jsonrpc-protocol.md) | 中文

## 问题

Phase 6（SDK + ACP + hooks）在移植中还没有基础：运行时服务器、客户端 SDK 与 ACP 服务器都讲同一种共享 wire protocol——`packages/sdk/protocol` 的换行分隔 JSON-RPC 2.0 stdio 传输——而移植中还没有任何东西讲它。

## 决策

- `src/Dsh/Dsh.Sdk.Protocol` 移植协议包：`JsonRpcLineTransport` 从调用方拥有的输入流读取换行分隔帧，并写入调用方拥有的输出流。请求通过 `req_<uuid>` id 关联；缺失请求处理程序时回答 `-32601`，处理程序失败时以 `-32603` 附带消息回答，没有处理程序的通知被丢弃，格式错误的行被忽略。取消会移除挂起条目并以 `OperationCanceledException` 拒绝，但注册会一直存活到请求结算——发送后的取消仍会使挂起的请求失败（TS 在 resolve/reject 路径中移除其 abort 监听器，而不是在发送时移除；过早的 `using` 作用域注册是套件捕获的第一个实现 bug）。`Close` 与输入 EOF 以 `JSON-RPC transport closed` / `JSON-RPC input closed` 拒绝每个挂起请求。写入在同一个门禁下串行化并逐帧 flush，因此并发请求不会与通知交错（TS 依赖事件循环；已记录）。
- 协议格式约定以移植类型之上的记录形式交付：`initialize` / `session/prompt` / `shutdown` 方法名与协议格式稳定的服务器身份、握手/请求/结果记录、以及引用移植的 `ContentBlock`（Dsh.Llm）、`SessionEvent`（Dsh.Session）与 `SubagentStopReason`（Dsh.Subagent）的四个通知载荷。提示词内容联合类型（持久块 + 内联图片）已声明；其协议格式编解码器随运行时服务器 wave 一起加入。

## 影响

协议两端所讲的协议格式都已移植并验证：12 个协议套件（请求/响应往返、错误码、处理程序与通知接线、格式错误行容错、取消、关闭与 EOF 语义、以及类型约定）覆盖交叉的内存管道对。42 个 console 套件全部绿灯；完整解决方案以 0 错误构建。运行时服务器、客户端 SDK、ACP 服务器、钩子桥接与 python/ 退役在后面的 Phase-6 wave 中跟进。

## 备选方案

- 包装现成的 JSON-RPC 库：协议很小、协议格式已钉死、且是 stdio 形态；移植像对待其他每个 seam 一样保持其自有。
- 用 writer 任务做异步写入：门禁下的同步写入加逐帧 flush 更简单，且足以满足 stdio 与内存 fixture（测试前置数据）；如果消费方需要背压，再加入异步 writer。
