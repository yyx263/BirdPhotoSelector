[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pinned publisher revision and LFS SHA-256:
# https://huggingface.co/onnx-community/YOLOv10/commit/305deee9272dc7082fbc36e9f424a38d6ef23b8e
$ModelRevision = '305deee9272dc7082fbc36e9f424a38d6ef23b8e'
$ExpectedSha256 = 'a77dd863933f184a19e84361c64b788228a7c7dacc2c78939239a96ad3efca3b'
$ExpectedSize = 9386116L
$SourceUrl = "https://huggingface.co/onnx-community/YOLOv10/resolve/$ModelRevision/yolov10n.onnx"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ModelDirectory = Join-Path $ProjectRoot 'models'
$ModelPath = Join-Path $ModelDirectory 'yolov10n.onnx'

function Test-ModelFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $ExpectedSize) { return $false }
    # ONNX is protobuf and has no universal magic. This pinned model starts with
    # field 1 (IR version). The exact length and SHA-256 below are authoritative.
    $FileStream = [System.IO.File]::OpenRead($Path)
    try { if ($FileStream.ReadByte() -ne 0x08) { return $false } }
    finally { $FileStream.Dispose() }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals(
        $ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)
}

if (Test-ModelFile $ModelPath) {
    Write-Host "Model already installed and verified: $ModelPath"
    return
}

[System.IO.Directory]::CreateDirectory($ModelDirectory) | Out-Null
$PartialPath = Join-Path $ModelDirectory ("yolov10n.onnx.{0}.partial" -f [Guid]::NewGuid().ToString('N'))
try {
    Write-Host 'Downloading YOLOv10n ONNX (AGPL-3.0), approximately 9.4 MB...'
    Write-Host "Source revision: $ModelRevision"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $PreviousProgressPreference = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $SourceUrl -OutFile $PartialPath -UseBasicParsing -TimeoutSec 180
    }
    finally { $ProgressPreference = $PreviousProgressPreference }
    if (-not (Test-ModelFile $PartialPath)) {
        throw 'The model download failed validation (size, ONNX header, or SHA-256). The existing model was preserved.'
    }
    # The temporary file is on the same volume for an atomic replacement.
    if (Test-Path -LiteralPath $ModelPath -PathType Leaf) {
        [System.IO.File]::Replace($PartialPath, $ModelPath, $null)
    }
    else { [System.IO.File]::Move($PartialPath, $ModelPath) }
    Write-Host "Model installed and SHA-256 verified: $ModelPath"
    Write-Host 'Read THIRD_PARTY_NOTICES.md and the upstream AGPL-3.0 license before redistribution.'
    Write-Host 'Rebuild the application to copy this model into the published application folder.'
}
finally {
    # This exact temporary path was generated above inside the project model folder.
    if (Test-Path -LiteralPath $PartialPath -PathType Leaf) { Remove-Item -LiteralPath $PartialPath -Force }
}
