$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetExe = Join-Path $projectRoot '.dotnet\dotnet.exe'
$cliRoot = Join-Path $projectRoot '.cli-home'
$appDataRoot = Join-Path $cliRoot 'AppData\Roaming'

if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw 'Project-local .NET SDK was not found. See README.md.'
}

New-Item -ItemType Directory -Force -Path (Join-Path $appDataRoot 'NuGet') | Out-Null
$env:DOTNET_CLI_HOME = $cliRoot
$env:APPDATA = $appDataRoot
$env:NUGET_PACKAGES = Join-Path $cliRoot 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnetExe restore '.\GachaOps.slnx' --configfile '.\NuGet.Config' --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
    & $dotnetExe build '.\GachaOps.slnx' --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
