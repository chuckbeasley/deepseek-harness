# Manual build-and-test for the Phase 1 HMR port worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suite. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Cordis.Plugin.Hmr.Tests` work normally.
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

$core = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName
$loader = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Plugin.Loader') -Filter '*.cs' | ForEach-Object FullName
$include = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Plugin.Include') -Filter '*.cs' | ForEach-Object FullName
$hmr = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Plugin.Hmr') -Filter '*.cs' | ForEach-Object FullName
$tests = Get-ChildItem (Join-Path $root 'tests\Cordis.Plugin.Hmr.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core, then Loader (-> Core), Include (-> Core + Loader),
# Hmr (-> Core + Include), then the tests app.
$coreDll = Join-Path $bin 'Cordis.Core.dll'
$loaderDll = Join-Path $bin 'Cordis.Plugin.Loader.dll'
$includeDll = Join-Path $bin 'Cordis.Plugin.Include.dll'
$hmrDll = Join-Path $bin 'Cordis.Plugin.Hmr.dll'
$testsDll = Join-Path $bin 'Cordis.Plugin.Hmr.Tests.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$coreDll") -Sources $core -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$loaderDll", "-r:$coreDll") -Sources $loader -Label 'Cordis.Plugin.Loader'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$includeDll", "-r:$coreDll", "-r:$loaderDll") -Sources $include -Label 'Cordis.Plugin.Include'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$hmrDll", "-r:$coreDll", "-r:$loaderDll", "-r:$includeDll") -Sources $hmr -Label 'Cordis.Plugin.Hmr'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$testsDll", "-r:$coreDll", "-r:$loaderDll", "-r:$includeDll", "-r:$hmrDll") -Sources $tests -Label 'Cordis.Plugin.Hmr.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Cordis.Plugin.Hmr.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Cordis.Plugin.Hmr.Tests =='
& dotnet $testsDll
exit $LASTEXITCODE
