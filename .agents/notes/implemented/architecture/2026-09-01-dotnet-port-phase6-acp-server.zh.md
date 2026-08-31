# Agent Note: .NET 移植 Phase 6 wave 4——ACP 服务器与运行时 profile

Status: implemented

[English](2026-09-01-dotnet-port-phase6-acp-server.md) | 中文

## 问题

移植尚未为可信的程序化客户端提供仅面向自动化的表面：ACP（Agent Client Protocol）服务器（持久会话、标准配置、已提交更新、取消、一次性权限决策）在 .NET 侧没有对应实现，也没有随附 profile 启动它。

## 决策

- `src/Dsh/Dsh.Acp` 在移植的 JSON-RPC 传输层上移植 `@deepseek-ai/dsh-acp`（ACP 的协议格式（wire format）就是同一种换行分隔的 JSON-RPC 2.0）：完整的方法面（带能力声明与协议格式稳定身份 `deepseek-harness-acp`/`0.0.1` 的 `initialize`、`authenticate`、`session/new`、带 base64url keyset cursor 的 `session/list`、经 agent loop（智能体循环）恢复流程的 `session/resume`、`session/close`、`session/setConfigOption`、`session/prompt`、以及 `session/cancel` 通知）、有序的已提交更新（消息/思考分片、工具生命周期、每条具体更新上的 `sessionUpdate` 判别符）、以及审批桥接——`tools/pre-execute` 拦截点询问组合应答者，`approval/request` waterfall（瀑布式事件）把所属会话的一次性决策作为 `session/requestPermission` 路由给客户端。
- 传输层现在并发分派传入请求：处理程序运行时 reader 必须继续读取，否则处理程序自己的出站请求（`requestPermission` 桥接）及其背后的通知永远无法被处理。响应保持与 id 关联。
- `acp` 运行时 profile 以 `dsh-acp` 组合包交付（`approval` + `acpRuntime` 行在 console stdio 上运行，stdin EOF 时退出；路由与 headless/web 行一样遵循 `DEEPSEEK_API_KEY`）。
- 会话存储的 `Remove` 被恢复流程严格按其文档描述的方式调用（在其存储的日志重新水合新会话之前释放该身份）。
- 已记录的缩减项，每项均具名：MCP 挂载在移植具备 MCP 客户端 seam 之前会被拒绝，内联图片提示词等待附件准入 seam，模型选项只公布会话的固定路由（agent loop 在创建时读取 `AgentOptions`；没有 catalog 或推理强度），用量更新等待移植的 token 计量器，持久化 header 不携带 origin/parent/cwd，因此恢复检查仅检查存在性、列表的工作区过滤为空操作，提示词取消仅通过 `session/cancel` 流转（移植的传输层没有服务端请求中止）。

## 影响

ACP 客户端可以创建、提示、观察、批准、取消、列出、恢复并关闭真实的 harness 会话：13 个 ACP 套件端到端地证明了编解码器与模型控制状态、身份与能力、校验与缩减项、已提交更新流、一次性审批桥接、确定性取消、列表 cursor 分页、恢复流程、以及真实的 `acp` profile 在进程 stdio 上的完整链路。45 个 console 套件全部绿灯；完整解决方案以 0 错误构建。钩子桥接与 python/ 退役在剩余的 Phase-6 wave 中跟进。

## 备选方案

- 整体移植 `@agentclientprotocol/sdk` 的协议格式（wire format）层：移植的传输层已经在讲同一种换行分隔的 JSON-RPC 2.0，因此 ACP 服务器直接用其需要的协议格式记录与常量接线。
- 按会话的持久化挂载：spine 的 sessionPersistence 行挂载整个存储，因此服务器依赖部署接线，而非双重订阅。
