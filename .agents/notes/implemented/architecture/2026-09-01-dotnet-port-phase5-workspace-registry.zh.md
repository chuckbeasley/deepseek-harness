# Agent Note: .NET 移植 Phase 5 第 1 波——持久化 workspace 注册表

Status: implemented

[English](2026-09-01-dotnet-port-phase5-workspace-registry.md) | 中文

## 问题

workspace 远程 namespace 停留在单槽生命周期上（一个当前 workspace，无持久化），而 TS 侧是带显示顺序、会话成员关系与归档集的持久注册表。六个基于注册表的命令与 follow 流被延后在其后。

## 决策

`WorkspaceRegistry`（ctx.workspaceRegistry）在既有 JSON 存储 seam 之上移植 TS 注册表核心：一个 `workspace_registry` 存储在 `workspaces` 表中保存逐 workspace 记录（id、规范路径、标题、时刻、sessionIds），并在全局单例中保存显示顺序 + 归档集。每一次已提交的变更都先持久化，然后发出其注册表事件（`workspace/upserted|removed|order|archived`），follow 流将消费这些事件。命令表面匹配 TS 形态：create（用 seam 码做路径校验、拒绝重复路径；命令层通过 resolveByPath 应答幂等的重新打开）、rename（唯一的非空标题）、delete、insertBefore（类 DOM 移动）、attach/insertSessionBefore 与 archiveSession。

两个已记录的缩减：会话成员关系是显式的（`AttachSession`），因为 C# 会话持久化不携带头级 workspace 记账（TS 从 sessionPersistence 头推导成员关系）；归档校验使用注入的 session-known 谓词，未组合会话 store 时默认接受任意（TS 对照实时会话加持久化校验）。生命周期 provider 保持为身份/根核心；注册表是远程 namespace 所依托的持久目录。

## 影响

workspace seam 现在有了持久目录：10 个新增注册表套件（共 19 个 workspace 套件）覆盖 create/resolve/rename/delete/order、成员关系移动、带 known-session 门控的归档集、变更事件与跨实例持久化，全部通过，完整解决方案以 0 错误构建。workspace 远程命令与 follow 流接下来将落在这个注册表上。

## 备选方案

- 像 TS 那样从会话持久化头推导会话成员关系：C# 持久化格式不携带 workspace 记账，扩展格式是单独的一波；带 attach 入口点的显式成员关系让注册表保持诚实，而无需发明头约定。
- 持久化整文档 blob 而非存储单元：存储 seam 的表 + 全局恰好匹配注册表形态（记录 + 顺序/归档），因此该单元是自然的介质，并让文档保持可检视的 JSON。
