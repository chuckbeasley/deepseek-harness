# Agent Note: .NET 移植 Phase 5 第 1 波——会话控制流与流取消约定

Status: implemented

[English](2026-09-01-dotnet-port-phase5-session-control-stream.md) | 中文

## 问题

会话目录最后一个实时线上表面 `session/control`（一条基线，然后是 queue/jobs/投影增量）被延后，因为其数据源看起来不存在：C# 会话词汇没有持久的 `agent/inbox/spliced` 事件，jobs seam 看起来没有事件。注册表流的取消也违反 TS 取消约定：mux 以 `gateway/internal` 错误帧（"A task was canceled"）应答被取消的逻辑流，而不是安静地结束。

## 决策

`SessionControlRemotes.Control` 在已经存在的表面上移植该流：

- **基线**：逐会话队列从实时 inbox 读取（`Agent.Inbox.NextTurn` 然后是 `NextStep`；每个条目都置于 `queued`——steering/context 放置需要 TS 的 splice 投影，被延后），逐会话 jobs 来自按所属会话过滤的 `IJobsService.List`（无主 job 没有线上席位），一致的投影切面来自 `SessionProjectionRegistry.Snapshot`（注册表现在作为 dsh-base 中的 `sessionProjections` spine 行挂载）。
- **增量**：每个 `agent/inbox/inserted|claimed|discarded` 事件一条队列帧——Agent 本身已经通过 `IInboxNotifications` 发出这些事件——每条帧携带完整的当前条目（幂等；这是移植端口对持久 `agent/inbox/spliced` 事件的 inbox 事件替代，Inbox seam 将其记录为延后）。每个 `IJobsService.OnJobsChanged` 为受影响的所属会话发一条 jobs 帧。实时投影增量保持延后：注册表没有按键变更事件，基线携带一致切面。
- **取消**：mux 不再以错误帧应答被取消的逻辑流；`session/follow` 与 `session/control` 通过令牌注册的通道完成安静结束（`yield return` 不能位于带 catch 的 try 块这一迭代器约束，使通道完成方式成为自然的形态）。

## 影响

会话目录现在在实时流层面完全移植：`session/control` 在真实 mock-LLM 轮次与真实 jobs provider 上提供基线与 queue/jobs 增量，host 套件从 62 增至 67（4 个控制套件外加 1 个 mux 注册表流取消套件），全部通过，完整解决方案以 0 错误构建，CLI 套件为 17。线上现在对每一条注册表流都符合 TS 取消约定。

## 备选方案

- 在 C# 会话词汇中加入持久的 `agent/inbox/spliced` 会话事件：Inbox seam 的类注释已经延后它，而实时 inbox 事件携带相同信息；控制流改为每个事件重新推导完整条目。
- 在流迭代器中捕获 `OperationCanceledException`：`yield return` 不能出现在带 catch 子句的 try 块中，因此取消时通道完成是同一约定的编译器诚实形态。
- 每个 `session/event` 发出投影增量：注册表计算整块切面而非按键变化，因此逐事件帧会很啰嗦，而且仍然不匹配 `SessionProjectionUpdate` 线上形态；在按键变更事件存在之前，基线切面是诚实的表面。
