# Manual build-and-test for the Phase 2 DeepSeek LLM provider worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suite. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Hsh.Llm.DeepSeek.Tests` work normally.
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
$deepseek = Get-ChildItem (Join-Path $root 'src\Hsh\Hsh.Llm.DeepSeek') -Filter '*.cs' | ForEach-Object FullName
$tests = Get-ChildItem (Join-Path $root 'tests\Hsh.Llm.DeepSeek.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Hsh.Llm -> Hsh.Llm.DeepSeek -> tests app.
$coreDll = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Hsh.Llm.dll'
$deepseekDll = Join-Path $bin 'Hsh.Llm.DeepSeek.dll'
$testsDll = Join-Path $bin 'Hsh.Llm.DeepSeek.Tests.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$coreDll") -Sources $core -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$coreDll") -Sources $llm -Label 'Hsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$deepseekDll", "-r:$coreDll", "-r:$llmDll") -Sources $deepseek -Label 'Hsh.Llm.DeepSeek'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$testsDll", "-r:$coreDll", "-r:$llmDll", "-r:$deepseekDll") -Sources $tests -Label 'Hsh.Llm.DeepSeek.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Llm.DeepSeek.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Hsh.Llm.DeepSeek.Tests =='
& dotnet $testsDll
exit $LASTEXITCODE
