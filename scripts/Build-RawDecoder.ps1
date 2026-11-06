[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# dotnet may inherit PowerShell 7's PSModulePath before launching Windows
# PowerShell 5.1. Put this host's built-in modules first (Archive/Utility).
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath
function Get-Sha256([string]$Path) {
    $Hasher = [Security.Cryptography.SHA256]::Create()
    $Stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($Hasher.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $Stream.Dispose(); $Hasher.Dispose() }
}
$ProjectRoot = Split-Path -Parent $PSScriptRoot
# 与 Unix 源码构建共用固定版本清单，避免下载源和校验值在多个入口漂移。
$Dependencies = Get-Content -LiteralPath (Join-Path $ProjectRoot 'native/dependencies.json') -Raw | ConvertFrom-Json
$Version = $Dependencies.LibRawVersion
$ArchiveHash = $Dependencies.WindowsSdkSha256
$Cache = Join-Path $ProjectRoot ".tools/libraw/$Version"
$SdkRoot = Join-Path $Cache "LibRaw-$Version"
$Archive = Join-Path $Cache "LibRaw-$Version-Win64.zip"
$OutputRoot = Join-Path $ProjectRoot '.tools/raw-decoder'
$Output = Join-Path $OutputRoot 'BirdPhotoSelector.Raw.dll'
$Source = Join-Path $ProjectRoot 'native/raw-decoder.cpp'
[IO.Directory]::CreateDirectory($Cache) | Out-Null
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null

if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
    $Partial = $Archive + '.partial'
    $Uri = [Uri]$Dependencies.WindowsSdkUrl
    $Proxy = [Net.WebRequest]::DefaultWebProxy.GetProxy($Uri)
    $ProxyArguments = @()
    if ($null -ne $Proxy -and $Proxy -ne $Uri) { $ProxyArguments = @('--proxy', $Proxy.AbsoluteUri) }
    & curl.exe --fail --location --retry 2 --silent --show-error @ProxyArguments --output $Partial $Uri.AbsoluteUri
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the fixed LibRaw Win64 SDK.' }
    if ((Get-Sha256 $Partial) -ne $ArchiveHash) { throw 'LibRaw archive SHA-256 mismatch.' }
    Move-Item -LiteralPath $Partial -Destination $Archive -Force
}
if ((Get-Sha256 $Archive) -ne $ArchiveHash) { throw 'Cached LibRaw archive SHA-256 mismatch.' }
if (-not (Test-Path -LiteralPath (Join-Path $SdkRoot 'libraw/libraw.h'))) {
    Expand-Archive -LiteralPath $Archive -DestinationPath $Cache -Force
}
$LibRawDll = Join-Path $SdkRoot 'bin/libraw.dll'
$ImportLibrary = Join-Path $SdkRoot 'lib/libraw.lib'
if ((Get-Sha256 $LibRawDll) -ne '2c4df36d9011267fd3ffd5c23e80d69d5f42c3d503f03ac02448f5c8a7fe0fa2' -or
    (Get-Sha256 $ImportLibrary) -ne 'b1d95da71f15ca145bada42e8afed4acedd8d1785a7cd7cf696bbca7acfa380f') {
    throw 'Extracted LibRaw binary/import library fingerprint mismatch; restore them from the verified archive.'
}

$Vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $Vswhere)) { throw 'Install Visual Studio C++ x64 build tools to build the RAW decoder.' }
$VsRoot = & $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($VsRoot)) { throw 'Visual Studio C++ x64 build tools were not found.' }
$Vcvars = Join-Path $VsRoot 'VC/Auxiliary/Build/vcvars64.bat'
$Cmake = Join-Path $VsRoot 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if (-not (Test-Path -LiteralPath $Cmake)) { throw 'Install the Visual Studio CMake tools component.' }
$BuildRoot = Join-Path $ProjectRoot '.tools/native-build/win-x64'
$PlatformOutput = Join-Path $OutputRoot 'BirdPhotoSelector.Platform.Native.dll'
$NativeInputs = @('native/raw-decoder.cpp', 'native/platform.cpp', 'native/bridge-api.h', 'native/CMakeLists.txt', 'native/dependencies.json')
$Fingerprint = (($NativeInputs | ForEach-Object { Get-Sha256 (Join-Path $ProjectRoot $_) }) -join ':') + ':' + (Get-Sha256 $PSCommandPath) + ':' + $ArchiveHash
$Stamp = Join-Path $OutputRoot 'build-fingerprint.txt'
if (-not (Test-Path -LiteralPath $Output) -or -not (Test-Path -LiteralPath $PlatformOutput) -or
    -not (Test-Path -LiteralPath $Stamp) -or (Get-Content -LiteralPath $Stamp -Raw).Trim() -ne $Fingerprint) {
    # Fixed quoted build paths only; no user filenames are interpolated into a shell command.
    $Command = 'call "{0}" >nul && "{1}" -S "{2}" -B "{3}" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release -DLIBRAW_ROOT="{4}" -DCMAKE_INSTALL_PREFIX="{5}" && "{1}" --build "{3}" --target install' -f
        $Vcvars, $Cmake, (Join-Path $ProjectRoot 'native'), $BuildRoot, $SdkRoot, $OutputRoot
    $Log = Join-Path $OutputRoot 'native-build.log'
    & cmd.exe /d /s /c $Command > $Log 2>&1
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $Log -Tail 45; throw 'CMake native compilation failed.' }
    [IO.File]::WriteAllText($Stamp, $Fingerprint)
}
Copy-Item -LiteralPath $LibRawDll -Destination (Join-Path $OutputRoot 'libraw.dll') -Force

# App-local redistributable runtime: the resulting folder works without installing VS.
$RedistBase = Join-Path $VsRoot 'VC/Redist/MSVC'
$Redist = Get-ChildItem -LiteralPath $RedistBase -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+$' |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $Redist) { throw 'MSVC x64 redistributable runtime files were not found.' }
$Crt = Get-ChildItem -LiteralPath (Join-Path $Redist.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
if ($null -eq $Crt) { throw 'MSVC CRT redistributable directory was not found.' }
foreach ($Runtime in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    Copy-Item -LiteralPath (Join-Path $Crt.FullName $Runtime) -Destination $OutputRoot -Force
}
Write-Output "RAW decoder ready: $Output"
