$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet build "$root\tests\Hsh.Cli.Tests\Hsh.Cli.Tests.csproj" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project "$root\tests\Hsh.Cli.Tests\Hsh.Cli.Tests.csproj" --no-build
exit $LASTEXITCODE
