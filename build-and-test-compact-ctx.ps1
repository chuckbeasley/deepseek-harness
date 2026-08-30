# Manual build-and-test for the Phase 4 compaction + context + session-query capability seams worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suites. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Dsh.Compaction.Tests` (and the context/session-query peers) work normally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdkDir = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$csc = Join-Path $sdkDir.FullName 'Roslyn\bincore\csc.dll'
$pack = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\packs\Microsoft.NETCore.App.Ref') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$refDir = Join-Path $pack.FullName 'ref\net10.0'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

# The host sandbox remaps reads of C:\Program Files\... made by child processes
# (csc fails with CS2001 on -r: paths under it), so reference assemblies are
# copied into the workspace and referenced from there.
$refsDir = Join-Path $bin 'refs'
New-Item -ItemType Directory -Force -Path $refsDir | Out-Null
Copy-Item (Join-Path $refDir '*.dll') $refsDir -Force
$refsFile = Join-Path $bin 'refs.rsp'
Get-ChildItem $refsDir -Filter '*.dll' | ForEach-Object { "-r:$($_.FullName)" } | Set-Content -LiteralPath $refsFile -Encoding utf8

function Invoke-Csc {
    param([string[]]$ExtraArgs, [string[]]$Sources, [string]$Label)
    $arguments = @('exec', $csc, '-noconfig', '-nologo', '-langversion:latest', '-nullable:enable', "@$refsFile") + $ExtraArgs + $Sources
    Write-Host "== $Label =="
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "csc failed for $Label (exit $LASTEXITCODE)" }
}

$src = Join-Path $root 'src'
$core = Get-ChildItem (Join-Path $src 'Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName
$llm = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$compaction = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Compaction') -Filter '*.cs' | ForEach-Object FullName
$context = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Context') -Filter '*.cs' | ForEach-Object FullName
$sessionQuery = Get-ChildItem (Join-Path $src 'Dsh\Dsh.SessionQuery') -Filter '*.cs' | ForEach-Object FullName
$compactionTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Compaction.Tests') -Filter '*.cs' | ForEach-Object FullName
$contextTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Context.Tests') -Filter '*.cs' | ForEach-Object FullName
$sessionQueryTests = Get-ChildItem (Join-Path $root 'tests\Dsh.SessionQuery.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Dsh.Llm -> Dsh.Session -> Dsh.Tools ->
# Dsh.Compaction -> Dsh.Context -> Dsh.SessionQuery -> the three test apps.
$coreDll = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Dsh.Llm.dll'
$sessionDll = Join-Path $bin 'Dsh.Session.dll'
$toolsDll = Join-Path $bin 'Dsh.Tools.dll'
$compactionDll = Join-Path $bin 'Dsh.Compaction.dll'
$contextDll = Join-Path $bin 'Dsh.Context.dll'
$sessionQueryDll = Join-Path $bin 'Dsh.SessionQuery.dll'
$compactionTestsDll = Join-Path $bin 'Dsh.Compaction.Tests.dll'
$contextTestsDll = Join-Path $bin 'Dsh.Context.Tests.dll'
$sessionQueryTestsDll = Join-Path $bin 'Dsh.SessionQuery.Tests.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$coreDll") -Sources $core -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$coreDll") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$coreDll", "-r:$llmDll") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$compactionDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $compaction -Label 'Dsh.Compaction'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$contextDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $context -Label 'Dsh.Context'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionQueryDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $sessionQuery -Label 'Dsh.SessionQuery'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$compactionTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$compactionDll") -Sources $compactionTests -Label 'Dsh.Compaction.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$contextTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$contextDll") -Sources $contextTests -Label 'Dsh.Context.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$sessionQueryTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$sessionQueryDll") -Sources $sessionQueryTests -Label 'Dsh.SessionQuery.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Compaction.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Context.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.SessionQuery.Tests.runtimeconfig.json') -Encoding utf8

Write-Host ''
Write-Host '== Running Dsh.Compaction.Tests =='
& dotnet $compactionTestsDll
if ($LASTEXITCODE -ne 0) { throw "Dsh.Compaction.Tests failed (exit $LASTEXITCODE)" }

Write-Host ''
Write-Host '== Running Dsh.Context.Tests =='
& dotnet $contextTestsDll
if ($LASTEXITCODE -ne 0) { throw "Dsh.Context.Tests failed (exit $LASTEXITCODE)" }

Write-Host ''
Write-Host '== Running Dsh.SessionQuery.Tests =='
& dotnet $sessionQueryTestsDll
if ($LASTEXITCODE -ne 0) { throw "Dsh.SessionQuery.Tests failed (exit $LASTEXITCODE)" }

Write-Host ''
Write-Host 'All compaction, context, and session-query tests passed.'
exit 0
