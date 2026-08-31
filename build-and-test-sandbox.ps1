# Manual build-and-test for the Phase 4 sandbox capability seam core
# (Dsh.Sandbox + the additive ShellSandboxInfo wiring into Dsh.Shell + Dsh.Sandbox.Tests).
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. The compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc in dependency order and runs the zero-dependency console
# assertion suite. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Dsh.Sandbox.Tests` work normally.
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
$src = Join-Path $root 'src\Dsh'
$llm = Get-ChildItem (Join-Path $src 'Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$subprocess = Get-ChildItem (Join-Path $src 'Dsh.Subprocess') -Filter '*.cs' | ForEach-Object FullName
$sandbox = Get-ChildItem (Join-Path $src 'Dsh.Sandbox') -Filter '*.cs' | ForEach-Object FullName
$shell = Get-ChildItem (Join-Path $src 'Dsh.Shell') -Filter '*.cs' | ForEach-Object FullName
$tests = Get-ChildItem (Join-Path $root 'tests\Dsh.Sandbox.Tests') -Filter '*.cs' | ForEach-Object FullName

# Dsh.Subprocess and Dsh.Shell rely on ImplicitUsings=enable but carry no GlobalUsings.cs source
# (the sibling projects do, so they compile with a bare csc invocation). Synthesize the implicit
# usings as a build-local source file from the sibling template and append it to their source
# lists; the sandbox projects ship their own GlobalUsings.cs.
$implicitUsings = Join-Path $bin 'ImplicitUsings.cs'
Copy-Item (Join-Path $root 'src\Cordis\Cordis.Core\GlobalUsings.cs') $implicitUsings -Force
$subprocess += $implicitUsings
$shell += $implicitUsings

# Compile in dependency order: Cordis.Core -> Dsh.Llm -> Dsh.Session -> Dsh.Tools -> Dsh.Subprocess
# -> Dsh.Sandbox -> Dsh.Shell -> tests. Dsh.Sandbox depends only on Cordis.Core; Dsh.Shell gains a
# dependency on Dsh.Sandbox for the ShellRunResult.Sandbox field, so the sandbox library must be
# compiled before the shell library.
$core = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Dsh.Llm.dll'
$sessionDll = Join-Path $bin 'Dsh.Session.dll'
$toolsDll = Join-Path $bin 'Dsh.Tools.dll'
$subprocessDll = Join-Path $bin 'Dsh.Subprocess.dll'
$sandboxDll = Join-Path $bin 'Dsh.Sandbox.dll'
$shellDll = Join-Path $bin 'Dsh.Shell.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources $cordis -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$core") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$core", "-r:$llmDll") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$subprocessDll", "-r:$core") -Sources $subprocess -Label 'Dsh.Subprocess'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sandboxDll", "-r:$core") -Sources $sandbox -Label 'Dsh.Sandbox'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$shellDll", "-r:$core", "-r:$llmDll", "-r:$toolsDll", "-r:$subprocessDll", "-r:$sandboxDll") -Sources $shell -Label 'Dsh.Shell'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Sandbox.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$subprocessDll", "-r:$sandboxDll", "-r:$shellDll") -Sources $tests -Label 'Dsh.Sandbox.Tests'

# The additive ShellRunResult.Sandbox field is exercised by the shell suite (Run constructs the
# record, the bash tool round-trips its result JSON), so it compiles and runs here as a
# compatibility gate. The tests themselves are untouched; only the shell library changed.
$shellTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Shell.Tests') -Filter '*.cs' | ForEach-Object FullName
$shellTests += $implicitUsings
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Shell.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$toolsDll", "-r:$subprocessDll", "-r:$sandboxDll", "-r:$shellDll") -Sources $shellTests -Label 'Dsh.Shell.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Sandbox.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Shell.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Dsh.Sandbox.Tests =='
& dotnet (Join-Path $bin 'Dsh.Sandbox.Tests.dll')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '== Running Dsh.Shell.Tests =='
& dotnet (Join-Path $bin 'Dsh.Shell.Tests.dll')
exit $LASTEXITCODE