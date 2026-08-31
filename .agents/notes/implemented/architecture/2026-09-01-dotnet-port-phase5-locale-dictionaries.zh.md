# Agent Note: .NET 移植 Phase 5 第 1 波——双语 shell 文案与 locale 交接

Status: implemented

[English](2026-09-01-dotnet-port-phase5-locale-dictionaries.md) | 中文

## 问题

shell 只携带一份英文词典（`WebLocale.English`），没有任何选择机制：locale 选择机制与更多词典被延后，因此非英文浏览器总是看到英文文案。

## 决策

- `WebLocale` 现在是双语的——英文与简体中文，即仓库的双语配对——回退链为 当前词典 → 英文 → 键（缺失的键原样渲染）。`Negotiate(Accept-Language)` 按头顺序选择第一个主子标签与已发布 locale 匹配的语言，默认英文。
- 活跃 locale 由 `MapDshApp` 中的一个小中间件按请求钉定（`HttpContext.Items["dsh.locale"]`），页面通过 `PersistentComponentState` 把它带过 prerender → circuit 边界：预渲染注册一个 `OnPersisting` 回调，交互 circuit 取回该值，因此两次渲染一致。组件注入的作用域 `WebLocale` 是逐作用域 `LocaleScope` 之上的门面，在每次 `T()` 调用时解析语言——页面在 `OnInitialized` 中钉定语言，该钩子在组件的注入服务解析之后运行。

## 影响

shell 端到端使用浏览器的语言：headless-Chrome smoke 现在请求 `Accept-Language: zh-CN`，验证中文 shell 能预渲染，验证 zh 文案在交互挂接后幸存（PersistentComponentState 交接），并且仍然完成完整的 mock 轮次。99 个 host 套件（5 个新增 locale 套件，含 Kestrel 上的真实预渲染测试）通过；完整解决方案以 0 错误构建。两个值得保留的实现事实：Razor 在预渲染 HTML 中对非 ASCII 文案做 HTML 编码（断言匹配编码后的形式），以及 `PersistAsJson` 只在 `OnPersisting` 回调内合法。

## 备选方案

- 通过 JS 初始化器在客户端解析 locale（navigator.languages）：服务器渲染文案，因此服务器必须知道语言；Accept-Language 头已在每个请求上携带它，包括 circuit 握手。
- 由静态当前 locale 切换的单例 `WebLocale`：逐作用域状态绝不能是静态的——作用域门面让并发 circuit 相互独立。
