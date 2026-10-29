[CmdletBinding()]
param(
    [switch]$DownloadSamples,
    [string[]]$InputPath = @(),
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [ValidateRange(1,32)][int]$DecoderThreads = 4
)

# Stage-one development probe only. It does not change the application's import,
# project or deletion behavior, and never writes to an input photo.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$LibraryVersion = '0.22.3'
$LibraryUrl = "https://www.libraw.org/data/LibRaw-$LibraryVersion-Win64.zip"
# Fingerprint recorded from the official HTTPS download, not an upstream signature.
$LibrarySha256 = '4e2213a0b10e32199a30b9114a61376cdb40f94df861262b4a6dca659701276b'
$LibraryCache = Join-Path $ProjectRoot ".tools/libraw/$LibraryVersion"
$LibraryRoot = Join-Path $LibraryCache "LibRaw-$LibraryVersion"
$BuildRoot = Join-Path $ProjectRoot '.tools/raw-probe'
$SampleRoot = Join-Path $ProjectRoot '.tools/raw-samples'
$Probe = Join-Path $BuildRoot 'RawCompatibilityProbe.exe'
$ManifestPath = Join-Path $PSScriptRoot 'RawCompatibility.samples.json'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectRoot 'artifacts/raw-compatibility'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$RunId = [Guid]::NewGuid().ToString('N')
$ControlRoot = Join-Path $BuildRoot ('controls/' + $RunId)
$SummaryPath = Join-Path $OutputDirectory 'summary.json'

