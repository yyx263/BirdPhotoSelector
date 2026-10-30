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
$Version = '0.22.3'
$ArchiveHash = '4e2213a0b10e32199a30b9114a61376cdb40f94df861262b4a6dca659701276b'
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
    $Uri = [Uri]"https://www.libraw.org/data/LibRaw-$Version-Win64.zip"
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
$Fingerprint = (Get-Sha256 $Source) + ':' + (Get-Sha256 $PSCommandPath) + ':' + $ArchiveHash
$Stamp = Join-Path $OutputRoot 'build-fingerprint.txt'
if (-not (Test-Path -LiteralPath $Output) -or -not (Test-Path -LiteralPath $Stamp) -or
    (Get-Content -LiteralPath $Stamp -Raw).Trim() -ne $Fingerprint) {
    $Object = Join-Path $OutputRoot 'RawDecoder.obj'
    $Implib = Join-Path $OutputRoot 'BirdPhotoSelector.Raw.lib'
    # Only fixed, quoted build paths are passed to cmd to initialize MSVC.
    $Command = 'call "{0}" >nul && cl.exe /nologo /EHsc /std:c++17 /utf-8 /O2 /MD /LD /I"{1}" "{2}" /Fo"{3}" /link /OUT:"{4}" /IMPLIB:"{5}" "{6}" && dumpbin.exe /nologo /dependents "{4}" "{7}"' -f
        $Vcvars, $SdkRoot, $Source, $Object, $Output, $Implib, $ImportLibrary, $LibRawDll
    $Log = Join-Path $OutputRoot 'native-build.log'
    & cmd.exe /d /s /c $Command > $Log 2>&1
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $Log -Tail 35
        throw 'Native RAW decoder compilation failed.'
    }
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
