$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetExe = Join-Path $projectRoot '.dotnet\dotnet.exe'
$cliRoot = Join-Path $projectRoot '.cli-home'
$appDataRoot = Join-Path $cliRoot 'AppData\Roaming'

& (Join-Path $PSScriptRoot 'build.ps1')

$env:DOTNET_CLI_HOME = $cliRoot
$env:APPDATA = $appDataRoot
$env:NUGET_PACKAGES = Join-Path $cliRoot 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnetExe run --project '.\tests\GachaOps.Core.Tests\GachaOps.Core.Tests.csproj' --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