function Test-InDirectory([string]$Path, [string]$Directory) {
    $Candidate = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\','/'))
    $Root = [IO.Path]::GetFullPath($Directory).TrimEnd([char[]]@('\','/'))
    return [string]::Equals($Candidate, $Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Candidate.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Write-JsonFile($Value, [string]$Path) {
    # A reader sees either the previous checkpoint or this complete JSON document.
    $Temporary = $Path + '.' + $RunId + '.tmp'
    try {
        [IO.File]::WriteAllText($Temporary, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($Temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($Temporary, $Path) }
    }
    finally { if ([IO.File]::Exists($Temporary)) { [IO.File]::Delete($Temporary) } }
}

# Validate every original before creating output directories or replacing any file.
# A per-job check alone cannot protect another job's input from this job's outputs.
$Manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$ManifestIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$ManifestNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$OriginalPaths = [Collections.Generic.List[string]]::new()
foreach ($Sample in $Manifest.Samples) {
    $Name = [string]$Sample.FileName
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::IsPathRooted($Name) -or
        [IO.Path]::GetFileName($Name) -cne $Name -or $Name -in @('.','..') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name.TrimEnd([char[]]@(' ','.')) -cne $Name) {
        throw "Manifest sample FileName must be a safe basename: $Name"
    }
    if ($Sample.Id -notmatch '^[a-zA-Z0-9_-]+$' -or $Sample.Id -match '^(summary$|user-|control-)') {
        throw ('Unsafe or reserved manifest sample identifier: ' + $Sample.Id)
    }
    if (-not $ManifestIds.Add([string]$Sample.Id) -or -not $ManifestNames.Add($Name)) {
        throw 'Manifest sample identifiers and filenames must be unique.'
    }
    if ($Sample.ExpectedStatus -notin @('Decoded','Unsupported','Cancelled','Invalid')) {
        throw ('Unknown expected status: ' + $Sample.ExpectedStatus)
    }
    $OriginalPaths.Add([IO.Path]::GetFullPath((Join-Path $SampleRoot $Name)))
}
$UserPaths = [Collections.Generic.List[string]]::new()
foreach ($UserInput in $InputPath) {
    $Path = [IO.Path]::GetFullPath($UserInput)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Input does not exist: $Path" }
    if ($DownloadSamples -and (Test-InDirectory $Path $SampleRoot)) {
        throw "An explicit input is inside the sample download cache. Omit -DownloadSamples to keep it read-only: $Path"
    }
    $UserPaths.Add($Path)
    $OriginalPaths.Add($Path)
}
foreach ($Path in $OriginalPaths) {
    foreach ($WritableRoot in @($OutputDirectory, $BuildRoot, $LibraryCache)) {
        if (Test-InDirectory $Path $WritableRoot) {
            throw "An original input is inside an output or development-tool directory that may be overwritten: $Path ($WritableRoot)"
        }
    }
}

foreach ($Directory in @($LibraryCache, $BuildRoot, $SampleRoot, $OutputDirectory)) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
}
$Results = [Collections.Generic.List[object]]::new()
$RunErrors = [Collections.Generic.List[object]]::new()
$RunState = @{
    StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');FinishedAtUtc=$null;
    Completed=$false;Stage='Preparing';CurrentJob=$null;PlannedTotal=$null
}

function Write-Checkpoint {
    $PassedCount = @($Results | Where-Object Passed).Count
    $AllPassed = $RunState.Completed -and $Results.Count -gt 0 -and
        $PassedCount -eq $Results.Count -and $RunErrors.Count -eq 0
    $Status = if ($RunState.Completed) { if ($AllPassed) { 'Passed' } else { 'Failed' } }
        elseif ($RunErrors.Count -gt 0) { 'Failed' } else { 'Running' }
    $Summary = [pscustomobject]@{
        SchemaVersion=1;RunId=$RunId;StartedAtUtc=$RunState.StartedAtUtc;
        UpdatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');FinishedAtUtc=$RunState.FinishedAtUtc;
        Completed=[bool]$RunState.Completed;AllPassed=[bool]$AllPassed;Status=$Status;
        Stage=$RunState.Stage;CurrentJob=$RunState.CurrentJob;PlannedTotal=$RunState.PlannedTotal;
        LibraryVersion=$LibraryVersion;LibrarySource=$LibraryUrl;LibraryArchiveSha256=$LibrarySha256;
        RequestedOpenMPThreads=$DecoderThreads;
        FingerprintNote='SDK SHA-256 is a collected fingerprint of the official HTTPS archive, not a publisher signature.';
        SampleManifest=$ManifestPath;ControlDirectory=$ControlRoot;InputFilesAreReadOnly=$true;FullResolutionDemosaic=$true;
        TimingContext='Single runs after input SHA-256 checks; OS file cache may be warm. Memory is native probe process peak, not the full WPF application.';
        PairGeometryValidation='Not established by unrelated RAW samples. Same-shot JPG+RAW, portrait and crop-mode pairs are still required.';
        CameraCoverage='Only the exact sample models and modes in this report; not a brand-wide support claim.';
        Passed=$PassedCount;Total=$Results.Count;Results=@($Results.ToArray());Errors=@($RunErrors.ToArray())
    }
    Write-JsonFile $Summary $SummaryPath
}

# Replace a previous run's successful summary before downloads, builds or probes.
Write-Checkpoint

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$Sha256, [long]$Bytes = 0) {
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        ($Bytes -eq 0 -or (Get-Item -LiteralPath $Destination).Length -eq $Bytes) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $Partial = $Destination + '.partial'
    Write-Host ('Downloading ' + [IO.Path]::GetFileName($Destination))
    # curl uses the caller's HTTPS configuration and preserves certificate checking.
    $TargetUri = [Uri]$Url
    $ProxyUri = [Net.WebRequest]::DefaultWebProxy.GetProxy($TargetUri)
    $ProxyArguments = @()
    if ($null -ne $ProxyUri -and $ProxyUri -ne $TargetUri) { $ProxyArguments = @('--proxy', $ProxyUri.AbsoluteUri) }
    & curl.exe --fail --location --silent --show-error --connect-timeout 20 --max-time 600 --retry 2 @ProxyArguments --output $Partial $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url (exit $LASTEXITCODE)" }
    if (($Bytes -gt 0 -and (Get-Item -LiteralPath $Partial).Length -ne $Bytes) -or
        (Get-FileHash -LiteralPath $Partial -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Size/SHA-256 verification failed; the previous final file was preserved: $Destination"
    }
    Move-Item -LiteralPath $Partial -Destination $Destination -Force
}

try {
$RunState.Stage = 'PreparingSDK'
Write-Checkpoint
$Archive = Join-Path $LibraryCache "LibRaw-$LibraryVersion-Win64.zip"
Get-VerifiedDownload $LibraryUrl $Archive $LibrarySha256
if (-not (Test-Path -LiteralPath (Join-Path $LibraryRoot 'libraw/libraw.h'))) {
    Expand-Archive -LiteralPath $Archive -DestinationPath $LibraryCache -Force
}

if (-not $SkipBuild) {
    $RunState.Stage = 'BuildingProbe'
    Write-Checkpoint
    $Vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $Vswhere)) { throw 'Visual Studio C++ x64 build tools are required for the development probe.' }
    $VsRoot = & $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ([string]::IsNullOrWhiteSpace($VsRoot)) { throw 'Install the Visual Studio C++ x64 build tools before building this probe.' }
    $Vcvars = Join-Path $VsRoot 'VC/Auxiliary/Build/vcvars64.bat'
    $Source = Join-Path $PSScriptRoot 'raw-compatibility-probe.cpp'
    $Object = Join-Path $BuildRoot 'RawCompatibilityProbe.obj'
    $ImportLibrary = Join-Path $LibraryRoot 'lib/libraw.lib'
    # This command only initializes MSVC and compiles fixed paths; no file removal.
    $BuildCommand = 'call "{0}" >nul && cl.exe /nologo /EHsc /std:c++17 /utf-8 /O2 /MD /I"{1}" "{2}" /Fo"{3}" /Fe"{4}" /link "{5}" psapi.lib && dumpbin.exe /nologo /dependents "{6}"' -f
        $Vcvars, $LibraryRoot, $Source, $Object, $Probe, $ImportLibrary, (Join-Path $LibraryRoot 'bin/libraw.dll')
    & cmd.exe /d /s /c $BuildCommand > (Join-Path $OutputDirectory 'native-build.log') 2>&1
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $OutputDirectory 'native-build.log') -Tail 30
        throw 'Native probe compilation failed.'
    }
}
if (-not (Test-Path -LiteralPath $Probe)) { throw 'The native probe has not been built.' }
Copy-Item -LiteralPath (Join-Path $LibraryRoot 'bin/libraw.dll') -Destination (Join-Path $BuildRoot 'libraw.dll') -Force

