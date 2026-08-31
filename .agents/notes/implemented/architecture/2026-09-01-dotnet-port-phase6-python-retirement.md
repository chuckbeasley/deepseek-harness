# Agent Note: .NET port Phase 6 wave 6 — the Python SDK retirement

Status: implemented

English | [中文](2026-09-01-dotnet-port-phase6-python-retirement.zh.md)

## Problem

The .NET client SDK (Dsh.Sdk.Client) replaced the Python SDK as the out-of-process client contract, but the retired surface still lived in the repository: the `python/` tree, its release pipelines and builders, its CI jobs and dependabot entry, the runtime-closure gate, the pytest config, the user guide, and references across the docs, gates, and manifests.

## Decision

- `python/` is deleted, along with the surfaces that existed only to ship it: the   `build-exe-for-python-sdk` workflow and its spec, the GitLab python release pipeline, the   builder/smoke/release scripts (`build-exe-for-python-sdk.ts`, `build-python-release.py`,   `smoke-python-runtime.py`, `check-macos-deployment-target.py`), the runtime-closure gate   (`verify-runtime-closure.ts` + spec, its `package.json` script, and its run-gates   entries), `pytest.ini`, the uv dependabot entry, the python jobs in the PR workflow, the   `python/sdk-runtime` workspace and lockfile entries, and the python SDK user guide with   its translation sidecars.
- Every active reference is updated: the notices generator drops the Python closure   metadata, collection, and sections (the fetched-tool section goes with the pkg builder);   the workspace-constraints, translation-pairing, config-source-ownership, and CI-workflow   gates drop the python paths and tests; the SDK group READMEs (en + zh) name the retired   Python SDK and its .NET replacement; `AGENTS.md` drops the python layout line and the   python half of the Both-SDKs testing rule; the docs i18n scope and config catalog drop   the python mentions.
- The code-runtime's CPython language backend (`packages/code-runtime/code-runtime-python`)   is a separate seam (the fd-3 wire protocol for a CPython sandbox runtime) and is   unaffected.

## Consequences

The repository no longer carries the retired Python SDK or any surface that shipped it: the .NET client SDK is the out-of-process client contract, and every gate, pipeline, and document that referenced the python tree is consistent with its deletion. 45 console suites green; full solution builds at 0 errors. Phase 6 is complete.

## Alternatives considered

- Keeping `python/` as a frozen snapshot: the retired SDK would rot without a consumer and   its pipelines would still reference it; deletion with reference updates is the clean   retirement the phase plan specified.
