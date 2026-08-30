# Manual build-and-test for the Phase 4 goal + schedule + feedback capability seams.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes (the same boundary documented in the harness pwsh tool notes). Restore itself
# works, and the compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc and runs the zero-dependency console assertion suites. On an
# unrestricted host, `dotnet build` / `dotnet run --project tests\Dsh.Goal.Tests` (and the other
# two test projects) work normally.
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
$core = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Dsh.Llm.dll'
$sessionDll = Join-Path $bin 'Dsh.Session.dll'
$toolsDll = Join-Path $bin 'Dsh.Tools.dll'
$timerDll = Join-Path $bin 'Cordis.Plugin.Timer.dll'
$goalDll = Join-Path $bin 'Dsh.Goal.dll'
$scheduleDll = Join-Path $bin 'Dsh.Schedule.dll'
$feedbackDll = Join-Path $bin 'Dsh.Feedback.dll'

$cordis = Get-ChildItem (Join-Path $src 'Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName
$llm = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$timer = Get-ChildItem (Join-Path $src 'Cordis\Cordis.Plugin.Timer') -Filter '*.cs' | ForEach-Object FullName
$goal = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Goal') -Filter '*.cs' | ForEach-Object FullName
$schedule = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Schedule') -Filter '*.cs' | ForEach-Object FullName
$feedback = Get-ChildItem (Join-Path $src 'Dsh\Dsh.Feedback') -Filter '*.cs' | ForEach-Object FullName
$goalTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Goal.Tests') -Filter '*.cs' | ForEach-Object FullName
$scheduleTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Schedule.Tests') -Filter '*.cs' | ForEach-Object FullName
$feedbackTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Feedback.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Dsh.Llm -> Dsh.Session -> Dsh.Tools ->
# Cordis.Plugin.Timer -> Dsh.Goal -> Dsh.Schedule -> Dsh.Feedback, then the test executables.
Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources $cordis -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$core") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$core", "-r:$llmDll") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$core", "-r:$sessionDll", "-r:$llmDll") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$timerDll", "-r:$core") -Sources $timer -Label 'Cordis.Plugin.Timer'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$goalDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $goal -Label 'Dsh.Goal'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$scheduleDll", "-r:$core", "-r:$timerDll") -Sources $schedule -Label 'Dsh.Schedule'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$feedbackDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $feedback -Label 'Dsh.Feedback'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Goal.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$goalDll") -Sources $goalTests -Label 'Dsh.Goal.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Schedule.Tests.dll')", "-r:$core", "-r:$timerDll", "-r:$scheduleDll") -Sources $scheduleTests -Label 'Dsh.Schedule.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Feedback.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$feedbackDll") -Sources $feedbackTests -Label 'Dsh.Feedback.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Goal.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Schedule.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Feedback.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Dsh.Goal.Tests =='
& dotnet (Join-Path $bin 'Dsh.Goal.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw "Dsh.Goal.Tests failed (exit $LASTEXITCODE)" }

Write-Host '== Running Dsh.Schedule.Tests =='
& dotnet (Join-Path $bin 'Dsh.Schedule.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw "Dsh.Schedule.Tests failed (exit $LASTEXITCODE)" }

Write-Host '== Running Dsh.Feedback.Tests =='
& dotnet (Join-Path $bin 'Dsh.Feedback.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw "Dsh.Feedback.Tests failed (exit $LASTEXITCODE)" }

exit 0
