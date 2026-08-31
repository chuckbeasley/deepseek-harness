# Agent Note: .NET 移植 Phase 5 第 1 波——回环 fence 之外的认证

Status: implemented

[English](2026-09-01-dotnet-port-phase5-auth-beyond-loopback.md) | 中文

## 问题

fence 把每个请求都绑定到回环 authority（已记录的缩减）：在 LAN IP 或声明的主机名上提供 GUI 的部署无法打开信任 fence，preset seam 也不携带信任分类，因此 settings 打开器无法拒绝随部署发布的 preset（`agent-preset/read-only` 与「随部署发布的 preset」概念一起被延后）。

## 决策

- `WebAuthFence` 现在接收一个 `trustedHosts` 列表。每条配置的条目都会在主机启动时被断言为裸 `host[:port]` authority（`AssertTrustedAuthority`，即 TS 的 `assertTrustedAuthority`）：路径、userinfo、空白、悬空或补零的端口，或非规范的主机拼写都会让启动响亮失败，而不是悄悄改变授权范围。匹配遵循 TS 规则：无端口条目在任何端口上信任该主机名（LAN 服务形态），显式条目比较 WHATWG 主机并在两侧丢弃 http 默认端口（80），IPv6 字面量在两侧加方括号。webHost 行从其配置中读取 `trustedHosts`，非字符串列表元素响亮失败（TS 的 zod 数组）。
- preset seam 增加 `PresetTrust`（`System`/`User`，即 TS 的 `PresetTrust`）：每条 roster 行与已解析的 preset 都携带其被发现所在根的信任（provider 取根的信任；preset 行取一个 `trust` 配置）。settings 打开器以 `agent-preset/read-only {agentPreset, reason: "it ships with the deployment"}` 拒绝非用户 preset，与 TS 的拒绝完全一致。默认 spine 根仍由用户撰写；部署显式挂载 system 根——移植端口不捆绑任何 preset 来种出一个 system 根。

## 影响

部署可以在回环之外提供 GUI，并保留同样的 confused-deputy 防御（DNS 重绑定 Host fence、cross-site 标记、Origin 相等性）以及同样的误配置授权响亮失败表面；preset 编写表面现在可以强制 system/user 边界。94 个 host 套件（7 个新增 fence 套件，含一次完整的受信任 authority cookie 往返）与 15 个 preset 套件通过；完整解决方案以 0 错误构建。

## 备选方案

- 绑定 `0.0.0.0` 时自动推导 LAN IP 字面量（TS 的 `resolveLanTrust`）：移植端口尚不支持非回环绑定，因此声明列表表面就是全部；推导将在绑定主机落地时加入。
- 带优先级的 multi-root preset roster（完整的 TS `roots` 模型）：延后项点名的正是信任分类，而移植端口的 seam 按设计是单根；roster 保持为已记录的缩减。