$RunState.Stage = 'PreparingSamples'
Write-Checkpoint
$SampleJobs = [Collections.Generic.List[object]]::new()
foreach ($Sample in $Manifest.Samples) {
    $Path = Join-Path $SampleRoot $Sample.FileName
    if ($DownloadSamples) { Get-VerifiedDownload $Sample.Url $Path $Sample.Sha256 ([long]$Sample.Bytes) }
    if (-not (Test-Path -LiteralPath $Path)) {
        if ($InputPath.Count -eq 0) { throw "Missing cached sample $Path. Use -DownloadSamples or provide -InputPath." }
        continue
    }
    if (([long]$Sample.Bytes -gt 0 -and (Get-Item -LiteralPath $Path).Length -ne [long]$Sample.Bytes) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sample.Sha256) {
        throw ('Cached RAW failed provenance verification: ' + $Path)
    }
    $SampleJobs.Add([pscustomobject]@{Id=$Sample.Id; Path=$Path; Expected=$Sample.ExpectedStatus; Provenance=$Sample})
}
for ($Index = 0; $Index -lt $UserPaths.Count; $Index++) {
    $Path = $UserPaths[$Index]
    $SampleJobs.Add([pscustomobject]@{Id=('user-' + ($Index + 1)); Path=$Path; Expected='Decoded'; Provenance=$null})
}
if ($SampleJobs.Count -eq 0) { throw 'No input RAW samples were selected.' }

