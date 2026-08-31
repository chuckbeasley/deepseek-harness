# Agent Note: .NET 移植 Phase 5 第 1 波——Blazor 交互性修复与浏览器 smoke

Status: implemented

[English](2026-09-01-dotnet-port-phase5-blazor-interactivity-browser-smoke.md) | 中文

## 问题

交互式 shell 挂接了实时 circuit（StartCircuit、attachWebRendererInterop、RenderBatch 替换预渲染标记），但键入的输入没有产生任何服务器效果：渲染出的 DOM 携带字面的 `@bind`/`@onsubmit` 属性，因此从未接上任何事件处理器——提交的表单原生导航到 `/?` 并重载页面，杀死了 circuit。发布的 CLI 提供的是一个看起来交互、实则不交互的 shell。

## 决策

- 根本原因是 SDK 10.0.400 上的 Razor 源码生成器在构建被中断后为 `Dsh.Web.App` 提供了陈旧的生成代码：逐文件增量缓存从未在 `.razor` 内容变化时失效，因此每次增量构建都重新输出旧的字面属性代码。干净构建能正确重新生成。对标准模板、Sdk.Web 库形态、`@rendermode` 与 `@using static` 的探测都正确地把 `@bind`/`@on*` 编译为 EventCallback/委托代码，因此工具链、项目形态与页面自身的指令都是清白的。在本机把 razor 编辑视为需要干净构建，直到 SDK 升级。
- 组件的 `@using`（`Microsoft.AspNetCore.Components`、`.Forms`、`.Routing`、`.Web`）并入 `_Imports.razor`——标准模板本就携带它们，无论从哪个角度看它们都属于那里。
- smoke 逼出的托管要求现在就是发布形态：`Dsh.Cli` 是带 `RequiresAspNetWebAssets` 的 SDK.Web 二进制（`_framework` blazor.web.js 资源属于 Web SDK 内部），WebApplication 内容根是 `AppContext.BaseDirectory`（发布的 wwwroot 位于二进制旁边，而非启动器 CWD），Router/RouteView 保持静态，因为它们的模板化/类型化参数无法跨越交互边界——页面通过页面上的 `@rendermode InteractiveServer` 选择加入。
- 浏览器证据：针对发布 CLI 的 headless-Chrome CDP 驱动打开启动令牌 URL，交换 fence cookie，等待交互 DOM，键入聊天消息，并通过 Blazor 的委托监听器分派 change + 表单提交。circuit 帧显示 BeginInvokeDotNetFromJS DispatchEventAsync → RenderBatch → EndInvokeDotNetFromJS；提示回显，助手回答 "Todo list recorded."，会话行出现。

## 影响

shell 端到端真正可交互：40 个 console 套件通过，完整解决方案以 0 错误构建，浏览器 smoke 针对最终发布产物通过。已知的外观问题：`_content/Dsh.Web.App/dsh.css` 引用重新定位到根并 404（静态 web 资源合并所致），已记录，不阻塞。

## 备选方案

- 在驱动中依赖 Enter 键的隐式表单提交：在 CDP 下不可靠，而且一旦编译后的表单携带 `@onsubmit:preventDefault` 就毫无必要；smoke 确定性地分派 change + `requestSubmit()`。
- 用 `@onkeydown` Enter 处理替换表单：表单从来不是问题所在——未编译的指令才是；保留表单就保留了可访问的提交按钮。
