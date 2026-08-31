# Agent Note: .NET 移植 Phase 5 第 1 波——shell 内批准 UI、设置/计划页面与 LAN 信任

Status: implemented

[English](2026-09-01-dotnet-port-phase5-approval-ui-pages-lan-trust.md) | 中文

## 问题

Phase 5 延后之后，三个后续表面仍处于表面门控（surface-gated）状态：shell 没有进程内批准/提问 UI（交互 seam 只能由远程 `$events` 客户端应答），settings 与 plan seam 没有 GUI 页面（剩余的 ui-* 包在等待表面），fence 也无法服务全接口绑定（LAN 字面量从未被推导）。

## 决策

- `Dsh.Ui.Approval`（ui-approval 移植端口）向 shell 的 `shell.overlay` slot 渲染一个组件，在进程内应答交互 waterfall：`approval/request` 的批准/拒绝对话框、`user-questions/ask` 的文本对话框，以及 tools/pre-execute 适配器——在 shell 存活期间把每一次 shell 工具调用都路由经过批准 seam。一切随 circuit 一起销毁；TUI 在自己的 profile 上保留自己的对话框，headless profile 保持未批准状态。
- `Dsh.Ui.Settings` 与 `Dsh.Ui.Plan` 添加路由页面（`/settings` 显示 settings 文档路径与脱敏后的 namespace 目录；`/plan` 显示所选会话的 plan 折叠视图，跟随共享选择与 store 实时更新）以及侧边栏导航链接。仅靠静态 Router 的 `AdditionalAssemblies` 无法路由这些页面：在 .NET 10 中 SSR router 通过端点级路由数据匹配，因此页面程序集必须在映射时注册到 RazorComponents 端点上（`AddAdditionalAssemblies`）。这迫使 web bundle 将 webCore（创建 slot 与页面程序集注册表）排在 ui-* 行之前、webHost（执行映射）排在最后。
- fence 的 LAN 信任（TS 的 `resolveLanTrust`）：绑定全接口主机（`0.0.0.0`）时，将机器的非回环 IPv4 字面量推导为无端口的受信任 authority——IP 字面量 Host 在任何端口上都安全，且绑定前无法得知绑定端口——显式配置的条目随后按配置顺序加入。

## 影响

shell 现在能应答自己的批准与提问，settings 与 plan seam 有了 GUI 表面，全接口绑定通过同一道 fence 服务 LAN 客户端。113 个 host 套件通过（2 个新增：LAN 推导与真实 Kestrel 主机上的组合 shell 页面），41 个 console 套件全部通过，完整解决方案 0 错误，headless-Chrome smoke 端到端驱动整个循环：提交 → 针对 mock 轮次的工具调用出现批准对话框 → 点击 Approve → 回复渲染完成。

## 备选方案

- 仅通过静态 Router 的 AdditionalAssemblies 路由 ui-* 页面：端点从未看到这些程序集，因此 `/settings` 与 `/plan` 返回 404——在 .NET 10 的 SSR 模型中，映射时注册是强制性的。
- 一个可编辑 namespace 的 settings 页面：页面只读取脱敏后的目录与文档路径；编辑仍保留在 remote/CLI 表面（已记录），与移植端口的只读优先立场一致，直到写 UI 出现消费需求。
