# Agent Note: .NET 移植 Phase 5 第 1 波——类型化 codec 源码生成器

Status: implemented

[English](2026-09-01-dotnet-port-phase5-typed-codec-generator.md) | 中文

## 问题

gateway 的线上信封是手写的 JsonElement 管道：一元载体用 Utf8JsonWriter 逐字段构建服务器信封，mux 通过匿名对象构建错误帧。延后项点名了用于类型化 codec 的 Roslyn 源码生成器——即 typert codec 的一半——因此线上代码由生成且编译期检查，而不是手工维护。

## 决策

- `src/Dsh/Dsh.Rpc.Generator` 是一个增量 Roslyn 源码生成器：标记 `[RpcCodec]` 的 record（该属性由生成器自身发出）获得一个静态 `<Name>Codec` 类，含逐属性的 `Encode`（基于 Utf8JsonWriter）与 `TryDecode`（逐属性类型检查，带可读的拒绝）。支持的成员类型：string、int、long、double、bool、`System.Text.Json.JsonElement`、这些类型的可空形式，以及生成 codec 可组合的嵌套 `[RpcCodec]` record。不支持的成员类型发出 `#error`，使构建响亮失败而不是交付半个 codec。可空的 JsonElement 成员在缺失时编码为空对象——RPC 错误词汇总是携带 `details`。
- 消费方就是 gateway 本身：`RpcError` 携带 `[RpcCodec]`，一元载体的结果错误分支与 mux 的错误帧现在都通过生成的 `RpcErrorCodec.Encode` 渲染。线上形态不变——codec 测试钉定确切的 `{code, message, details}` 对象、往返与拒绝词汇，既有的 gateway/mux/fence 套件在真实载体上证明该形态。

## 影响

gateway 的错误编码现在是生成代码；添加或改变 codec 成员类型会让构建以指向明确的诊断失败，而不是悄悄错误序列化。103 个 host 套件通过（4 个新增 codec 套件）；完整解决方案以 0 错误构建。值得保留的实现事实：生成器以 netstandard2.0 为目标（record 需要 `IsExternalInit` polyfill，`Environment`/range 语法被分析器规则禁止），位置 record 必须通过其主构造函数构造（对象初始化器需要无参构造函数），`JsonElement` 可空类型需要在生成的条件语句中显式 `(JsonElement?)null` 转换。

## 备选方案

- 手写每一个 codec（原先的状态）：延后项点名的正是生成器，而 gateway 是让生成代码真实而非推测的那个唯一消费方。
- 生成完整的信封写入器（type/rpcId/result 嵌套）：信封有普通 record 无法表达的值或错误分支；生成的 codec 覆盖共享错误词汇，固定的信封外壳留在载体中，TS host 也把它留在那里。
