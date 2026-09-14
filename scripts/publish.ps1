$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetExe = Join-Path $projectRoot '.dotnet\dotnet.exe'
$cliRoot = Join-Path $projectRoot '.cli-home'
$appDataRoot = Join-Path $cliRoot 'AppData\Roaming'
$publishRoot = Join-Path $projectRoot 'artifacts\GachaOps-win-x64'

if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw 'Project-local .NET SDK was not found. See README.md.'
}

New-Item -ItemType Directory -Force -Path (Join-Path $appDataRoot 'NuGet') | Out-Null
$env:DOTNET_CLI_HOME = $cliRoot
$env:APPDATA = $appDataRoot
$env:NUGET_PACKAGES = Join-Path $cliRoot 'publish-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnetExe restore '.\src\GachaOps.App\GachaOps.App.csproj' -r win-x64 --configfile '.\NuGet.Config'
    if ($LASTEXITCODE -ne 0) { throw "Publish restore failed with exit code $LASTEXITCODE." }
    & $dotnetExe publish '.\src\GachaOps.App\GachaOps.App.csproj' -c Release -r win-x64 `
        --self-contained true --no-restore -p:PublishSingleFile=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    Write-Host "Published: $publishRoot\GachaOps.exe"
}
finally {
    Pop-Location
}
