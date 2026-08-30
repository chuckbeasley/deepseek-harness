# 2. Target architecture

## Solution layout

One .NET solution mirroring the package groups, with `ProjectReference` edges replacing pnpm workspace links and NuGet packages for the publishable API surface.

```
deepseek-harness.slnx
├── src/Cordis/                    # vendored framework, ported faithfully
│   ├── Cordis.Core/               # Context, Service, Events (5 dispatch modes), Fiber/effects, Registry
│   ├── Cordis.Cosmokit/           # utilities (Branded ids, home/path, timeout, retention)
│   ├── Cordis.Schemastery/        # config schema DSL + validation
│   ├── Cordis.Plugin.Loader/      # cordis.yml row resolution + transactional reconciliation
│   ├── Cordis.Plugin.Include/     # config trees + patch layers + expression interpolation
│   ├── Cordis.Plugin.Group/       # composition groups
│   ├── Cordis.Plugin.Timer/       # background scheduling
│   ├── Cordis.Plugin.Hmr/         # config watch + .NET hot reload
│   └── Cordis.Logger.Console/     # console logging (→ Microsoft.Extensions.Logging)
├── src/Dsh.Core/                  # session, system-prompt, tools, agent, agent-loop, scope
├── src/Dsh.Llm/                   # llm seam + DeepSeek providers + streaming
├── src/Dsh.<capability>/*         # fs, subprocess, shell, terminal, sandbox, lsp, web, skill,
│                                   # compaction, context, subagent, jobs, workflow, webhook, todo,
│                                   # plan, preset, guard, hooks, session-query, settings, credentials,
│                                   # storage, workspace, attachment, spill, goal, schedule, feedback, identity
├── src/Dsh.Cli/                   # dsh tool: args, profile boot, plugin mgmt, dump-config, TUI
├── src/Dsh.Web.Host/              # ASP.NET Core (Kestrel) + SignalR + Typert gateway
├── src/Dsh.Web.App/               # Blazor shell: slot outlet, store layer, localization, theme
├── src/Dsh.Web.Plugins.*/         # Razor Class Libraries (one per current ui-* plugin)
├── src/Dsh.Sdk/                   # .NET client SDK (replaces Python SDK) + stdio JSON-RPC server
├── src/Dsh.Acp/                   # ACP server
├── native/landlock-run/           # unchanged native code, P/Invoke + sidecar binding
└── tests/                         # xUnit, bUnit, Playwright, Verify snapshots, e2e
```

## Technology mapping

| Concern | Today | .NET 10 replacement |
|---|---|---|
| Language/runtime | TypeScript ESM | C# 14 on .NET 10 (LTS) |
| Workspace/build | pnpm, tsdown/tsc | `.slnx` + csproj `ProjectReference`; `dotnet build/publish` |
| DI + lifecycle | Cordis Context/Service/Fiber | **faithful `Cordis.Core` port** (MS DI used inside providers, not as the plugin container) |
| Typed events | declaration merging | typed event contracts + a generic dispatch engine (`Emit/Waterfall/Parallel/Serial/Bail`) |
| Reversible effects | `ctx.effect()` | `IAsyncDisposable` composition inside the Fiber |
| Waterfall middleware | `(...args, next)` | async `Func<..., Next>` chains (ASP.NET-style middleware) |
| Config schema | Schemastery | `Cordis.Schemastery` port; JSON/YAML via `System.Text.Json` + a YAML lib |
| `!!js` config expressions | JS eval | decision (§3.4) — Roslyn script evaluator vs. restricted expression language |
| Type graph / RPC | Typert generator + registry | Roslyn **source generators** + `System.Text.Json` source-gen codecs; SignalR hubs for web, JSON-RPC for SDKs |
| CLI args | commander | System.CommandLine / Spectre.Console.Cli |
| TUI | — (no interactive TUI today) | Spectre.Console (rich output/prompts) + Terminal.Gui (full-screen session UI) |
| Web host | Node HTTP + WS/SSE | ASP.NET Core minimal APIs + SignalR |
| Web client | React slot system | Blazor Server `RenderFragment` slot outlet + DI-registered `IComponentRegistration` |
| Stores/observables | custom snapshot stores | `INotifyPropertyChanged` / `IAsyncEnumerable` streaming over SignalR |
| Localization | typed dicts + `t` | `.resx`/JSON + `IStringLocalizer`; a Roslyn analyzer enforces i18n ownership |
| Persistence | SQLite + JSONL | `Microsoft.Data.Sqlite` (+ EF Core where useful) + JSONL log |
| LLM streaming | Node HTTP/SSE | `System.Net.Http` streaming + `System.Text.Json` |
| Subprocess/PTY | child_process + node-pty | `System.Diagnostics.Process` + ConPTY (Win) / pty (Unix) |
| Sandbox | bwrap/Landlock/Seatbelt + native addon | native sidecars retained; managed policy layer + P/Invoke binding |
| Unit tests | vitest | xUnit (or NUnit) + `Microsoft.NET.Test.Sdk` |
| Component tests | jsdom + RTL | **bUnit** |
| Snapshots | keyless recorded sessions | **Verify** (or Snapper) |
| E2E / browser | Playwright + vitest | **Playwright for .NET** |
| Coverage gate | v8 100% | Microsoft.CodeCoverage + Coverlet + CI gate |
| Lint/format | oxlint/ESLint | Roslyn analyzers + `.editorconfig` + `dotnet format` |
| Clone detection | jscpd | retained as a dev tool (runs over C# now) |
| Docs build | VitePress | folded into the Blazor app / static hosting |
