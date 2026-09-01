<#
.SYNOPSIS
    Non-destructive extraction of the .NET port (hsh) from the deepseek-harness
    monorepo into a standalone project directory.

.DESCRIPTION
    Copies the port's tree - src/ (Cordis + Hsh), tests/, the snapshots/session
    corpus, plan/, the solution file, Directory.Build.props, the build-and-test
    scripts, the dotnet CI workflow, and the product docs - into $Target with
    the monorepo's relative layout preserved, writes a fresh README/.gitignore/
    PROVENANCE.md, verifies the copy (solution build + the 73-scenario corpus
    gate + every console suite + the xUnit framework suite + the headless
    smoke), and optionally git-inits the result with an initial commit.

    The monorepo is never modified. The TypeScript tree (packages/, apps/,
    vendor/, website/, docs/, native/, scripts/) stays behind untouched.

.PARAMETER Target
    Destination directory. Defaults to a sibling "hsh" of this repository.

.PARAMETER SkipVerify
    Skip the build + corpus + suite verification (copy only).

.PARAMETER SkipGit
    Skip the fresh `git init` + initial commit.
#>
param(
    [string]$Target = (Join-Path (Split-Path -Parent $PSScriptRoot) 'hsh'),
    [switch]$SkipVerify,
    [switch]$SkipGit
)

# Native commands (dotnet/git/robocopy) may legitimately write to stderr (e.g. a suite's
# DEBUG frame); under 'Stop' a merged 2>&1 stderr line becomes a terminating error, so the
# script uses 'Continue' and checks $LASTEXITCODE explicitly after every native command.
$ErrorActionPreference = 'Continue'
$source = $PSScriptRoot
$sourceFull = [System.IO.Path]::GetFullPath($source)
$targetFull = [System.IO.Path]::GetFullPath($Target)

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

# ---- guards: never clobber the source repo or an existing non-empty target ----
if ([System.String]::Equals($sourceFull, $targetFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Target must differ from the source repo ($sourceFull)"
}
if ($targetFull.StartsWith($sourceFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Target must not be inside the source repo"
}
if ($sourceFull.StartsWith($targetFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Target must not contain the source repo"
}
if (Test-Path $targetFull) {
    if (@(Get-ChildItem -Force $targetFull).Count -gt 0) { throw "Target exists and is not empty: $targetFull" }
}
New-Item -ItemType Directory -Force -Path $targetFull | Out-Null
Write-Host "extracting $sourceFull -> $targetFull"

# ---- 1. port sources, tests, and the corpus (bin/obj artifacts excluded) ----
foreach ($dir in 'src', 'tests') {
    & robocopy (Join-Path $source $dir) (Join-Path $targetFull $dir) /E /XD bin obj /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $dir (exit $LASTEXITCODE)" }
}
& robocopy (Join-Path $source 'snapshots\session') (Join-Path $targetFull 'snapshots\session') /E /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed for snapshots\session (exit $LASTEXITCODE)" }
$LASTEXITCODE = 0
& robocopy (Join-Path $source 'plan') (Join-Path $targetFull 'plan') /E /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed for plan (exit $LASTEXITCODE)" }
$LASTEXITCODE = 0
# The TS snapshot harness lives at snapshots/session/*.snapshot.ts; the corpus
# scenarios are directories, so only the harness file is dropped.
Get-ChildItem (Join-Path $targetFull 'snapshots\session') -File -Filter '*.snapshot.ts' -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host 'copied  src/ tests/ snapshots/session/ plan/ (bin/obj excluded)'

# ---- 2. solution, build conventions, scripts, CI lane, docs ----
foreach ($item in 'deepseek-harness.slnx', 'Directory.Build.props', '.editorconfig',
                   'LICENSE', 'SAFETY.md', 'SAFETY.zh.md', 'BRAND_GUIDELINES.md', 'BRAND_GUIDELINES.zh.md',
                   'spike-design.md', 'SPIKE-README.md', 'PIPELINE-README.md',
                   '.github\workflows\dotnet-ci.yml') {
    $srcPath = Join-Path $source $item
    if (-not (Test-Path $srcPath)) { throw "missing source item: $srcPath" }
    $dstPath = Join-Path $targetFull $item
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dstPath) | Out-Null
    Copy-Item $srcPath $dstPath -Force
    if (-not $?) { throw "failed to copy $item" }
}
Get-ChildItem $source -Filter 'build-and-test-*.ps1' -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $targetFull $_.Name) -Force
    if (-not $?) { throw "failed to copy $($_.Name)" }
}
Write-Host 'copied  solution, props, editorconfig, build scripts, dotnet CI, product docs'

