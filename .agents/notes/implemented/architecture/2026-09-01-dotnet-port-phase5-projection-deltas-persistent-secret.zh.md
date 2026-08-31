# Agent Note: .NET 移植 Phase 5 第 1 波——实时投影增量与持久化 fence 密钥

Status: implemented

[English](2026-09-01-dotnet-port-phase5-projection-deltas-persistent-secret.md) | 中文

## 问题

两个已记录的缺口仍然存在：会话控制流的实时投影增量（基线携带一致切面，但没有按键更新，因为投影注册表没有变更事件），以及 fence 的按实例签名密钥（cookie 在主机重启时失效，不像 TS 的凭据记录密钥）。

## 决策

- `SessionControlRemotes` 现在订阅 `session/event`，并在每个已提交事件之后，把受影响会话的投影切面与上次发送的切面做 diff，每个变化的键发出一条 `projection { sessionId, key, value, seq }` 帧（即 TS 的 SessionProjectionUpdate 形态）。切面从基线读取的同一状态播种，因此第一条增量只携带真实变化（基线已经显示了例如 `title: null`）；帧携带完整值且幂等，非 JSON 视图与基线一样被省略。diff 状态与快照读取在同一把锁下串行化，因此来自不同会话的并发事件无法与注册表的惰性单元构建竞争。
- `WebHostService` 现在在组合了凭据 seam 时通过它解析 cookie 签名密钥：读取 `DSH_WEB_SESSION_SECRET` 引用（环境值优先；畸形存储值响亮失败），或在托管存储中创建新的 32 字节 base64url 值，因此 cookie 像 TS 凭据记录一样在主机重启后存活。没有凭据 seam 时，fence 保留按实例的随机密钥。

## 影响

`session/control` 现在完整了（基线 + queue/jobs/投影增量），只要组合了凭据 seam，fence 的已记录缩减就被关闭。host 套件从 85 增至 87（真实 mock 轮次上的投影增量套件，以及证明重启前 cookie 能对共享托管存储的全新主机认证的重启套件），全部通过，完整解决方案以 0 错误构建。

## 备选方案

- 让投影注册表发出按键变更事件：注册表计算整块切面，没有按键 diff 约定；控制流拥有 diff，注册表保持不变。
- 即使没有凭据 seam 也总是创建密钥：托管存储就是凭据 seam；没有它的主机没有持久的地方存放密钥，因此随机回退是诚实的边界。
