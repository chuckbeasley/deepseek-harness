# Agent Note: .NET 移植 Phase 6 wave 2——SDK 运行时服务器

Status: implemented

[English](2026-09-01-dotnet-port-phase6-sdk-runtime-server.md) | 中文

## 问题

协议传输层已移植，但没有任何东西为它服务：进程外 SDK 需要运行时服务器——在 JSON-RPC 传输层上托管一个已启动的 harness 上下文、校验并记录 SDK 路由、惰性创建 agent+session 配对、并把运行时的生命周期流回给客户端的组件。

## 决策

- `src/Dsh/Dsh.Sdk.Server` 移植 TS 的 `HarnessSdkJsonRpcServer`：`initialize` 校验握手（格式错误的推理强度或非正 token 上限大声失败；未拥有的 `deepseek-official` 路由像 spine 的 deepseek 行一样挂载 DeepSeek 适配器；任何其他未注册提供方大声失败），`session/prompt` 以记录的路由在移植的 agent loop（智能体循环）上惰性创建 agent+session 配对（路由的 `maxTokens` 流入 `AgentOptions`）并排队持久化的用户消息，`shutdown` 对服务器拥有的会话、适配器与订阅执行 dispose（资源释放），而周围上下文继续运行。`session.event` 与 `session.status` 从上下文自身的事件经传输层实时流式输出。
- 提示词块协议格式编解码器在协议项目中交付：`type: "image"` 的块解码为内联图片成员，其他任何内容都通过会话日志的多态 `ContentBlock` 编解码器解码（持久日志讲的同一种协议格式（wire format）），反向写显式完成。
- 已记录的缩减项，每项均具名：`subagent.started`/`subagent.finished` 通知等待移植的 subagent 生命周期事件与父谱系（会话 header 不携带任何此类信息）、内联图片提示词块在附件 seam 准入 base64 之前被拒绝（它只从路径摄取）、以及推理强度通过校验但没有 `AgentOptions` 席位（agent loop 的调用配置有；经选项把它接进来是 loop-seam 变更）。

## 影响

SDK 客户端可以握手、提示并观察真实的 harness 运行时：8 个服务器套件证明了握手校验、回退适配器挂载、在移植的 agent loop 上带真实 mock 轮次的惰性会话创建、实时通知、图片缩减、关闭语义、以及未知方法错误。43 个 console 套件全部绿灯；完整解决方案以 0 错误构建。客户端 SDK、ACP 服务器、钩子桥接与 python/ 退役在后面的 Phase-6 wave 中跟进。

## 备选方案

- 通过 web 网关提供服务：SDK 协议按设计就是 stdio 行 JSON-RPC；传输层是唯一的协议格式，服务器组合与网关相同的 seam。
- 现在就把推理强度加入 `AgentOptions`：agent loop 的调用配置已携带该席位；选项管道是 loop-seam 变更，更适合与一个在校验之外确实需要它的消费方一起做。
