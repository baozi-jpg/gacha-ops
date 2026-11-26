$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetExe = Join-Path $projectRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
$cliRoot = Join-Path $projectRoot '.cli-home'
$env:DOTNET_CLI_HOME = $cliRoot
$env:APPDATA = Join-Path $cliRoot 'AppData\Roaming'
$env:NUGET_PACKAGES = Join-Path $cliRoot 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnetExe run --file '.\scripts\test-scheduled-start.cs'
    if ($LASTEXITCODE -ne 0) { throw "Workflow runner integration tests failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
