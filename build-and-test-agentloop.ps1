$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build "$root\tests\Hsh.AgentLoop.Tests\Hsh.AgentLoop.Tests.csproj" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$root\tests\Hsh.AgentLoop.Tests\Hsh.AgentLoop.Tests.csproj" --no-build
exit $LASTEXITCODE
