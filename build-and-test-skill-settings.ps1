# Manual build-and-test for the Phase 4 skill/settings worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suites. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Hsh.Skill.Tests` (and the settings twin) work normally.
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
$schemastery = Join-Path $bin 'Cordis.Schemastery.dll'
$llm = Join-Path $bin 'Hsh.Llm.dll'
$session = Join-Path $bin 'Hsh.Session.dll'
$tools = Join-Path $bin 'Hsh.Tools.dll'
$skill = Join-Path $bin 'Hsh.Skill.dll'
$settings = Join-Path $bin 'Hsh.Settings.dll'

# Compile in dependency order: Cordis.Core -> Cosmokit -> Schemastery -> Hsh.Llm -> Hsh.Session ->
# Hsh.Tools -> Hsh.Skill -> Hsh.Settings -> both test projects.
Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$cosmokit") -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Cosmokit') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Cosmokit'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$schemastery", "-r:$cosmokit") -Sources (Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Schemastery') -Filter '*.cs' | ForEach-Object FullName) -Label 'Cordis.Schemastery'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llm", "-r:$core") -Sources (Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Llm') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$session", "-r:$core", "-r:$llm") -Sources (Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Session') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$tools", "-r:$core", "-r:$llm", "-r:$session") -Sources (Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Tools') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$skill", "-r:$core", "-r:$llm", "-r:$session", "-r:$tools") -Sources (Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Skill') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Skill'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$settings", "-r:$core", "-r:$cosmokit", "-r:$schemastery") -Sources (Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Settings') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Settings'

Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Hsh.Skill.Tests.dll')", "-r:$core", "-r:$llm", "-r:$session", "-r:$tools", "-r:$skill") -Sources (Get-ChildItem (Join-Path $root 'tests\Hsh.Skill.Tests') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Skill.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Hsh.Settings.Tests.dll')", "-r:$core", "-r:$cosmokit", "-r:$schemastery", "-r:$settings") -Sources (Get-ChildItem (Join-Path $root 'tests\Hsh.Settings.Tests') -Filter '*.cs' | ForEach-Object FullName) -Label 'Hsh.Settings.Tests'

$runtime = $pack.Name
foreach ($name in @('Hsh.Skill.Tests', 'Hsh.Settings.Tests')) {
    $runtimeConfig = @{
        runtimeOptions = @{
            tfm = 'net10.0'
            framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
            rollForward = 'LatestMinor'
            configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
        }
    }
    $runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin "$name.runtimeconfig.json") -Encoding utf8
}

Write-Host '== Running Hsh.Skill.Tests =='
& dotnet (Join-Path $bin 'Hsh.Skill.Tests.dll')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host '== Running Hsh.Settings.Tests =='
& dotnet (Join-Path $bin 'Hsh.Settings.Tests.dll')
exit $LASTEXITCODE
