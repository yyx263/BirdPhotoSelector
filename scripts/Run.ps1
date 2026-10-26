[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$PublishedExe = Join-Path $ProjectRoot 'publish/BirdPhotoSelector/BirdPhotoSelector.exe'
if (Test-Path -LiteralPath $PublishedExe -PathType Leaf) {
    & $PublishedExe @AppArgs
    return
}

$LocalDotnet = Join-Path $ProjectRoot '.tools/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $LocalDotnet -PathType Leaf) { $Dotnet = $LocalDotnet }
else {
    $DotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $DotnetCommand) { throw 'No published app or .NET SDK was found. Run scripts/Build.ps1 after installing the .NET 8 SDK.' }
    $Dotnet = $DotnetCommand.Source
}

$PreviousCliHome = [Environment]::GetEnvironmentVariable('DOTNET_CLI_HOME', 'Process')
$PreviousTelemetry = [Environment]::GetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', 'Process')
Push-Location $ProjectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools/dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $Dotnet run --project (Join-Path $ProjectRoot 'BirdPhotoSelector.csproj') --configuration Release -- @AppArgs
    if ($LASTEXITCODE -ne 0) { throw "Application exited with code $LASTEXITCODE." }
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $PreviousCliHome, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', $PreviousTelemetry, 'Process')
    Pop-Location
}
