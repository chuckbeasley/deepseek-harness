# Manual build-and-test for the Phase 4 web + credentials capability seams worktree.
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suites. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Dsh.Web.Tests` (and the credentials peer) work normally.
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
$llm = Get-ChildItem (Join-Path $root 'src\Dsh\Dsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $root 'src\Dsh\Dsh.Session') -Filter '*.cs' | ForEach-Object FullName
$tools = Get-ChildItem (Join-Path $root 'src\Dsh\Dsh.Tools') -Filter '*.cs' | ForEach-Object FullName
$web = Get-ChildItem (Join-Path $root 'src\Dsh\Dsh.Web') -Filter '*.cs' | ForEach-Object FullName
$credentials = Get-ChildItem (Join-Path $root 'src\Dsh\Dsh.Credentials') -Filter '*.cs' | ForEach-Object FullName
$webTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Web.Tests') -Filter '*.cs' | ForEach-Object FullName
$credentialsTests = Get-ChildItem (Join-Path $root 'tests\Dsh.Credentials.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Dsh.Llm -> Dsh.Session -> Dsh.Tools ->
# Dsh.Web / Dsh.Credentials -> both test apps.
$coreDll = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Dsh.Llm.dll'
$sessionDll = Join-Path $bin 'Dsh.Session.dll'
$toolsDll = Join-Path $bin 'Dsh.Tools.dll'
$webDll = Join-Path $bin 'Dsh.Web.dll'
$credentialsDll = Join-Path $bin 'Dsh.Credentials.dll'
$webTestsDll = Join-Path $bin 'Dsh.Web.Tests.dll'
$credentialsTestsDll = Join-Path $bin 'Dsh.Credentials.Tests.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$coreDll") -Sources $core -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$coreDll") -Sources $llm -Label 'Dsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$coreDll", "-r:$llmDll") -Sources $session -Label 'Dsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$toolsDll", "-r:$coreDll", "-r:$llmDll", "-r:$sessionDll") -Sources $tools -Label 'Dsh.Tools'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$webDll", "-r:$coreDll", "-r:$llmDll", "-r:$toolsDll") -Sources $web -Label 'Dsh.Web'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$credentialsDll", "-r:$coreDll") -Sources $credentials -Label 'Dsh.Credentials'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$webTestsDll", "-r:$coreDll", "-r:$llmDll", "-r:$toolsDll", "-r:$webDll") -Sources $webTests -Label 'Dsh.Web.Tests'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$credentialsTestsDll", "-r:$coreDll", "-r:$credentialsDll") -Sources $credentialsTests -Label 'Dsh.Credentials.Tests'

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Web.Tests.runtimeconfig.json') -Encoding utf8
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Dsh.Credentials.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Dsh.Web.Tests =='
& dotnet $webTestsDll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host '== Running Dsh.Credentials.Tests =='
& dotnet $credentialsTestsDll
exit $LASTEXITCODE
