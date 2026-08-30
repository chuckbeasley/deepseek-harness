# Manual build-and-test for the Phase 1 timer/logger worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes (the same boundary documented in the harness pwsh tool notes). Restore itself
# works, and the compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc and runs the zero-dependency console assertion suite
# (tests\Cordis.Plugin.Timer.Tests). On an unrestricted host, `dotnet build` / `dotnet run
# --project tests\Cordis.Plugin.Timer.Tests` work normally.
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

$core = Join-Path $bin 'Cordis.Core.dll'
$cosmokit = Join-Path $bin 'Cordis.Cosmokit.dll'
$timer = Join-Path $bin 'Cordis.Plugin.Timer.dll'
$logger = Join-Path $bin 'Cordis.Logger.Console.dll'

Invoke-Csc -ExtraArgs @(
    '-target:library',
    "-out:$cosmokit",
    "-doc:$(Join-Path $bin 'Cordis.Cosmokit.xml')"
) -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Cosmokit') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Cosmokit'

Invoke-Csc -ExtraArgs @(
    '-target:library',
    "-out:$core",
    "-doc:$(Join-Path $bin 'Cordis.Core.xml')"
) -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Core'

Invoke-Csc -ExtraArgs @(
    '-target:library',
    "-out:$timer",
    "-doc:$(Join-Path $bin 'Cordis.Plugin.Timer.xml')",
    "-r:$core"
) -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Plugin.Timer') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Plugin.Timer'

Invoke-Csc -ExtraArgs @(
    '-target:library',
    "-out:$logger",
    "-doc:$(Join-Path $bin 'Cordis.Logger.Console.xml')",
    "-r:$core",
    "-r:$cosmokit"
) -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Logger.Console') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Logger.Console'

Invoke-Csc -ExtraArgs @(
    '-target:exe',
    "-out:$(Join-Path $bin 'Cordis.Plugin.Timer.Tests.dll')",
    "-r:$core",
    "-r:$cosmokit",
    "-r:$timer",
    "-r:$logger"
) -Sources (Get-ChildItem (Join-Path $root 'tests\Cordis.Plugin.Timer.Tests') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Plugin.Timer.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Cordis.Plugin.Timer.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Cordis.Plugin.Timer.Tests =='
& dotnet (Join-Path $bin 'Cordis.Plugin.Timer.Tests.dll')
exit $LASTEXITCODE
