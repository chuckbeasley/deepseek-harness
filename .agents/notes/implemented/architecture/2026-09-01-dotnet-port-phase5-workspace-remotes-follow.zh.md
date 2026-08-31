# Agent Note: .NET 移植 Phase 5 第 1 波——workspace 命令与 follow 流补齐 namespace

Status: implemented

[English](2026-09-01-dotnet-port-phase5-workspace-remotes-follow.md) | 中文

## 问题

workspace 远程 namespace 只有 `workspace/create`（在单槽生命周期之上）。持久注册表先落地；这一轮把命令重新接到它上面，并添加 follow 流，对照 TS WorkspaceController 补齐 workspace namespace。

## 决策

`WorkspaceRemotes` 现在完全坐落在注册表上：

- `workspace/create` 先按路径解析（幂等的重新注册应答 `created: false`，匹配 TS 命令），否则创建，并把每一次 seam 失败包装为 `workspace/invalid-path { path }`（TS 以同样的方式包装所有非 Remote 的创建错误）。
- 基于 id 的命令把 seam 码映射为 TS 线上码：目标缺失与（像 TS 的顺序错误映射那样）无效的顺序移动为 `workspace/not-found { workspaceId }`；重复的 rename 标题为 `workspace/name-conflict { name }`；非成员会话移动为 `workspace/move-invalid { workspaceId, sessionId, beforeSessionId? }`；点名未知会话的归档请求为 `session/not-found { sessionId }`。
- `workspace/follow` 先流式传输基线（`{ items, archivedSessionIds }`），然后从注册表事件流式传输 upsert/remove/order/archived 增量，在基线之前订阅，因此读取期间的变更排在基线之后（与 session/control 相同的形态）。Workspace 视图携带来自注册表的真实会话成员关系。

## 影响

workspace namespace 完整了：在持久目录之上的 create/rename/delete/insertBefore/insertSessionBefore/archiveSession/follow，host 套件为 85（remotes 套件从 5 增至 11 个用例，含带增量排序的 follow 流），完整解决方案以 0 错误构建。目录剩余的线上缺口是实时投影增量与 `$events` waterfall 结算。

## 备选方案

- 把 create 留在生命周期 provider 上：注册表在远程表面上取代它，而幂等语义（resolveByPath 对比单槽当前值）只在注册表上匹配 TS；生命周期仍作为自己的 seam 可用。
- 即使顺序未变也为每次变更发一条顺序帧：注册表在每次已提交的、影响顺序的变更上发出顺序帧（create 追加、delete 重写、insertBefore 移动），follow 流原样转发它们——客户端把它们当作幂等的全序帧，匹配 TS 流的形态。
