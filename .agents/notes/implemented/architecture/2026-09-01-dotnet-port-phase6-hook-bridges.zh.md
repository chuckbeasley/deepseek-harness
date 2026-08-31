# Agent Note: .NET 移植 Phase 6 wave 5——Claude Code 与 Codex 钩子桥接

Status: implemented

[English](2026-09-01-dotnet-port-phase6-hook-bridges.md) | 中文

## 问题

移植的 hooks seam 已有编解码器、匹配器与事件类型，但没有桥接：Claude Code 与 Codex 命令钩子桥接（配置解析、钩子执行、限制性合并、分离运行的完全停稳、持久化的 invoked/result 配对、以及扩展点映射）在 .NET 侧没有对应实现，shell seam 也没有为方言环境提供环境槽位。

## 决策

- `src/Dsh/Dsh.Hooks` 增加桥接运行所依赖的共享协议组件：`HookRunner`（经 shell seam 执行命令钩子，支持按钩子覆盖超时、桥接自有的默认超时、可信 stdin 载荷与方言环境、尾随换行的分帧差异、以及预期事件名作用域；基础设施拒绝变成无退出码的结果，因此钩子永远不会使轮次崩溃）、`HookMerge`（deny > ask > allow 优先级、粘性首个 `continue:false`、胜出等级的原因、有序的上下文/系统消息累积）、`DetachedRuns`（emit 形态的 SessionStart 运行被跟踪、在 dispose 时中止并排空）、以及 `HookLog`（轮次包裹的 invoked/result 配对，带 500 字符的 stderr 摘要上限）。
- Claude Code 桥接解析七事件匹配器组格式，支持 `${CLAUDE_PLUGIN_ROOT}`/`${CLAUDE_PROJECT_DIR}` 替换并跳过非命令钩子，监听 `agent/session-start`、`agent/pre-step`、`tools/pre-execute`、`tools/post-execute` 与 `agent/turn-stopping`，在导出 `CLAUDE_PROJECT_DIR` 的情况下构建 CC 载荷，并以字面量交替模式运行匹配器。Codex 桥接解析五事件子集，支持 `async`/非命令跳过与 `timeout`/`timeoutSec` 别名，拥有 snake_case 载荷（model、permission_mode、turn_id、`{ command }` tool_input）、仅正则匹配器、无尾随换行、以及干净纯 stdout 作为上下文的规则。
- shell request/spec 增加了方言环境流经的额外环境槽位（request → spec → 子进程 spawn，在环境清理后合并）。spine 挂载 `hooksClaudeCode` 与 `hooksCodex` 行，其 `configPath` 由部署方拥有（没有随附 profile 组合包）。
- 已记录的缩减项，每项均具名：移植的会话 header 不携带工作区 cwd（载荷 cwd 与钩子 workdir 回退到进程 cwd）、SubagentStart/SubagentStop 可解析但永不触发（移植的 subagent seam 没有开始/结束生命周期事件）、钩子 `ask` 映射为 deny（移植的预工具决策没有 ask 席位）、后工具 `additionalContext` 注入到下一步（工具决策不携带 additional-context 槽位）、以及阻塞的 Stop 钩子无界强制继续（TS 的 loop-guard TODO）。

## 影响

未修改的 Claude Code 与 Codex 钩子可在 harness 的拦截点上运行：23 个钩子套件证明了合并优先级、两个配置解析器、真实进程运行器（载荷分帧、阻塞退出、受限的基础设施失败）、以及两个桥接在真实 agent loop（智能体循环）上的端到端行为（载荷捕获、deny 阻塞、上下文注入、持久化的 invoked/result 配对、以及 Codex 的纯 stdout 规则）。45 个 console 套件全部绿灯；完整解决方案以 0 错误构建。python/ 退役是最后一个 Phase-6 wave。

## 备选方案

- 按桥接复制 run/merge/append 逻辑：共享协议组件在各方言间完全一致；它们只存在于 seam 中一份，载荷与决策映射由各桥接拥有。
- 让工具决策承载后工具上下文：移植的 PostToolDecision 记录没有 additional-context 槽位；下一步注入以已记录的偏差保持上下文对模型可见。
