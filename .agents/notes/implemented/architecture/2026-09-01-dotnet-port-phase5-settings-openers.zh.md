# Agent Note: .NET 移植 Phase 5 第 1 波——settings 文档/预设打开器补齐 namespace

Status: implemented

[English](2026-09-01-dotnet-port-phase5-settings-openers.md) | 中文

## 问题

settings 远程 namespace 已经完整，除了 TS SettingsController 随附的打开器组：`settings/openSettingsDocument`、`settings/canOpenAgentPresetDirectory` 与 `settings/openAgentPresetDirectory`。C# 移植端口缺少文档物化钩子与可注入的原生打开器 seam，preset seam 的解析失败形态也需要映射到线上码。

## 决策

- `SettingsProvider.PrepareDocument()`（virtual，非文件存储返回 null；FileSettingsProvider 物化不存在的文档并返回解析后的路径）移植 TS 的 `prepareDocument` 约定。
- `Dsh.Web.Host` 中的 `SettingsOpeners` 移植 TS 的 `SettingsControllerInternals`：可注入的 `OpenPath`/`OpenTextFile` 委托加一个 `CanOpen` 事实；生产默认通过 OS 桌面处理器 shell 打开（`Process.Start` 配合 `UseShellExecute`）。测试注入 fake，因此没有任何测试会启动 GUI。
- 三个远程逐字段镜像 TS 控制器：`openSettingsDocument` 与 TS 完全一致地分类准备失败、本地文档缺失、打开失败与中止；`openAgentPresetDirectory` 把空 id 映射为 `gateway/bad-request`，把缺失的 preset 服务映射为 `agent-preset/not-found { agentPreset, available: [] }`，把缺失的 preset 映射为带已发现 id 的 `agent-preset/not-found`，把缺失的原生打开器映射为 `{ opened: false, path }`。C# preset seam 没有信任分类（TS 随附 system 根），因此 `agent-preset/read-only` 与「随部署发布的 preset」概念一起保持延后，在远程的类注释中具名。

## 影响

settings namespace 现在完整了：describe/update/replace/mutate 加全部三个打开器，host 套件为 76（9 个新增打开器套件，每条拒绝路径都用 fake 覆盖），完整解决方案以 0 错误构建，CLI 套件为 17。仅剩的 settings 相邻延后表面是信任分类（随部署发布的 preset）与原生目录选择器。

## 备选方案

- 在远程内部把打开器硬编码为静态调用：TS 正是为了让测试永不启动桌面处理器而保持打开器可注入；记录 seam 是同样的形态。
- 把解析失败映射为 `gateway/internal`：TS 的 resolve 抛出带可用 roster 的 `agent-preset/not-found`，因此 C# 远程改为复现该分类。
