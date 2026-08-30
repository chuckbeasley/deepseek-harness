# Manual build-and-test for the Phase 0 spike worktree (part 1: pure vocabulary).
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes (the same boundary documented in the harness pwsh tool notes). Restore itself
# works, and the compiler runs fine when spawned with inherited stdio, so this script compiles
# each project directly with csc and runs the zero-dependency console assertion suite. On an
# unrestricted host, `dotnet build` / `dotnet run --project tests\Dsh.Spike.Tests` work normally.
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

$src = Join-Path $root 'src\Dsh'
$llm = Get-ChildItem (Join-Path $src 'Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $src 'Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$spike = Get-ChildItem (Join-Path $src 'Dsh.Spike') -Filter '*.cs' | ForEach-Object FullName
$tests = Get-ChildItem (Join-Path $root 'tests\Dsh.Spike.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Llm first (no project refs), then Session and Tools (-> Llm),
# then Spike (-> all three), then the tests app (-> all four).
Invoke-Csc -ExtraArgs @('-target:library', "-out:$(Join-Path $bin 'Dsh.Llm.dll')") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$(Join-Path $bin 'Dsh.Session.dll')", "-r:$(Join-Path $bin 'Dsh.Llm.dll')") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$(Join-Path $bin 'Dsh.Tools.dll')", "-r:$(Join-Path $bin 'Dsh.Llm.dll')") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Spike.dll')", "-r:$(Join-Path $bin 'Dsh.Session.dll')", "-r:$(Join-Path $bin 'Dsh.Llm.dll')", "-r:$(Join-Path $bin 'Dsh.Tools.dll')") -Sources $spike -Label 'Dsh.Spike'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Dsh.Spike.Tests.dll')", "-r:$(Join-Path $bin 'Dsh.Session.dll')", "-r:$(Join-Path $bin 'Dsh.Llm.dll')", "-r:$(Join-Path $bin 'Dsh.Tools.dll')", "-r:$(Join-Path $bin 'Dsh.Spike.dll')") -Sources $tests -Label 'Dsh.Spike.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Spike.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Dsh.Spike.Tests =='
& dotnet (Join-Path $bin 'Dsh.Spike.Tests.dll')
exit $LASTEXITCODE
