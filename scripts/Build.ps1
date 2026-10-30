[CmdletBinding()]
param(
    [switch]$SkipModel,
    [switch]$SkipSpeciesModel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'BirdPhotoSelector.csproj'
$LocalDotnet = Join-Path $ProjectRoot '.tools/dotnet/dotnet.exe'
$OutputDirectory = Join-Path $ProjectRoot 'publish/BirdPhotoSelector'
$PackagesDirectory = Join-Path $ProjectRoot '.tools/packages'
$ModelPath = Join-Path $ProjectRoot 'models/yolov10n.onnx'
$SpeciesDirectory = Join-Path $ProjectRoot 'models/bioclip2'
$SpeciesFiles = @('vision.onnx', 'candidates.json', 'embeddings.f32', 'metadata.json')

if (Test-Path -LiteralPath $LocalDotnet -PathType Leaf) { $Dotnet = $LocalDotnet }
else {
    $DotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $DotnetCommand) {
        throw 'Install the .NET 8 SDK for Windows x64, or extract it into .tools/dotnet. No global tools will be installed by this script.'
    }
    $Dotnet = $DotnetCommand.Source
}

if (-not $SkipModel -and -not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
    throw 'The bird model is missing. Run scripts/Setup-Model.ps1 first, or pass -SkipModel for a build with manual viewing and background grouping.'
}
if (-not $SkipSpeciesModel) {
    foreach ($Name in $SpeciesFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $SpeciesDirectory $Name) -PathType Leaf)) {
            throw 'BioCLIP 2 or China bird candidates are missing. Run scripts/Setup-SpeciesModel.ps1 first, or pass -SkipSpeciesModel to build without species recognition assets.'
        }
    }
}

$PreviousCliHome = [Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')
$PreviousTelemetry = [Environment]::GetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', 'Process')
Push-Location $ProjectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools/dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $Dotnet restore $ProjectFile --runtime win-x64 --packages $PackagesDirectory --configfile (Join-Path $ProjectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed (exit $LASTEXITCODE)." }
    & $Dotnet publish $ProjectFile --configuration Release --runtime win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'BirdPhotoSelector.exe') -PathType Leaf)) {
        throw 'Publish did not produce BirdPhotoSelector.exe.'
    }
    foreach ($Dependency in @('BirdPhotoSelector.Raw.dll', 'libraw.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
            'THIRD_PARTY_NOTICES.md', 'licenses/LibRaw-CDDL-1.0.txt', 'licenses/LibRaw-COPYRIGHT.txt', 'RELEASE_NOTES.md', '远程兼容启动.cmd')) {
        if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $Dependency) -PathType Leaf)) {
            throw "Publish did not include required RAW/runtime/documentation file: $Dependency"
        }
    }
    if (-not $SkipModel -and -not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'models/yolov10n.onnx') -PathType Leaf)) {
        throw 'Publish did not include models/yolov10n.onnx.'
    }
    if (-not $SkipSpeciesModel) {
        foreach ($Name in $SpeciesFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory ('models/bioclip2/' + $Name)) -PathType Leaf)) {
                throw "Publish did not include BioCLIP 2 asset: $Name"
            }
        }
    }
    Write-Host "Published: $OutputDirectory"
    Write-Host 'Distribute this entire folder. The target PC does not need a separately installed .NET runtime.'
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $PreviousCliHome, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', $PreviousTelemetry, 'Process')
    Pop-Location
}
