[CmdletBinding()]
param([string]$ReportPath = 'artifacts/release-package-validation.json')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $ProjectRoot 'publish/BirdPhotoSelector'
[xml]$Project = Get-Content -LiteralPath (Join-Path $ProjectRoot 'BirdPhotoSelector.csproj') -Raw
$Version = [string]$Project.Project.PropertyGroup.Version
[xml]$ApplicationManifest = Get-Content -LiteralPath (Join-Path $ProjectRoot 'app.manifest') -Raw
$Identity = $ApplicationManifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
$VersionParts = [Version]$Version
$ExpectedIdentityVersion = ([Version]::new($VersionParts.Major, $VersionParts.Minor, $VersionParts.Build, 0)).ToString()
if ($null -eq $Identity -or $Identity.GetAttribute('version') -ne $ExpectedIdentityVersion) {
    throw "Application manifest version must match project version: $ExpectedIdentityVersion"
}
$Archive = Join-Path $ProjectRoot "publish/BirdPhotoSelector-$Version-win-x64.zip"
if (Test-Path -LiteralPath $Archive) { throw "Release archive already exists: $Archive" }
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path $ProjectRoot $ReportPath }

function Get-Sha256([string]$Path) {
    $Stream = [IO.File]::OpenRead($Path)
    $Hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($Hash.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $Hash.Dispose(); $Stream.Dispose() }
}
function Write-Json($Value, [string]$Path) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

$Required = @('BirdPhotoSelector.exe', 'BirdPhotoSelector.dll', 'BirdPhotoSelector.Raw.dll', 'libraw.dll',
    'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll',
    'models/yolov10n.onnx', 'models/bioclip2/vision.onnx', 'models/bioclip2/asset-hashes.json',
    'licenses/LibRaw-CDDL-1.0.txt', 'licenses/LibRaw-COPYRIGHT.txt', 'licenses/LibRaw-ComponentNotices.txt',
    'licenses/DotNetRuntime-ThirdPartyNotices.txt', 'THIRD_PARTY_NOTICES.md', '使用说明.md', 'RELEASE_NOTES.md',
    '远程兼容启动.cmd')
foreach ($Name in $Required) {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $Name) -PathType Leaf)) { throw "Required package member missing: $Name" }
}
$Product = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Source 'BirdPhotoSelector.exe')).ProductVersion
if (-not $Product.StartsWith($Version + '+') -and $Product -ne $Version) { throw "Published version does not match $Version : $Product" }

$Prefix = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
# Exclude temporary notes even when an older publish left a docs directory behind.
$Files = @(Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object {
        $RelativePath = $_.FullName.Substring($Prefix.Length).Replace('\', '/')
        $_.Name -ne 'release-manifest.json' -and -not $RelativePath.StartsWith('docs/', [StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($Prefix.Length).Replace('\', '/'); Bytes = $_.Length; SHA256 = Get-Sha256 $_.FullName }
    })
$ByPath = @{}
foreach ($File in $Files) { $ByPath[$File.Path] = $File }
if ($ByPath['models/yolov10n.onnx'].SHA256 -ne 'a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b') {
    throw 'Published YOLO model fingerprint mismatch.'
}
$Expected = Get-Content -LiteralPath (Join-Path $Source 'models/bioclip2/asset-hashes.json') -Raw | ConvertFrom-Json
foreach ($Entry in $Expected.PSObject.Properties) {
    $Name = 'models/bioclip2/' + $Entry.Name.Replace('\', '/')
    if (-not $ByPath.ContainsKey($Name) -or $ByPath[$Name].SHA256 -ne $Entry.Value) { throw "Published BioCLIP resource fingerprint mismatch: $Name" }
}
$Manifest = [pscustomobject]@{ Version = $Version; ProductVersion = $Product; RecordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    Excludes = @('release-manifest.json', 'docs/**'); Files = $Files }
Write-Json $Manifest (Join-Path $Source 'release-manifest.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
# Use the same explicit file list for the manifest and archive; CreateFromDirectory
# would reintroduce ignored temporary docs and any other excluded members.
$Zip = [IO.Compression.ZipFile]::Open($Archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($File in $Files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, (Join-Path $Source $File.Path),
            $File.Path, [IO.Compression.CompressionLevel]::Fastest) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, (Join-Path $Source 'release-manifest.json'),
        'release-manifest.json', [IO.Compression.CompressionLevel]::Fastest) | Out-Null
}
finally { $Zip.Dispose() }
$Zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $EntryNames = @{}
    foreach ($Entry in $Zip.Entries) {
        $Name = $Entry.FullName.Replace('\', '/')
        if ($EntryNames.ContainsKey($Name)) { throw "Duplicate ZIP member: $Name" }
        $EntryNames[$Name] = $Entry
    }
    if ($EntryNames.Count -ne $Files.Count + 1 -or -not $EntryNames.ContainsKey('release-manifest.json')) {
        throw 'ZIP members do not exactly match the manifest and its single self entry.'
    }
    foreach ($File in $Files) {
        if (-not $EntryNames.ContainsKey($File.Path) -or $EntryNames[$File.Path].Length -ne $File.Bytes) {
            throw "ZIP member missing or wrong size: $($File.Path)"
        }
        $Stream = $EntryNames[$File.Path].Open()
        $Hash = [Security.Cryptography.SHA256]::Create()
        try { $ActualHash = ([BitConverter]::ToString($Hash.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
        finally { $Hash.Dispose(); $Stream.Dispose() }
        if ($ActualHash -ne $File.SHA256) { throw "ZIP content fingerprint mismatch: $($File.Path)" }
    }
}
finally { $Zip.Dispose() }
$ArchiveHash = Get-Sha256 $Archive
[IO.File]::WriteAllText($Archive + '.sha256', $ArchiveHash + '  ' + [IO.Path]::GetFileName($Archive) + "`r`n", [Text.UTF8Encoding]::new($false))
$Runtime = Get-Content -LiteralPath (Join-Path $Source 'BirdPhotoSelector.runtimeconfig.json') -Raw | ConvertFrom-Json
$Report = [pscustomobject]@{ Status = 'Passed'; Version = $Version; RecordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o');
    Directory = $Source; Archive = $Archive; ArchiveBytes = (Get-Item -LiteralPath $Archive).Length; ArchiveSHA256 = $ArchiveHash;
    VerifiedFiles = $Files.Count; Frameworks = $Runtime.runtimeOptions.includedFrameworks; ModelsVerified = @('YOLOv10n', 'BioCLIP 2 and catalog resources');
    ZipMembersChecked = $Files.Count + 1; Manifest = (Join-Path $Source 'release-manifest.json') }
Write-Json $Report $ReportPath
Write-Output "Release package verified: $Archive"
Write-Output "SHA-256: $ArchiveHash"
Write-Output "Verified files: $($Files.Count)"
