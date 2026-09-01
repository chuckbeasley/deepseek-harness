# Manual build-and-test for the Phase 2 session-spine worktree (persistence / projection / titles).
#
# `dotnet build`/`dotnet test` are blocked by the host sandbox: MSBuild's Csc task spawns the C#
# compiler with captured stdout/stderr, and the sandbox denies child processes that capture output
# through pipes. Restore itself works, and the compiler runs fine when spawned with inherited
# stdio, so this script compiles each project directly with csc and runs the zero-dependency
# console assertion suite. On an unrestricted host, `dotnet build` / `dotnet run --project
# tests\Hsh.Session.Persistence.Tests` work normally.
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
$src = Join-Path $root 'src\Hsh'
$llm = Get-ChildItem (Join-Path $src 'Hsh.Llm') -Filter '*.cs' | ForEach-Object FullName
$session = Get-ChildItem (Join-Path $src 'Hsh.Session') -Filter '*.cs' | ForEach-Object FullName
$persistence = Get-ChildItem (Join-Path $src 'Hsh.Session.Persistence') -Filter '*.cs' | ForEach-Object FullName
$projection = Get-ChildItem (Join-Path $src 'Hsh.Session.Projection') -Filter '*.cs' | ForEach-Object FullName
$titles = Get-ChildItem (Join-Path $src 'Hsh.Session.Titles') -Filter '*.cs' | ForEach-Object FullName
$tests = Get-ChildItem (Join-Path $root 'tests\Hsh.Session.Persistence.Tests') -Filter '*.cs' | ForEach-Object FullName

# Compile in dependency order: Cordis.Core -> Hsh.Llm -> Hsh.Session -> the three new projects -> tests.
$core = Join-Path $bin 'Cordis.Core.dll'
$llmDll = Join-Path $bin 'Hsh.Llm.dll'
$sessionDll = Join-Path $bin 'Hsh.Session.dll'
$persistenceDll = Join-Path $bin 'Hsh.Session.Persistence.dll'
$projectionDll = Join-Path $bin 'Hsh.Session.Projection.dll'
$titlesDll = Join-Path $bin 'Hsh.Session.Titles.dll'

Invoke-Csc -ExtraArgs @('-target:library', "-out:$core") -Sources $cordis -Label 'Cordis.Core'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$llmDll", "-r:$core") -Sources $llm -Label 'Hsh.Llm'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$sessionDll", "-r:$core", "-r:$llmDll") -Sources $session -Label 'Hsh.Session'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$persistenceDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll") -Sources $persistence -Label 'Hsh.Session.Persistence'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$projectionDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll") -Sources $projection -Label 'Hsh.Session.Projection'
Invoke-Csc -ExtraArgs @('-target:library', "-out:$titlesDll", "-r:$core", "-r:$llmDll", "-r:$sessionDll") -Sources $titles -Label 'Hsh.Session.Titles'
Invoke-Csc -ExtraArgs @('-target:exe', "-out:$(Join-Path $bin 'Hsh.Session.Persistence.Tests.dll')", "-r:$core", "-r:$llmDll", "-r:$sessionDll", "-r:$persistenceDll", "-r:$projectionDll", "-r:$titlesDll") -Sources $tests -Label 'Hsh.Session.Persistence.Tests'

# Ship the hand-written replay fixture next to the test assembly.
Copy-Item (Join-Path $root 'tests\Hsh.Session.Persistence.Tests\Fixtures\pinned-session.jsonl') $bin -Force

$runtime = $pack.Name
$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net10.0'
        framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime }
        rollForward = 'LatestMinor'
        configProperties = @{ 'System.Reflection.Metadata.MetadataUpdater.IsSupported' = $false }
    }
}
$runtimeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bin 'Hsh.Session.Persistence.Tests.runtimeconfig.json') -Encoding utf8

Write-Host '== Running Hsh.Session.Persistence.Tests =='
& dotnet (Join-Path $bin 'Hsh.Session.Persistence.Tests.dll')
exit $LASTEXITCODE
