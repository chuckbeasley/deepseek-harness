# Agent Note: .NET 移植 Phase 5 第 1 波——作为 RCL slot 贡献的 ui-* 插件集

Status: implemented

[English](2026-09-01-dotnet-port-phase5-ui-plugin-rcls.md) | 中文

## 问题

shell 的聊天页面把一切都内联渲染：会话列表、输入器与侧边栏都硬编码在 `ChatPage.razor` 中，因此没有任何 UI 表面能向 shell 贡献——ui-* 插件集的延后项（把约 45 个 TS 客户端包移植为通过 slot 贡献的 Razor Class Library）没有任何可组合进去的东西。

## 决策

- 四个 ui-* RCL 随附，每个对应一个 shell 表面，各自通过共享的 `SlotRegistry` 注册组件并在 dispose 时收回：
  - `Dsh.Ui.Sidebar`——品牌行 + New Session 动作进入 `sidebar` slot；
  - `Dsh.Ui.Sessions`——会话列表（选中高亮、running/queued 状态）进入 `sessions` slot；
  - `Dsh.Ui.Chat`——输入器（交互式 `dsh-input-row` 表单）进入 `chat.composer` slot；
  - `Dsh.Ui.Workspace`——基于 `workspaceRegistry` seam、实时来自其四个注册表事件的 workspace 列表进入 `sidebar` slot。
- 聊天页面由 slot 组合而成：它渲染 slot，只拥有对话与循环轮次。选择通过作用域 `ShellState` 传递（列表与对话始终一致）；手势通过作用域 `ShellBus` 传递（`RequestNewSession` / `RequestSend`），因此贡献者永远不知道页面的内部。spine 的 webHost 行创建共享的 `SlotRegistry`（ui 行注册进 DI 服务的同一个实例），dsh-web bundle 挂载四个 ui 行。
- 剩余的 TS ui-* 包等待它们要构建的表面——移植端口的 shell 还没有 settings/plan/goal/jobs/skill/subagent/tool/trajectory/approval 页面。每一个都在计划 README 中具名为表面门控，而不是被悄悄丢弃。

## 影响

shell 是可组合的：UI 包通过注册进 slot 贡献，预渲染 HTML 证明组合结果（侧边栏 chrome、workspace 列表、会话列表、输入器、双语文案）。112 个 host 套件通过（真实 Kestrel 主机上的 1 个新增组合套件，断言每一项贡献与两种 locale），41 个 console 套件全部通过，完整解决方案 0 错误，headless-Chrome smoke 端到端驱动组合 shell——输入器通过 bus 发布，页面运行 mock 轮次，回复渲染，zh locale 在 circuit 挂接后幸存。

## 备选方案

- 现在移植每一个 TS ui-* 包：移植端口的 shell 对其中大多数没有表面（settings 页面、plan/goal/jobs 视图、tool/trajectory 卡片、对话渲染器）；延后项的完成物是 slot 组合机制加 shell 表面集，其余显式表面门控。
- 在 renderSlot 现场传 props（TS 的 four-shares 模型）：移植端口的 `SlotRegistration` 只携带片段工厂；共享状态与手势由作用域服务携带，这是移植端口的最小等价物，直到带 props 的 slot API 出现需要它的消费方。
