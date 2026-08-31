# Agent Note: .NET port Phase 5 wave 1 — Blazor interactivity fixes and the browser smoke

Status: implemented

## Problem

The interactive shell attached a live circuit (StartCircuit, attachWebRendererInterop,
RenderBatch replacing the prerendered markup) but typed input produced no server effect:
the rendered DOM carried literal `@bind`/`@onsubmit` attributes, so no event handler was
ever wired — a submitted form navigated natively to `/?` and reloaded the page, killing
the circuit. The published CLI was serving a shell that looked interactive but was not.

## Decision

- The root cause was the Razor source generator on SDK 10.0.400 serving stale generated
  code for `Dsh.Web.App` after an interrupted build: the per-file incremental cache never
  invalidated on `.razor` content changes, so every incremental build re-emitted the old
  literal-attribute output. Clean builds regenerate correctly. Probes across stock
  templates, the Sdk.Web-library shape, `@rendermode`, and `@using static` all compile
  `@bind`/`@on*` into EventCallback/delegation code, so the toolset, project shape, and
  the page's own directives are innocent. Treat razor edits as requiring a clean build
  on this machine until the SDK is upgraded.
- The component `@using`s (`Microsoft.AspNetCore.Components`, `.Forms`, `.Routing`,
  `.Web`) joined `_Imports.razor` — the stock template carries them and they belong
  there regardless.
- The hosting requirements the smoke forced are now the shipped shape: `Dsh.Cli` is an
  SDK.Web binary with `RequiresAspNetWebAssets` (the `_framework` blazor.web.js assets
  are internal to the Web SDK), the WebApplication content root is
  `AppContext.BaseDirectory` (the published wwwroot sits next to the binary, not in the
  launcher CWD), and the Router/RouteView stay static because their templated/typed
  parameters cannot cross an interactive boundary — pages opt in with
  `@rendermode InteractiveServer` on the page.
- Browser evidence: a headless-Chrome CDP driver against the published CLI opens the
  launch-token URL, exchanges the fence cookie, waits for the interactive DOM, types a
  chat message, and dispatches change + form-submit through Blazor's delegated
  listeners. The circuit frames show BeginInvokeDotNetFromJS DispatchEventAsync →
  RenderBatch → EndInvokeDotNetFromJS; the prompt echoes, the assistant answers
  "Todo list recorded.", and the session row appears.

## Consequence

The shell is genuinely interactive end to end: 40 console suites green, full solution
building at 0 errors, and the browser smoke passes against the final published
artifact. Known cosmetic: the `_content/Dsh.Web.App/dsh.css` reference re-bases to the
root and 404s (static web asset merge), noted, not blocking.

## Alternatives considered

- Relying on Enter-key implicit form submission in the driver: unreliable under CDP
  and unnecessary once the compiled form carries `@onsubmit:preventDefault`; the smoke
  dispatches change + `requestSubmit()` deterministically.
- Replacing the form with `@onkeydown` Enter handling: the form was never the problem —
  the un-compiled directives were; keeping the form preserves the accessible submit
  button.
