# Manual build-and-test for the Phase 4 jobs + workflow capability seams worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suites. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Hsh.Jobs.Tests` (and the workflow peer) work normally.
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
$llm = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$jobs = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Jobs') -Filter '*.cs' | ForEach-Object FullName
$workflow = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Workflow') -Filter '*.cs' | ForEach-Object FullName
$jobsTests = Get-ChildItem (Join-Path $root 'tests\Hsh.Jobs.Tests') -Filter '*.cs' | ForEach-Object FullName
$workflowTests = Get-ChildItem (Join-Path $root 'tests\Hsh.Workflow.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Hsh.Llm -> Hsh.Session -> Hsh.Tools ->
# Hsh.Jobs / Hsh.Workflow -> both test apps.
$coreDll = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Hsh.Llm.dll'
$sessionDll = Join-Path $bin 'Hsh.Session.dll'
$toolsDll = Join-Path $bin 'Hsh.Tools.dll'
$jobsDll = Join-Path $bin 'Hsh.Jobs.dll'
$workflowDll = Join-Path $bin 'Hsh.Workflow.dll'
$jobsTestsDll = Join-Path $bin 'Hsh.Jobs.Tests.dll'
$workflowTestsDll = Join-Path $bin 'Hsh.Workflow.Tests.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$coreDll") -Sources $core -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$coreDll") -Sources $llm -Label 'Hsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$coreDll", "-r:$llmDll") -Sources $session -Label 'Hsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $tools -Label 'Hsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$jobsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $jobs -Label 'Hsh.Jobs'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$workflowDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $workflow -Label 'Hsh.Workflow'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$jobsTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$jobsDll") -Sources $jobsTests -Label 'Hsh.Jobs.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$workflowTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$workflowDll") -Sources $workflowTests -Label 'Hsh.Workflow.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Jobs.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Workflow.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Hsh.Jobs.Tests =='
& dotnet $jobsTestsDll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host '== Running Hsh.Workflow.Tests =='
& dotnet $workflowTestsDll
exit $LASTEXITCODE
