# Manual build-and-test for the Phase 4 identity + plan capability seams.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes (the same boundary documented in the harness pwsh tool notes). Restore itself
# works, and the compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc and runs the zero-dependency console assertion suites. On an
# unrestricted host, `dotnet build` / `dotnet run --project tests\Hsh.Identity.Tests` and
# `tests\Hsh.Plan.Tests` work normally.
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

$cordis = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Core') -Filter '*.cs' | ForEach-Object FullName
$cosmokit = Get-ChildItem (Join-Path $root 'src\Cordis\Cordis.Cosmokit') -Filter '*.cs' | ForEach-Object FullName
$src = Join-Path $root 'src\Hsh'
$llm = Get-ChildItem (Join-Path $src 'Hsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Hsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Hsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$identity = Get-ChildItem (Join-Path $src 'Hsh.Identity') -Filter '*.cs' | ForEach-Object FullName
$plan = Get-ChildItem (Join-Path $src 'Hsh.Plan') -Filter '*.cs' | ForEach-Object FullName
$identityTests = Get-ChildItem (Join-Path $root 'tests\Hsh.Identity.Tests') -Filter '*.cs' | ForEach-Object FullName
$planTests = Get-ChildItem (Join-Path $root 'tests\Hsh.Plan.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Cordis.Cosmokit -> Hsh.Llm -> Hsh.Session ->
# Hsh.Tools -> Hsh.Identity -> Hsh.Plan, then both test executables.
$core = Join-Path $bin 'Cordis.Core.dll'
$cosmokitDll = Join-Path $bin 'Cordis.Cosmokit.dll'
$llmDll = Join-Path $bin 'Hsh.Llm.dll'
$sessionDll = Join-Path $bin 'Hsh.Session.dll'
$toolsDll = Join-Path $bin 'Hsh.Tools.dll'
$identityDll = Join-Path $bin 'Hsh.Identity.dll'
$planDll = Join-Path $bin 'Hsh.Plan.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources $cordis -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$cosmokitDll") -Sources $cosmokit -Label 'Cordis.Cosmokit'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$core") -Sources $llm -Label 'Hsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$core", "-r:$llmDll") -Sources $session -Label 'Hsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$core", "-r:$sessionDll", "-r:$llmDll") -Sources $tools -Label 'Hsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$identityDll", "-r:$core", "-r:$cosmokitDll", "-r:$llmDll") -Sources $identity -Label 'Hsh.Identity'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$planDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll") -Sources $plan -Label 'Hsh.Plan'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Hsh.Identity.Tests.dll')", "-r:$core", "-r:$cosmokitDll", "-r:$llmDll", "-r:$identityDll") -Sources $identityTests -Label 'Hsh.Identity.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Hsh.Plan.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$planDll") -Sources $planTests -Label 'Hsh.Plan.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Identity.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Plan.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Hsh.Identity.Tests =='
& dotnet (Join-Path $bin 'Hsh.Identity.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw "Hsh.Identity.Tests failed (exit $LASTEXITCODE)" }

Write-Host '== Running Hsh.Plan.Tests =='
& dotnet (Join-Path $bin 'Hsh.Plan.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw "Hsh.Plan.Tests failed (exit $LASTEXITCODE)" }

exit 0
