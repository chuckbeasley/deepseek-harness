# Agent Note: .NET 移植 Phase 4 wave 3——延后能力接缝落地

Status: implemented

[English](2026-08-31-dotnet-port-phase4-wave3-deferred-seams.md) | 中文

## 问题

.NET 10 移植（分支 `port/dotnet10`）将 Phase 4 wave 2 记为完成，并留下具名的 wave-3 余量：沙箱 + 原生 landlock 桥接、webhook 入口、进程外 subagent 驱动、LSP 进程宿主 + 工具，以及 PTY/ConPTY 终端后端。完成 Phase 4 意味着以移植的接缝纪律落地这些表面：Service Definition + Provider + Consumer、零依赖控制台测试套件、spine 挂载，以及文档化的缩减项。

## 决策

每个剩余接缝都以忠实但有边界的 C# 移植交付，并由进程支撑的控制台测试套件验证：

- `Dsh.Authorization`（credentials 能力的凭据授权半部）：每个密钥一次尝试、decline/cancel 语义，以及通过写观察型 credentials 门面观察到的提交确认成功（`credentials/record-updated` 事件尚未移植）。
- `Dsh.Sandbox`：confine 契约（`Confine(argv, policy) -> ConfinedArgv?`）加 Landlock sidecar 后端。sidecar 的 `--probe` 与 `--ro/--rw -- argv` 契约是原生桥接边界：托管侧探测、包装，并在没有可用 sidecar 时故障关闭（`SANDBOX_UNAVAILABLE`）。原生 `landlock-run` 二进制在切换阶段之前仍以 `native/` 为源记录。
- `Dsh.Webhook`：带包含式派发与中止并排空（abort-and-drain）拆除的规则注册表、GitHub HMAC-SHA256 处理器（精确状态/消息词汇），以及回环 `HttpListener` 入口。会话创建是必需的组合钩子（`IWebhookSessionAction`），随 Phase-5 的 agent/workspace/preset spine 延后；其请求没有挂载动作的规则会大声失败。
- `Dsh.Subagent`：按具名驱动组织的 provider 注册表、作为具名 provider 的进程内驱动，以及 `SdkOutOfProcessProvider`——每次委派一个子运行时，通过按换行分帧的 JSON-RPC（initialize/session/prompt/shutdown）通信，含 assistant 输出折叠、`sdkChildOutcome` 原因映射，以及幂等的拆除阶梯。子运行时服务器随 SDK 阶段到来；脚本化假子进程固定线协议契约，配置为其携带 argv 接缝。
- `Dsh.Lsp`：带精确 65536 字节头部上限的流式 Content-Length 解码器、结构防护协议翻译、基于私有 `Process` 句柄的 JSON-RPC 连接（subprocess 接缝尚无管道模式——文档化回退）、带 transient didOpen/didClose 生命周期的串行化可中止服务器实例、`$/cancelRequest` 宽限，以及 shutdown/terminate 阶梯，外加纯工具渲染器。一个 fixture 服务器固定 90 个测试套件。
- `Dsh.Terminal`：ConPTY 后端（Windows）带受控提示就绪模型（`stdin_read`/`inferred_idle`）、OSC 133 净化器、串行化 resize，以及 ClosePseudoConsole → 进程树终止的拆除阶梯；套件在非 Windows 上自行跳过。

## 影响

wave-2 的"延后至 wave 3"清单已清空，除去那些依赖确实属于后续阶段的表面（LSP provider 池 + fs 宿主辅助、SDK/ACP 子服务器、Unix pty，以及各接缝具名表面如 sqlite 后端、fs diff/edit 工具与观察策略）。这些继续在接缝源码中具名，且 `dotnet-ci.yml` 车道现在运行每个控制台套件，使平台矩阵拥有该信号。Phase 4 记为 COMPLETE；Phase 5（Blazor）仍是下一阶段。

## 备选方案

- 扩展 `Dsh.Subprocess` 以支持管道 stdin/stdout 模式，而非在 Dsh.Lsp 中使用私有进程句柄：该接缝变更是后续 wave；连接的 spawner 接缝使替换成为机械操作。
- 现在就移植原生 landlock-run 二进制：它仅限 Linux，无法在移植当前宿主机上构建或运行；sidecar 契约与故障关闭的托管侧才是诚实的交付物。
- 通过现有 agent/workspace 接缝实现 webhook 会话创建：创建路径需要尚未移植的 presets、标题与权限解析；必需动作钩子在不对等的情况下保持接缝诚实。
