# Agent Note: .NET 移植 Phase 5 第 1 波——settings、credentials 与 workspace 远程 namespace 落地

Status: implemented

[English](2026-09-01-dotnet-port-phase5-settings-credentials-workspace-remotes.md) | 中文

## 问题

Phase 5 web 基础（gateway、mux、`$events`、Blazor shell 与会话 remotes）把 settings、credentials 与 workspace 远程 namespace 记录为延后。收尾该波的远程目录意味着在已移植的 seam（`Dsh.Settings`、`Dsh.Credentials`、`Dsh.Workspace`）之上移植这三个 namespace，并保持与 TS 控制器完全一致的行为：脱敏读取、分类的写入拒绝、语法与批量边界，以及 provider 缺失诊断。

## 决策

`Dsh.Web.Host` 中的三个远程类逐字段镜像 TS 控制器：

- `SettingsRemotes`——`settings/describe`（脱敏目录：writable/hasDocument 加每个 namespace 一个视图，含 ns、schema、脱敏的 value/base/user、applies、secret slots、revision）、`settings/update` 与 `settings/replace`。写入对每一次 seam 拒绝分类：过期 revision 变为 `settings/conflict { ns, expected, actual }`；其余一切变为 `settings/rejected { ns }`。线上 schema 搭乘 Schemastery 的 `toJSON()` refs 信封，移植为 `Cordis.Schemastery.Schema.ToJson()`（`{ uid, refs }`，子引用为 uid 数字，callable 从不序列化）。
- `CredentialsRemotes`——`credentials/describe`（批量 ≤ 64 个 ref，语法 `^[A-Za-z_][A-Za-z0-9_]*$`，逐 ref 的 configured/source/writable，绝不含值）、`credentials/set`（非空值门控）与 `credentials/unset`；被遮蔽的写入以 `credential/rejected { ref }` 和 seam 自己的消息拒绝。
- `WorkspaceRemotes`——`workspace/create` 在已移植的单槽生命周期之上：幂等的重新打开应答 `created: false`，失败分类为 `workspace/invalid-path { path }`（TS 对每个非 Remote 错误的包装），视图携带稳定 id、规范路径、标题、空的 `sessionIds`（记账与注册表一起延后）以及 ISO-8601 时刻。

gateway 通过新的开放字符串 `RpcDomainError`（code + 可选 details）原样传输任何字符串代码，与 TS 的 `RemoteErrorCode` 联合保持开放的方式相同。namespace 在没有 provider 的情况下保持注册，并以可操作的 `gateway/internal` 应答，匹配 TS 控制器。一条 `settings` spine 行（基于 `<dshHome>/settings.json` 的 FileSettingsProvider——移植端口仅支持 JSON，因此默认文档偏离 TS 的 `settings.yaml`）加入 dsh-base bundle，seam 暴露一个公开的线上值转换器（`SettingsWireValues.FromJsonElement`），使 host 不重复 seam 的 JSON 值表示。

## 影响

wave-1 远程目录现在覆盖 session、settings、credentials 与 workspace；host 套件从 26 增至 44（settings 6、credentials 7、workspace 5），全部通过，完整解决方案以 0 错误构建，settings/credentials/workspace seam 套件不变且通过。`dsh web` profile 现在在 gateway 上暴露全部三个 namespace。`settings/mutate` 路径操作随后在同一 seam 上落地（在序列化写链上执行有序的 set/unset 编辑，后来的操作观察到先前的操作，根路径语义，以及脱敏视图的密钥字段用例），使 settings 套件增至 16、host 套件增至 62。仍在移植规范与源码中具名延后的有：settings 文档/预设打开器、workspace 注册表方法（rename/delete/insertBefore/insertSessionBefore/archiveSession/follow）、directoryPicker（以 `directory-picker/unavailable` stub）、Roslyn 源码生成器、ui-* 插件集、locale 词典，以及回环令牌之外的信任/认证 fence。

## 备选方案

- 以类 TypeScript 的类型字符串而非 `toJSON()` refs 信封发出 schema：该字符串只是展示产物，无法再水合；信封是文档化的线上形式，客户端以 `new Schema(json)` 再水合。
- 在 `rpc` spine 行中硬性要求 settings/credentials/workspace provider：没有这些行的 profile 会启动失败；TS 控制器保持 namespace 注册并应答 provider 缺失诊断，因此 C# 处理器改为在调用时解析 provider。
- 在 host 中重复 JsonElement 到 seam 值的转换：该转换是 seam 文档化的 JSON 值表示，因此 seam 上的单个公开转换器才是诚实的边界。
