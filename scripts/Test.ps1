[CmdletBinding()]
param([switch]$IncludeModel)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Dotnet = Join-Path $ProjectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:BIRD_UI_SCREENSHOT_DIR = Join-Path $ProjectRoot 'artifacts/test-visuals'
if ($IncludeModel) {
    $env:BIRD_MODEL_PATH = Join-Path $ProjectRoot 'models/yolov10n.onnx'
    $env:BIRD_SAMPLE_PATH = Join-Path $ProjectRoot 'artifacts/samples/great-tit.jpg'
    if (-not (Test-Path -LiteralPath $env:BIRD_MODEL_PATH) -or -not (Test-Path -LiteralPath $env:BIRD_SAMPLE_PATH)) {
        throw 'Model and local verification sample must exist. See artifacts/samples/ATTRIBUTION.md.'
    }
}
Push-Location $ProjectRoot
try {
    & $Dotnet restore Tests/BirdPhotoSelector.SmokeTests.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Test dependency restore failed.' }
    & $Dotnet run --project Tests/BirdPhotoSelector.SmokeTests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
}
finally {
    Pop-Location
    if ($IncludeModel) { Remove-Item Env:BIRD_MODEL_PATH,Env:BIRD_SAMPLE_PATH -ErrorAction SilentlyContinue }
}
