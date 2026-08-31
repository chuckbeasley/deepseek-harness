# Agent Note: .NET 移植 Phase 5 第 1 波——交互 seam 与 $events waterfall 结算

Status: implemented

[English](2026-09-01-dotnet-port-phase5-interaction-and-events-settlement.md) | 中文

## 问题

最后一个延后的线上表面：`$events` 流只转发普通 emit，没有 waterfall 投递，也没有 `$events/result` 结算，ask-user/approval 表面未移植——TUI 在 `tools/pre-execute` 上拥有自己的批准对话框，而其他任何东西都无法回答人类提问。

## 决策

- `src/Dsh/Dsh.Interaction` 移植交互能力 seam：批准服务（`approval/request` waterfall，带封闭的结果词汇 allowed-once / rejected / cancelled / unavailable，无应答者时故障关闭，ask/never 会话策略带 `approval/policy` 日志覆盖，以及轮次封闭的 `approval/asked` + `approval/decided` 审计对——空闲的 ask 在追加之前拒绝，因为轮次之间的裸事件在重载时就是崩溃尾部的垃圾），user-questions 服务（`user-questions/ask` 应答者 waterfall，带 ASK_ABORTED / EMPTY_QUESTIONS / UNAVAILABLE 分类），以及面向模型的 `ask_user_question` 工具。interaction/* 会话事件像其他所有插件合并标记一样注册进会话事件类型注册表。
- web host 把两个 waterfall 桥接到每一条存活的 `$events` 流上：提案以 `{type: "waterfall", event, eventId, agentId, request}` 转发，request 投影为 JSON 安全字段，挂起的续体保存在按客户端结算中，`$events/result` 一元调用结算它——`next` 委托给 waterfall 链，`result` 把值映射进封闭词汇（其他任何东西都故障关闭），`rejected` 恢复远程错误（name/code/details）并使 ask 关闭失败。中止的请求或关闭中的流投递 `{type: "cancel", eventId}` 并把 ask 结算为 cancelled/aborted。线上形态镜像 TS 流协议：未知 clientId 结算为 `gateway/internal`（"identifies no active event stream"），已知客户端上的未知 eventId 以 no-op 确认，畸形载荷结算为 `gateway/bad-request`。
- spine 挂载 `approval`（策略配置，未知值响亮失败）、`userQuestions` 与 `toolAskUser` 行；webHost 行拥有结算并注册 `$events/result` 方法。

## 影响

远程 GUI 现在可以通过 mux 应答批准 ask 与用户提问，seam 是 ask/audit/policy 语义唯一的家（TUI 作为应答者形态的消费者之一保留自己的对话框）。111 个 host 套件（真实 Kestrel 主机与 mux WebSocket 上的 7 个新增结算套件，含取消帧路径与两条桥）与 12 个交互套件通过；完整解决方案以 0 错误构建。两个值得保留的实现事实：名为 `_` 的 lambda 参数遮蔽 `out _` 丢弃（$events/result 处理器必须给其取消令牌命名），以及已注册但空闲的客户端代次对 no-op 确认仍是「活跃」的，因此结算把客户端与待处理提案分开跟踪。

## 备选方案

- 自动转发每一个 waterfall 事件：seam 点名的正是交互表面，桥只订阅这两个交互 waterfall；通用的远程 waterfall 机制等待需要它的消费方。
- 完整的 TS 批准表面（实时 agent 所有权检查、策略用户消息注入、权限预设、命令）：延后项点名的正是 ask-user/approval 表面；实时检查与消息注入在各自消费方落地时加入。
