# Manual build-and-test for the Phase 4 wave 2 capability seams (Dsh.Storage, Dsh.Workspace,
# Dsh.Spill, Dsh.Attachment + their console test projects).
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. The compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc in dependency order and runs the zero-dependency console
# assertion suites. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Dsh.Storage.Tests` work normally.
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

function New-RuntimeConfig {
    param([string]$AssemblyName)
    $runtime = $pack.Name
    $runtimeConfig = @{
        runtimeOptions = @{
            tfm = 'net10.0'
            framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
            rollForward = 'LatestMinor'
            configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
        }
    }
    $runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin "$AssemblyName.runtimeconfig.json") -Encoding utf8
}

$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'

$cordis = Get-ChildItem (Join-Path $src 'Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName
$llm = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$storage = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Storage') -Filter '*.cs' | ForEach-Object FullName
$workspace = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Workspace') -Filter '*.cs' | ForEach-Object FullName
$spill = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Spill') -Filter '*.cs' | ForEach-Object FullName
$attachment = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Attachment') -Filter '*.cs' | ForEach-Object FullName
$storageTests = Get-ChildItem (Join-Path $tests 'Dsh.Storage.Tests') -Filter '*.cs' | ForEach-Object FullName
$workspaceTests = Get-ChildItem (Join-Path $tests 'Dsh.Workspace.Tests') -Filter '*.cs' | ForEach-Object FullName
$spillTests = Get-ChildItem (Join-Path $tests 'Dsh.Spill.Tests') -Filter '*.cs' | ForEach-Object FullName
$attachmentTests = Get-ChildItem (Join-Path $tests 'Dsh.Attachment.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Dsh.Llm -> Dsh.Session -> Dsh.Tools
# -> Dsh.Storage -> Dsh.Workspace -> Dsh.Spill -> Dsh.Attachment -> the four test projects.
$core = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Dsh.Llm.dll'
$sessionDll = Join-Path $bin 'Dsh.Session.dll'
$toolsDll = Join-Path $bin 'Dsh.Tools.dll'
$storageDll = Join-Path $bin 'Dsh.Storage.dll'
$workspaceDll = Join-Path $bin 'Dsh.Workspace.dll'
$spillDll = Join-Path $bin 'Dsh.Spill.dll'
$attachmentDll = Join-Path $bin 'Dsh.Attachment.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources $cordis -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$core") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$core", "-r:$llmDll") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$storageDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $storage -Label 'Dsh.Storage'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$workspaceDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$storageDll") -Sources $workspace -Label 'Dsh.Workspace'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$spillDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$storageDll", "-r:$workspaceDll") -Sources $spill -Label 'Dsh.Spill'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$attachmentDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$storageDll", "-r:$workspaceDll", "-r:$spillDll") -Sources $attachment -Label 'Dsh.Attachment'

$testDlls = @('Dsh.Storage.Tests', 'Dsh.Workspace.Tests', 'Dsh.Spill.Tests', 'Dsh.Attachment.Tests')
$testSources = @($storageTests, $workspaceTests, $spillTests, $attachmentTests)
for ($i = 0; $i -lt $testDlls.Count; $i++) {
    $name = $testDlls[$i]
    $refs = @("-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$storageDll", "-r:$workspaceDll", "-r:$spillDll", "-r:$attachmentDll")
    Invoke-Csc -ExtraArgs (@('-target:exe', "-out:$(Join-Path $bin "$name.dll")") + $refs) -Sources $testSources[$i] -Label $name
    New-RuntimeConfig -AssemblyName $name
}

$failed = $false
foreach ($name in $testDlls) {
    Write-Host "== Running $name =="
    & dotnet (Join-Path $bin "$name.dll")
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) {
    Write-Host 'BUILD/TEST FAILED'
    exit 1
}
Write-Host 'ALL TESTS PASSED'
exit 0
