[CmdletBinding()]
param([switch]$Rebuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AssetRoot = Join-Path $ProjectRoot 'models/bioclip2'
$CacheRoot = Join-Path $ProjectRoot '.tools/species-assets'
$PythonRoot = Join-Path $ProjectRoot '.tools/species-python'
$Python = Join-Path $PythonRoot 'python.exe'
$Builder = Join-Path $PSScriptRoot 'Setup-SpeciesModel.py'
$RequirementLock = Join-Path $PSScriptRoot 'SpeciesModel.requirements.txt'

# A prepared distribution does not need Python or an Internet connection to validate.
$HashManifest = Join-Path $AssetRoot 'asset-hashes.json'
if (-not $Rebuild -and (Test-Path -LiteralPath $HashManifest -PathType Leaf)) {
    $Hashes = Get-Content -Raw -LiteralPath $HashManifest | ConvertFrom-Json
    $AllValid = $true
    foreach ($Entry in $Hashes.PSObject.Properties) {
        $AssetPath = [IO.Path]::GetFullPath((Join-Path $AssetRoot $Entry.Name))
        $AssetPrefix = [IO.Path]::GetFullPath($AssetRoot).TrimEnd('\') + '\'
        if (-not $AssetPath.StartsWith($AssetPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid asset hash path.' }
        if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash -ne $Entry.Value) {
            $AllValid = $false
            break
        }
    }
    $RequiredAssets = @('vision.onnx', 'embeddings.f32', 'candidates.json', 'metadata.json')
    $RequiredPresent = @($RequiredAssets | Where-Object { $null -eq $Hashes.PSObject.Properties[$_] }).Count -eq 0
    if ($AllValid -and $RequiredPresent -and @($Hashes.PSObject.Properties).Count -ge 5) {
        Write-Host 'BioCLIP 2 China assets already prepared; every SHA-256 passed.'
        return
    }
}

[IO.Directory]::CreateDirectory($CacheRoot) | Out-Null
[IO.Directory]::CreateDirectory($PythonRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $AssetRoot 'licenses')) | Out-Null
Add-Type -AssemblyName System.Net.Http
$SpeciesHttp = [Net.Http.HttpClient]::new()
$SpeciesHttp.Timeout = [TimeSpan]::FromMinutes(10)

function Get-PinnedFile([string]$Url, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $Temporary = $Destination + '.partial'
    $Response = $SpeciesHttp.GetAsync($Url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $Response.EnsureSuccessStatusCode() | Out-Null
        $Stream = [IO.File]::Create($Temporary)
        try { $Response.Content.CopyToAsync($Stream).GetAwaiter().GetResult() | Out-Null }
        finally { $Stream.Dispose() }
        if ((Get-FileHash -LiteralPath $Temporary -Algorithm SHA256).Hash -ne $Sha256) {
            throw "SHA-256 mismatch: $Destination. Existing final file was preserved."
        }
        Move-Item -LiteralPath $Temporary -Destination $Destination -Force
    }
    finally { $Response.Dispose() }
}

try {
    Get-PinnedFile 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' `
        (Join-Path $CacheRoot 'python-embed.zip') '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
    if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
        Expand-Archive -LiteralPath (Join-Path $CacheRoot 'python-embed.zip') -DestinationPath $PythonRoot -Force
    }
    [IO.File]::WriteAllText((Join-Path $PythonRoot 'python312._pth'), "python312.zip`n.`nLib/site-packages`nimport site`n")
    $PipWheel = Join-Path $CacheRoot 'pip-25.0.1-py3-none-any.whl'
    if (-not (Test-Path -LiteralPath (Join-Path $PythonRoot 'pip/__main__.py') -PathType Leaf)) {
        $PipMetadata = $SpeciesHttp.GetStringAsync('https://pypi.org/pypi/pip/25.0.1/json').GetAwaiter().GetResult() | ConvertFrom-Json
        $PipDownload = $PipMetadata.urls | Where-Object filename -eq 'pip-25.0.1-py3-none-any.whl' | Select-Object -First 1
        Get-PinnedFile $PipDownload.url $PipWheel 'c46efd13b6aa8279f33f2864459c8ce587ea6a1a59ee20de055868d8f7688f7f'
        & $Python -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' $PipWheel $PythonRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare project-local pip.' }
    }
    Get-PinnedFile 'https://www.gbifchina.org.cn/archive.do?r=chinacol2023&v=1.1' `
        (Join-Path $CacheRoot 'china-checklist.zip') '9c17cdea98e210b2acc6ccf482580a22991a568455e0d6d3b58ba0ea5c675d1b'
    Get-PinnedFile 'https://huggingface.co/imageomics/bioclip-2/resolve/2957b322090f9cb17ae72c71981c7218a28d81e0/open_clip_config.json' `
        (Join-Path $CacheRoot 'open_clip_config.json') '1bf947e96e943fe50efd5c3e26c37f843a2fa3c358967719a68c8a6d17ce68c8'
    # The license URLs are additionally content-pinned; a changed upstream text fails closed.
    Get-PinnedFile 'https://raw.githubusercontent.com/Imageomics/bioclip-2/main/LICENSE' `
        (Join-Path $AssetRoot 'licenses/BioCLIP2-MIT.txt') 'a8d48c87f39b50f070f727fe73641d8d9f62f9e8fd9b114f786aaf50cef12265'
    Get-PinnedFile 'https://creativecommons.org/licenses/by-nc/4.0/legalcode.txt' `
        (Join-Path $AssetRoot 'licenses/ChinaChecklist-CC-BY-NC-4.0.txt') '41003d4a74749c0220e33dd415042164b5a1093ed401f36277234f772d22d3d0'
    Get-PinnedFile 'https://creativecommons.org/publicdomain/zero/1.0/legalcode.txt' `
        (Join-Path $AssetRoot 'licenses/TreeOfLife-CC0-1.0.txt') 'a2010f343487d3f7618affe54f789f5487602331c0a8d03f49e9a7c547cf0499'
    Get-PinnedFile 'https://huggingface.co/datasets/imageomics/TreeOfLife-200M/raw/5f2dc493b3dc0e544438a04038ab15faa646b749/README.md' `
        (Join-Path $AssetRoot 'licenses/TreeOfLife-source-card.txt') '4e231609b900ba1c4e0c34c8e130fd14b63fe219031126f208fa9b54df0004d9'
    & $Python -c 'import sys,zipfile,pathlib; pathlib.Path(sys.argv[2]).write_bytes(zipfile.ZipFile(sys.argv[1]).read("eml.xml"))' `
        (Join-Path $CacheRoot 'china-checklist.zip') (Join-Path $AssetRoot 'licenses/ChinaChecklist-v1.1-EML.xml')
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract verified China catalogue attribution.' }
    $TorchWheel = Join-Path $CacheRoot 'torch-2.5.1+cpu-cp312-cp312-win_amd64.whl'
    & $Python $Builder fetch 'https://download-r2.pytorch.org/whl/cpu/torch-2.5.1%2Bcpu-cp312-cp312-win_amd64.whl' `
        $TorchWheel 205405035 'a6b720410350765d3d77c01a5ce098a6c45af446284e45e87a98b8a16e7d564d'
    if ($LASTEXITCODE -ne 0) { throw 'CPU PyTorch download failed.' }
    & $Python -m pip install --isolated --disable-pip-version-check --no-cache-dir --no-warn-script-location --no-deps --upgrade `
        --target (Join-Path $PythonRoot 'Lib/site-packages') --index-url https://pypi.org/simple `
        --extra-index-url https://download.pytorch.org/whl/cpu --requirement $RequirementLock $TorchWheel
    if ($LASTEXITCODE -ne 0) { throw 'Project-local species export dependencies could not be installed.' }
    foreach ($Asset in @('weights', 'labels', 'text')) {
        & $Python $Builder download $Asset
        if ($LASTEXITCODE -ne 0) { throw "Species source download failed: $Asset" }
    }
    & $Python $Builder candidates
    if ($LASTEXITCODE -ne 0) { throw 'China candidate generation failed.' }
    & $Python $Builder export
    if ($LASTEXITCODE -ne 0) { throw 'BioCLIP 2 ONNX export failed.' }
    & $Python $Builder metadata
    if ($LASTEXITCODE -ne 0) { throw 'Species asset metadata generation failed.' }
    Write-Host "Prepared offline species assets: $AssetRoot"
    Write-Host 'Read the model MIT and China-candidate CC-BY-NC-4.0 notices before redistribution.'
}
finally { $SpeciesHttp.Dispose() }