function Invoke-Probe([string]$Id, [string]$Path, [string]$Expected, $Provenance, [switch]$Cancel) {
    if ($Id -notmatch '^[a-zA-Z0-9_-]+$') { throw "Unsafe report identifier: $Id" }
    $RunState.Stage = 'Probing'
    $RunState.CurrentJob = [pscustomobject]@{Id=$Id;InputPath=$Path;ExpectedStatus=$Expected}
    Write-Checkpoint
    $ReportPath = Join-Path $OutputDirectory ($Id + '.json')
    $PreviewPath = Join-Path $OutputDirectory ($Id + '.bmp')
    $Native = $null
    $NativeExit = $null
    $InputHashBefore = $null
    $DriverStage = 'InputValidation'
    try {
    if ([string]::Equals([IO.Path]::GetFullPath($Path), [IO.Path]::GetFullPath($ReportPath), [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals([IO.Path]::GetFullPath($Path), [IO.Path]::GetFullPath($PreviewPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A report or preview path would overwrite the input file.'
    }
    if (Test-Path -LiteralPath $ReportPath -PathType Leaf) { Remove-Item -LiteralPath $ReportPath -Force }
    $InputHashBefore = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $Arguments = @($Path, $ReportPath, $PreviewPath)
    if ($Cancel) { $Arguments += '--cancel-at-unpack' }
    foreach ($Argument in $Arguments) {
        if ($Argument.Contains('"')) { throw 'A native probe argument contains an invalid quote.' }
    }
    $QuotedArguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    # Own the Process handle directly: Windows PowerShell 5.1 Start-Process can
    # otherwise lose ExitCode for a short-lived process returned with -PassThru.
    $StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $StartInfo.FileName = $Probe
    $StartInfo.Arguments = $QuotedArguments
    $StartInfo.UseShellExecute = $false
    $StartInfo.CreateNoWindow = $true
    $StartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    $StartInfo.EnvironmentVariables['OMP_NUM_THREADS'] = [string]$DecoderThreads
    $Process = [Diagnostics.Process]::new()
    $Process.StartInfo = $StartInfo
    $ProcessStarted = $false
    $DriverStage = 'NativeProcess'
    try {
        if (-not $Process.Start()) { throw "Cannot start the native probe: $Id" }
        $ProcessStarted = $true
        $Stdout = $Process.StandardOutput.ReadToEndAsync()
        $Stderr = $Process.StandardError.ReadToEndAsync()
        if (-not $Process.WaitForExit(180000)) {
            # Only terminate the exact development probe created above.
            $Process.Kill()
            $Process.WaitForExit()
            throw "RAW probe exceeded its three-minute limit: $Id"
        }
        $NativeExit = $Process.ExitCode
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Id + '.stdout.log')), $Stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Id + '.stderr.log')), $Stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    }
    finally {
        try {
            # Also stop this exact child if the enclosing script is interrupted.
            if ($ProcessStarted -and -not $Process.HasExited) {
                $Process.Kill()
                $Process.WaitForExit()
            }
        }
        finally { $Process.Dispose() }
    }
    $DriverStage = 'NativeReport'
    if (-not (Test-Path -LiteralPath $ReportPath)) { throw "Probe did not produce a report (exit $NativeExit): $Id" }
    $Native = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $Passed = switch ($Expected) {
        'Decoded' { $NativeExit -eq 0 -and $Native.Status -eq 'Success' -and $Native.DemosaicReached -and
            $Native.Output.Width -gt 0 -and $Native.Output.Height -gt 0 -and $Native.Output.Bits -eq 8 -and
            -not $Native.DecodeSettings.HalfSize -and (Test-Path -LiteralPath $PreviewPath); break }
        'Unsupported' { $NativeExit -eq 1 -and $Native.Status -eq 'Failed' -and $Native.ErrorCode -in @(-2,-8); break }
        'Cancelled' { $NativeExit -eq 2 -and $Native.Status -eq 'Cancelled' -and $Native.CancellationCallbackTriggered; break }
        'Invalid' { $NativeExit -eq 1 -and $Native.Status -eq 'Failed' -and $Native.ErrorCode -in @(-2,-100008,-100009); break }
        default { throw "Unknown expected status: $Expected" }
    }
    $InputHashAfter = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $InputUnchanged = $InputHashBefore -eq $InputHashAfter
    $InputPathReportedCorrectly = [string]::Equals([IO.Path]::GetFullPath($Path), $Native.InputPath, [StringComparison]::OrdinalIgnoreCase)
    $Passed = $Passed -and $InputUnchanged -and $InputPathReportedCorrectly
    $Entry = [pscustomobject]@{Id=$Id;ExpectedStatus=$Expected;Passed=[bool]$Passed;ExitCode=$NativeExit;
        InputBytes=(Get-Item -LiteralPath $Path).Length;
        InputSha256=$InputHashAfter;InputUnchanged=$InputUnchanged;InputPathReportedCorrectly=$InputPathReportedCorrectly;
        Provenance=$Provenance;Native=$Native}
    $Results.Add($Entry)
    $RunState.CurrentJob = $null
    Write-Checkpoint
    Write-Host ("{0}: {1} (expected {2}, passed={3})" -f $Id, $Native.Status, $Expected, $Passed)
    return $Entry
    }
    catch {
        $Failure = $_
        $InputHashAfter = $null
        $InputUnchanged = $null
        $InputBytes = $null
        try {
            $InputBytes = (Get-Item -LiteralPath $Path -ErrorAction Stop).Length
            $InputHashAfter = (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
            if ($null -ne $InputHashBefore) { $InputUnchanged = $InputHashBefore -eq $InputHashAfter }
        }
        catch { }
        $Results.Add([pscustomobject]@{
            Id=$Id;ExpectedStatus=$Expected;Passed=$false;ExitCode=$NativeExit;
            InputPath=$Path;InputBytes=$InputBytes;InputSha256=$InputHashAfter;InputUnchanged=$InputUnchanged;
            InputPathReportedCorrectly=$false;Provenance=$Provenance;Native=$Native;
            DriverStage=$DriverStage;ErrorMessage=$Failure.Exception.Message;ErrorType=$Failure.Exception.GetType().FullName
        })
        Write-Checkpoint
        throw $Failure
    }
}

$HasDecoded = @($SampleJobs | Where-Object Expected -eq 'Decoded').Count -gt 0
$RunState.PlannedTotal = $SampleJobs.Count + 1
if ($HasDecoded) { $RunState.PlannedTotal += 2 }
Write-Checkpoint
foreach ($Job in $SampleJobs) {
    $null = Invoke-Probe $Job.Id $Job.Path $Job.Expected $Job.Provenance
}
$FirstDecoded = $SampleJobs | Where-Object Expected -eq 'Decoded' | Select-Object -First 1
if ($null -ne $FirstDecoded) {
    $null = Invoke-Probe 'control-cancel' $FirstDecoded.Path 'Cancelled' $FirstDecoded.Provenance -Cancel
    # Controls belong to this run only, never to the original/download cache.
    if (Test-Path -LiteralPath $ControlRoot) { throw "Control directory already exists: $ControlRoot" }
    [IO.Directory]::CreateDirectory($ControlRoot) | Out-Null
    $UnicodeRoot = Join-Path $ControlRoot 'Unicode path with spaces'
    [IO.Directory]::CreateDirectory($UnicodeRoot) | Out-Null
    # Build non-ASCII characters without depending on PowerShell 5.1 source encoding.
    $UnicodeName = ([string][char]0x6D4B) + [char]0x8BD5 + ' RAW' + [IO.Path]::GetExtension($FirstDecoded.Path)
    $UnicodePath = Join-Path $UnicodeRoot $UnicodeName
    [IO.File]::Copy($FirstDecoded.Path, $UnicodePath, $false)
    $null = Invoke-Probe 'control-unicode' $UnicodePath 'Decoded' $FirstDecoded.Provenance
}
if (-not (Test-Path -LiteralPath $ControlRoot)) { [IO.Directory]::CreateDirectory($ControlRoot) | Out-Null }
$InvalidPath = Join-Path $ControlRoot 'not-a-raw-file.raw'
$InvalidBytes = [Text.Encoding]::ASCII.GetBytes('This is a deliberately invalid RAW test fixture.')
$InvalidStream = [IO.File]::Open($InvalidPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $InvalidStream.Write($InvalidBytes, 0, $InvalidBytes.Length) }
finally { $InvalidStream.Dispose() }
$null = Invoke-Probe 'control-invalid' $InvalidPath 'Invalid' $null

$RunState.Completed = $true
$RunState.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$RunState.Stage = 'Completed'
$RunState.CurrentJob = $null
Write-Checkpoint
$PassedCount = @($Results | Where-Object Passed).Count
Write-Host ("RAW probe: {0}/{1} expected outcomes passed. Report: {2}" -f $PassedCount, $Results.Count, $SummaryPath)
if ($PassedCount -ne $Results.Count -or $RunErrors.Count -gt 0) { exit 1 }
exit 0
}
catch {
    $RunErrors.Add([pscustomobject]@{
        Stage=$RunState.Stage;Job=$RunState.CurrentJob;Message=$_.Exception.Message;ExceptionType=$_.Exception.GetType().FullName
    })
    $RunState.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Write-Checkpoint
    Write-Error ("RAW probe did not complete. Failed run {0} was checkpointed to {1}: {2}" -f $RunId, $SummaryPath, $_.Exception.Message) -ErrorAction Continue
    exit 1
}
