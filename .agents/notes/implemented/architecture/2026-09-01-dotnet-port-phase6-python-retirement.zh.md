# Agent Note: .NET 移植 Phase 6 wave 6——Python SDK 退役

Status: implemented

[English](2026-09-01-dotnet-port-phase6-python-retirement.md) | 中文

## 问题

.NET 客户端 SDK（Dsh.Sdk.Client）取代了 Python SDK 成为进程外客户端约定，但已退役的表面仍留在仓库中：`python/` 目录树、其发布流水线与构建器、其 CI 任务与 dependabot 条目、运行时闭包门禁、pytest 配置、用户指南、以及 docs、门禁与 manifest 中的引用。

## 决策

- `python/` 被删除，连同仅为交付它而存在的表面：`build-exe-for-python-sdk` workflow 及其 spec、GitLab python 发布流水线、builder/smoke/release 脚本（`build-exe-for-python-sdk.ts`、`build-python-release.py`、`smoke-python-runtime.py`、`check-macos-deployment-target.py`）、运行时闭包门禁（`verify-runtime-closure.ts` + spec、其 `package.json` 脚本与 run-gates 条目）、`pytest.ini`、uv dependabot 条目、PR workflow 中的 python 任务、`python/sdk-runtime` workspace 与 lockfile 条目、以及 python SDK 用户指南及其翻译伴随文件。
- 每个活跃引用都会被更新：notices 生成器删除 Python 闭包元数据、集合与分节（fetched-tool 分节随 pkg builder 一起移除）；workspace-constraints、translation-pairing、config-source-ownership 与 CI-workflow 门禁删除 python 路径与测试；SDK 组 README（en + zh）点名已退役的 Python SDK 及其 .NET 替代品；`AGENTS.md` 删除 python 布局行与 Both-SDKs 测试规则中的 python 一半；docs i18n 范围与 config catalog 删除 python 提及。
- code-runtime 的 CPython 语言后端（`packages/code-runtime/code-runtime-python`）是独立的 seam（面向 CPython 沙箱运行时的 fd-3 wire protocol），不受影响。

## 影响

仓库不再携带已退役的 Python SDK 或任何交付它的表面：.NET 客户端 SDK 是进程外客户端约定，所有引用 python 目录树的门禁、流水线与文档都与其删除保持一致。45 个 console 套件绿灯；完整解决方案以 0 错误构建。Phase 6 完成。

## 备选方案

- 将 `python/` 保留为冻结快照：已退役的 SDK 在没有消费方的情况下会腐烂，其流水线仍会引用它；带引用更新的删除才是阶段计划所规定的干净退役。
