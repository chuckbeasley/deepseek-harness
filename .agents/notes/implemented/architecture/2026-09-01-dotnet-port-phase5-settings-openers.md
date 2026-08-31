# Agent Note: .NET port Phase 5 wave 1 — the settings document/preset openers complete the namespace

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase5-settings-openers.zh.md)

## Problem

The settings remote namespace was complete except for the opener group the TS SettingsController ships: `settings/openSettingsDocument`, `settings/canOpenAgentPresetDirectory`, and `settings/openAgentPresetDirectory`. The C# port lacked the document materialization hook and the injectable native-opener seam, and the preset seam's resolution failure shape needed mapping to the wire codes.

## Decision

- `SettingsProvider.PrepareDocument()` (virtual, null for non-file storage; FileSettingsProvider   materializes an absent document and returns the resolved path) ports the TS   `prepareDocument` contract.
- `SettingsOpeners` in `Dsh.Web.Host` ports the TS `SettingsControllerInternals`: injectable   `OpenPath`/`OpenTextFile` delegates plus a `CanOpen` fact; the production default shell-opens   through the OS desktop handler (`Process.Start` with `UseShellExecute`). Tests inject fakes, so   no test launches a GUI.
- The three remotes mirror the TS controller field by field: `openSettingsDocument` classifies   preparation failures, a missing local document, open failures, and aborts exactly as the TS   does; `openAgentPresetDirectory` maps an empty id to `gateway/bad-request`, an absent preset   service to `agent-preset/not-found { agentPreset, available: [] }`, a missing preset to   `agent-preset/not-found` with the discovered ids, and a missing native opener to   `{ opened: false, path }`. The C# preset seam has no trust classification (the TS ships a   system root), so `agent-preset/read-only` stays deferred with the shipped-preset concept,   named in the remote's class comment.

## Consequences

The settings namespace is now complete: describe/update/replace/mutate plus all three openers, with the host suite at 76 (9 new opener suites, every refusal path covered with fakes), the full solution building at 0 errors, and the CLI suite at 17. The only remaining settings-adjacent deferred surfaces are the trust classification (shipped presets) and the native directory picker.

## Alternatives considered

- Hard-coding the opener as a static call inside the remote: the TS keeps the opener injectable   precisely so tests never launch a desktop handler; the record seam is the same shape.
- Mapping resolve failures to `gateway/internal`: the TS resolve throws `agent-preset/not-found`   with the available roster, so the C# remote reproduces that classification instead.
