# Agent Note: .NET 移植 Phase 5 第 1 波——shell 的实时状态对齐与 locale 所有的文案

Status: implemented

[English](2026-09-01-dotnet-port-phase5-shell-live-state-locale.md) | 中文

## 问题

Blazor 聊天 shell 渲染已提交的对话事件，但没有实时 agent 状态：轮次进行中的会话看起来与空闲会话一模一样，排队消息不可见，失败也消失不见。其产品文案同样是硬编码的英文，违反了移植规范为 C# shell 沿用的「客户端 UI 文案归 locale 所有」规则。

## 决策

- `WebSessionStore` 现在为每个会话条目投影三个实时事实，全部来自已移植 seam 已经发出的事件：`Running` 来自 `agent/status`（在 store 构造时从 AgentRegistry 基线化），`Queued` 来自三个 `agent/inbox/*` 事件（实时 inbox 计数），`Error` 来自 `agent/error`（新活动开始时清除，因此陈旧的失败绝不会比下一轮活得更久）。每条通知都保持提交后，符合 store 既有的约定。
- `WebLocale` 以最小方式移植文案规则：一份通过 `T(key)` 解析的类型化英文词典（缺失的键原样渲染），注册进 DI 并注入聊天页面。locale 选择机制与更多词典保持延后，在类注释中具名。聊天页面在会话列表中渲染 running/queued 状态，并在对话中渲染错误横幅。

## 影响

shell 现在与 seam 显示实时对齐（running/queued/error），host 套件从 76 增至 79（真实 mock-LLM 轮次上的三个 store 套件，含 mock 的两阶段 todo-then-text fixture，todo 行与 profile 完全一样地挂载），全部通过，完整解决方案以 0 错误构建。预渲染 shell smoke 在线上驱动一次真实的 mock 轮次（session/create + session/prompt），并断言所提供 HTML 中的 locale 文案、会话行与用户/助手对话文本。

## 备选方案

- 仅从会话事件推导 running/queued：会话日志不携带 inbox 或 status 事实，因此 agent 事件是权威来源（与 session/control 流使用的选择相同）。
- 带选择与多词典的完整 locale 系统：被移植规范延后；类型化单词典在保持文案规则的同时，不假装选择机制存在。