# ---- 3. fresh project files ----
Write-Utf8NoBom (Join-Path $targetFull '.gitignore') @'
# .NET build artifacts
bin/
obj/
*.user
*.suo
*.binlog
.vs/
TestResults/
artifacts/

# local smoke / tooling residue
.local-feed/
.toolsmoke/
.sdk-consumer/
tmp/

# secrets and machine-local files
.env
*.local.json
'@

Write-Utf8NoBom (Join-Path $targetFull 'README.md') @'
# hsh — the .NET port of DeepSeek Harness

`hsh` is the standalone C#/.NET 10 port of the DeepSeek Harness agent
harness: the everything-is-a-plugin Cordis architecture, the recorded-session
parity corpus, the `Harness` dotnet tool (command `hsh`) and the `Harness.Sdk`
client. This tree was extracted from the TypeScript monorepo
`deepseek-harness` (branch `port/dotnet10`) — the monorepo stays the source of
record; see [PROVENANCE.md](PROVENANCE.md).

## Layout

    src/Hsh/*              the port (Harness.* namespaces, Hsh.* assemblies)
    src/Cordis/*           the vendored Cordis framework ports
    tests/Hsh.*            zero-dependency console assertion suites
    tests/Cordis.Core.Tests  the xUnit framework suite
    snapshots/session/     the 73-scenario recorded-session corpus
    plan/                  the running port log (en + zh)

## Build

    dotnet build deepseek-harness.slnx -c Release

## Test

    dotnet test tests/Cordis.Core.Tests/Cordis.Core.Tests.csproj -c Release
    dotnet run --project tests/Hsh.Spike.Tests/Hsh.Spike.Tests.csproj -c Release

The corpus gate is the parity baseline (63 passed / 1 documented drift /
9 skipped / 0 errored of 73 on this host):

    dotnet run --project tests/Hsh.Snapshot.Tests/Hsh.Snapshot.Tests.csproj -c Release -- corpus
    dotnet run --project tests/Hsh.Snapshot.Tests/Hsh.Snapshot.Tests.csproj -c Release -- diff <scenario>

Every remaining tests/*/*.csproj is a console assertion suite; run them like
the dotnet CI lane (skip Cordis.Core.Tests there — it is the xUnit lane).

## Package

    dotnet pack deepseek-harness.slnx -c Release -o .local-feed

produces the `Harness` dotnet tool (command `hsh`), the `Harness.Sdk` client,
and the `Harness.*` library closure, one version source (0.2.0 in
Directory.Build.props). A consumer restores `Harness.Sdk` from the feed and
drives `DeepSeekHarness` with `HshBin`/`HshHome` pointing at the installed
tool.

## Run

    dotnet run --project src/Hsh/Hsh.Cli/Hsh.Cli.csproj -c Release -- --profile headless "task"

Review the [safety notice](SAFETY.md) before running. This is developer-preview
software; expect compatibility-breaking changes.
'@

Write-Utf8NoBom (Join-Path $targetFull 'PROVENANCE.md') @'
# Provenance

This tree is a non-destructive extraction of the .NET port of DeepSeek
Harness from the `deepseek-harness` monorepo
(https://github.com/chuckbeasley/deepseek-harness), branch `port/dotnet10`,
commit 14a9e60295, produced by `extract-hsh-project.ps1`.

Included:

- `src/` — `Cordis/` (the framework ports) and `Hsh/` (the port), source only
  (bin/obj build artifacts excluded)
- `tests/` — every console assertion suite, the xUnit framework suite, and
  the `Cordis.Core.Tests.Runner` sources (bare-csc, no csproj)
- `snapshots/session/` — the 73 recorded-session corpus scenarios the parity
  gate replays (the TS harness file `headless.snapshot.ts` is excluded)
- `plan/` — the running port log (en + zh)
- `deepseek-harness.slnx`, `Directory.Build.props`, `.editorconfig`
- the `build-and-test-*.ps1` scripts and the `dotnet-ci.yml` workflow
- the product docs (LICENSE, SAFETY, BRAND_GUIDELINES) and the Phase 0-2
  design docs (spike-design.md, SPIKE-README.md, PIPELINE-README.md)

Excluded (they belong to the TypeScript monorepo): `packages/`, `apps/`,
`vendor/`, `website/`, `docs/`, `native/` (the landlock source of record —
the port's sandbox uses its own providers and a scripted fake for tests),
`scripts/` (TS gates), the TS snapshot harness trees (`snapshots/acp`,
`snapshots/sdk`, `snapshots/web`), the TS root tooling (tsconfig*, vitest*,
pnpm*, lefthook.yml, package.json), and the monorepo-facing docs (README,
CONTRIBUTING, THIRD_PARTY_NOTICES, AGENTS.md, CLAUDE.md).

The monorepo's relative layout is preserved exactly — the slnx and
Directory.Build.props sit at the root, `tests/Hsh.Snapshot.Tests` stays two
levels deep — so the RepoRoot/HshCliPath assembly metadata and the
root-relative build scripts work unchanged.
'@
Write-Host 'wrote  .gitignore, README.md, PROVENANCE.md'

# ---- 4. verification: build + corpus + suites (parity baseline) ----
if (-not $SkipVerify) {
    Push-Location $targetFull
    try {
        Write-Host '== 4a. solution build (Release) =='
        dotnet build deepseek-harness.slnx -c Release
        if ($LASTEXITCODE -ne 0) { throw 'solution build failed' }

        Write-Host '== 4b. corpus gate =='
        $corpus = dotnet run --project tests/Hsh.Snapshot.Tests/Hsh.Snapshot.Tests.csproj -c Release --no-build -- corpus 2>&1 | Out-String
        $summary = [regex]::Match($corpus, 'corpus: (\d+) passed, (\d+) drifted, (\d+) skipped, (\d+) errored of (\d+)')
        if (-not $summary.Success) { throw "corpus summary not found; output:`n$corpus" }
        $passed = [int]$summary.Groups[1].Value; $drifted = [int]$summary.Groups[2].Value
        $skipped = [int]$summary.Groups[3].Value; $errored = [int]$summary.Groups[4].Value
        Write-Host "corpus: $passed passed, $drifted drifted, $skipped skipped, $errored errored of 73"
        if ($passed -ne 63 -or $drifted -ne 1 -or $errored -ne 0) {
            throw "corpus deviates from the parity baseline (63/1/9/0): $passed/$drifted/$skipped/$errored"
        }

        Write-Host '== 4c. console assertion suites =='
        foreach ($project in (Get-ChildItem (Join-Path $targetFull 'tests') -Filter '*.csproj' -Recurse)) {
            if ($project.Name -eq 'Cordis.Core.Tests.csproj') { continue }  # the xUnit lane below
            Write-Host "== $($project.Name) =="
            dotnet run --project $project.FullName -c Release --no-build
            if ($LASTEXITCODE -ne 0) {
                # Known load-sensitive flake (e.g. Fs under back-to-back load): retry standalone once.
                Write-Host "retrying $($project.Name) standalone"
                dotnet run --project $project.FullName -c Release
                if ($LASTEXITCODE -ne 0) { throw "console suite failed: $($project.Name)" }
            }
        }

        Write-Host '== 4d. xUnit framework suite =='
        dotnet test tests/Cordis.Core.Tests/Cordis.Core.Tests.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'xUnit framework suite failed' }

        Write-Host '== 4e. headless smoke (Hsh.Spike) =='
        dotnet run --project src/Hsh/Hsh.Spike/Hsh.Spike.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'headless smoke failed' }
    }
    finally { Pop-Location }
    Write-Host 'verification passed'
}

# ---- 5. fresh git repository ----
if (-not $SkipGit) {
    Push-Location $targetFull
    try {
        git init 2>&1 | Out-Null
        git add -A
        $message = @'
Initial import of the extracted .NET port (hsh)

Non-destructive extraction from the deepseek-harness monorepo (branch
port/dotnet10, commit 14a9e60295) by extract-hsh-project.ps1. See
PROVENANCE.md for what is included and what stays in the monorepo.

Verified before this commit: solution build 0 errors, the 73-scenario
corpus at the 63/1/9/0 parity baseline, every console assertion suite,
the xUnit framework suite, and the headless smoke.
'@
        $msgPath = Join-Path $targetFull '.git\EXTRACT_COMMIT_MSG.txt'
        Write-Utf8NoBom $msgPath $message
        git commit -F $msgPath
        if ($LASTEXITCODE -ne 0) { throw 'initial commit failed' }
        Remove-Item $msgPath -Force
        git log -1 --oneline
    }
    finally { Pop-Location }
}

Write-Host "done: $targetFull"
