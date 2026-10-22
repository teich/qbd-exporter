[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$RunNow,
    [switch]$Canary
)

$ErrorActionPreference = 'Stop'
if ($RunNow -and $Canary) { throw 'RunNow and Canary cannot be used together.' }
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path (Join-Path $env:LOCALAPPDATA 'QuickBooksExporter') 'config\config.json'
}

function Write-RunEvent {
    param([Parameter(Mandatory)][string]$Event, [hashtable]$Details = @{})
    $record = [ordered]@{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o'); Event = $Event }
    foreach ($entry in $Details.GetEnumerator()) { $record[$entry.Key] = $entry.Value }
    Add-Content -LiteralPath $script:LogPath -Value ($record | ConvertTo-Json -Compress) -Encoding utf8
}

function Protect-LogMessage {
    param([AllowEmptyString()][string]$Message)
    $protected = $Message
    foreach ($replacement in @(
        @{ Value = [string]$script:Config.CompanyFile; Token = '[company-file]' },
        @{ Value = [string]$script:SnapshotRoot; Token = '[snapshot-root]' },
        @{ Value = [string]$script:Executable; Token = '[exporter-executable]' }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($replacement.Value)) {
            $protected = [regex]::Replace(
                $protected,
                [regex]::Escape($replacement.Value),
                $replacement.Token,
                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    if ($protected.Length -gt 2000) { return $protected.Substring(0, 2000) + '...[truncated]' }
    return $protected
}

trap {
    if (-not [string]::IsNullOrWhiteSpace($script:LogPath)) {
        Write-RunEvent -Event 'initialization-failed' -Details @{
            Mode = $mode
            ErrorType = $_.Exception.GetType().Name
            HResult = ('0x{0:X8}' -f $_.Exception.HResult)
            FailureLine = $_.InvocationInfo.ScriptLineNumber
        }
    }
    exit 1
}

function Invoke-Probe {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $script:ProbeReadStarted = $false
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 wraps native stderr as ErrorRecord objects. With
        # Stop in effect, the first stderr line would abort this function before
        # the native exit code and diagnostic output could be recorded.
        $ErrorActionPreference = 'Continue'
        & $script:Executable @Arguments 2>&1 | ForEach-Object {
            if ($null -ne $_ -and $_.ToString().Length -gt 0) {
                $message = $_.ToString()
                if ($message -match '^Reading [A-Za-z0-9-]+\.\.\.$') { $script:ProbeReadStarted = $true }
                Write-RunEvent -Event 'probe-output' -Details @{ Message = Protect-LogMessage -Message $message }
            }
        }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return $exitCode
}

function Invoke-ScheduledExtraction {
    param([AllowNull()][string]$AttemptPath)
    $attempts = 0
    if ($AttemptPath -and (Test-Path -LiteralPath $AttemptPath)) {
        $previous = Get-Content -LiteralPath $AttemptPath -Raw | ConvertFrom-Json
        $attempts = [int]$previous.Attempts
        if ($previous.Blocked -or $attempts -ge 3) {
            Write-RunEvent -Event 'export-skipped' -Details @{ Reason = $previous.Reason; Attempts = $attempts }
            return $false
        }
    }
    $attempt = [ordered]@{ Attempts = $attempts + 1; Blocked = $true; Reason = 'previous-attempt-incomplete' }
    # Persist before starting: a terminated coordinator must not silently retry this night.
    if ($AttemptPath) { $attempt | ConvertTo-Json | Set-Content -LiteralPath $AttemptPath -Encoding utf8 }
    Write-RunEvent -Event 'export-started'
    $exitCode = Invoke-Probe -Arguments @('snapshot', '--company', $script:Config.CompanyFile, '--output', $script:SnapshotRoot,
        '--page-size', $script:Config.PageSize.ToString(), '--max-minutes', $script:Config.MaxMinutes.ToString())
    $attempt.Blocked = $exitCode -ne 0 -and ($script:ProbeReadStarted -or $attempt.Attempts -ge 3)
    $attempt.Reason = if ($exitCode -eq 0) { 'export-succeeded' } elseif ($script:ProbeReadStarted) { 'extraction-failed-needs-review' } elseif ($attempt.Attempts -ge 3) { 'export-attempt-limit' } else { 'startup-failed-retryable' }
    if ($AttemptPath) { $attempt | ConvertTo-Json | Set-Content -LiteralPath $AttemptPath -Encoding utf8 }
    if ($exitCode -ne 0) {
        Write-RunEvent -Event 'export-failed' -Details @{ ExitCode = $exitCode; Reason = $attempt.Reason; Attempts = $attempt.Attempts; Retryable = -not $attempt.Blocked }
    }
    return $exitCode -eq 0
}

function Set-PostgresConnection {
    if (-not (Test-Path -LiteralPath $script:SecretPath -PathType Leaf)) { return $false }
    $encrypted = (Get-Content -LiteralPath $script:SecretPath -Raw).Trim()
    $secure = ConvertTo-SecureString $encrypted
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $env:QBD_POSTGRES_CONNECTION_STRING = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ($script:Config.PostgresSchema) { $env:QBD_POSTGRES_SCHEMA = $script:Config.PostgresSchema }
    return $true
}

function Send-PendingSnapshots {
    if (-not (Set-PostgresConnection)) {
        Write-RunEvent -Event 'load-skipped' -Details @{ Reason = 'credential-not-configured' }
        return $false
    }
    $allow = [string]::Join(',', [string[]]$script:Config.AllowedUnavailableEntities)
    $cutoff = [DateTimeOffset]::Parse([string]$script:Config.DeliverSnapshotsCompletedAfterUtc, [Globalization.CultureInfo]::InvariantCulture)
    $pending = foreach ($directory in Get-ChildItem -LiteralPath $script:SnapshotRoot -Directory -ErrorAction Stop) {
        if ($directory.Name -notmatch '^[0-9a-f]{32}$' -or (Test-Path -LiteralPath (Join-Path $script:DeliveredRoot ($directory.Name + '.json')))) { continue }
        if (Test-Path -LiteralPath (Join-Path $script:ReviewRoot ($directory.Name + '.json'))) { continue }
        $manifestPath = Join-Path $directory.FullName 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([int]$manifest.FormatVersion -ne 2 -or $manifest.ExportMode -ne 'full') { continue }
        $completed = [DateTimeOffset]::Parse([string]$manifest.CompletedUtc, [Globalization.CultureInfo]::InvariantCulture)
        if ($completed -ge $cutoff) {
            [pscustomobject]@{ Name = $directory.Name; FullName = $directory.FullName; CompletedUtc = $completed }
        }
    }
    # Versioned landing requires source order, independent of directory copy/touch times.
    $pending = $pending | Sort-Object CompletedUtc, Name
    foreach ($snapshot in $pending) {
        Write-RunEvent -Event 'load-started' -Details @{ SnapshotId = $snapshot.Name }
        $arguments = @('postgres', 'load', '--snapshot', $snapshot.FullName)
        if ($allow.Length -gt 0) { $arguments += @('--allow-unavailable', $allow) }
        $exitCode = Invoke-Probe -Arguments $arguments
        if ($exitCode -eq 14) {
            # Permanent chronology rejection is recorded in PostgreSQL by the loader.
            # Keep the export and a distinct review marker; never call it delivered.
            $issue = [ordered]@{ SnapshotId = $snapshot.Name; Reason = 'snapshot-out-of-order'; DetectedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
            $issue | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:ReviewRoot ($snapshot.Name + '.json')) -Encoding utf8
            Write-RunEvent -Event 'load-needs-review' -Details @{ SnapshotId = $snapshot.Name; Reason = 'snapshot-out-of-order' }
            continue
        }
        if ($exitCode -ne 0) {
            Write-RunEvent -Event 'load-failed' -Details @{ SnapshotId = $snapshot.Name; ExitCode = $exitCode }
            return $false
        }
        $marker = [ordered]@{ SnapshotId = $snapshot.Name; DeliveredUtc = [DateTimeOffset]::UtcNow.ToString('o') }
        $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:DeliveredRoot ($snapshot.Name + '.json')) -Encoding utf8
        Write-RunEvent -Event 'load-completed' -Details @{ SnapshotId = $snapshot.Name }
    }
    return $true
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Scheduled export configuration is missing." }
$configRoot = Split-Path -Parent $ConfigPath
$logRoot = Join-Path $configRoot 'logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$script:LogPath = Join-Path $logRoot ([DateTime]::Now.ToString('yyyy-MM') + '.jsonl')
$script:Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$now = Get-Date
$mode = if ($Canary) { 'canary' } elseif ($RunNow) { 'run-now' } else { 'scheduled' }
if ($RunNow -or $Canary) {
    $windowStart = $now
} else {
    $windowStartTime = [TimeSpan]::ParseExact([string]$script:Config.DailyAt, 'hh\:mm', [Globalization.CultureInfo]::InvariantCulture)
    $todayWindowStart = $now.Date.Add($windowStartTime)
    $yesterdayWindowStart = $todayWindowStart.AddDays(-1)
    $windowStart = if ($now -ge $todayWindowStart) { $todayWindowStart } elseif ($now -lt $yesterdayWindowStart.AddMinutes([int]$script:Config.WindowMinutes)) { $yesterdayWindowStart } else { exit 0 }
    $windowEnd = $windowStart.AddMinutes([int]$script:Config.WindowMinutes)
    if ($now -lt $windowStart -or $now -ge $windowEnd) { exit 0 }
}
$script:Executable = [IO.Path]::GetFullPath($script:Config.Executable)
$script:SnapshotRoot = [IO.Path]::GetFullPath($script:Config.SnapshotRoot)
$installRoot = Split-Path -Parent $configRoot
$script:SecretPath = Join-Path $configRoot 'postgres-connection.dpapi'
$stateRoot = Join-Path $configRoot 'state'
$script:DeliveredRoot = Join-Path $stateRoot 'delivered'
$script:ReviewRoot = Join-Path $stateRoot 'needs-review'
New-Item -ItemType Directory -Force -Path $script:SnapshotRoot, $stateRoot, $script:DeliveredRoot, $script:ReviewRoot | Out-Null
$installationPath = Join-Path $installRoot 'installation.json'
$installation = if (Test-Path -LiteralPath $installationPath -PathType Leaf) { Get-Content -LiteralPath $installationPath -Raw | ConvertFrom-Json } else { $null }
$releaseId = if ($installation -and $installation.ReleaseId) { [string]$installation.ReleaseId } else { 'unknown' }
$coordinatorSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash

$lockPath = Join-Path $stateRoot 'coordinator.lock'
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    Write-RunEvent -Event 'run-skipped' -Details @{ Reason = 'coordinator-already-running' }
    exit 0
}

try {
    Write-RunEvent -Event 'run-started' -Details @{ Mode = $mode; ReleaseId = $releaseId; CoordinatorSha256 = $coordinatorSha256 }
    if ($Canary) {
        Write-RunEvent -Event 'canary-completed' -Details @{ ReleaseId = $releaseId; CoordinatorSha256 = $coordinatorSha256 }
        Write-RunEvent -Event 'run-completed' -Details @{ Mode = $mode }
        exit 0
    }
    if (-not (Test-Path -LiteralPath $script:Executable -PathType Leaf)) { throw 'Deployed exporter executable is missing.' }

    $loadsHealthy = Send-PendingSnapshots

    $exportMarker = if ($RunNow) { $null } else { Join-Path $stateRoot ('export-' + $windowStart.ToString('yyyy-MM-dd') + '.json') }
    if ($null -ne $exportMarker -and (Test-Path -LiteralPath $exportMarker -PathType Leaf)) {
        Write-RunEvent -Event 'export-skipped' -Details @{ Reason = 'daily-export-already-completed' }
    } elseif (-not (Test-Path -LiteralPath $script:Config.CompanyFile -PathType Leaf)) {
        Write-RunEvent -Event 'export-skipped' -Details @{ Reason = 'company-file-unavailable' }
    } else {
        $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($script:SnapshotRoot).Substring(0, 1))
        $minimumBytes = [int64]$script:Config.MinimumFreeGiB * 1GB
        if ($drive.Free -lt $minimumBytes) {
            Write-RunEvent -Event 'export-skipped' -Details @{ Reason = 'insufficient-free-space'; MinimumFreeGiB = $script:Config.MinimumFreeGiB }
        } else {
            $attemptPath = if ($RunNow) { $null } else { Join-Path $stateRoot ('attempt-' + $windowStart.ToString('yyyy-MM-dd') + '.json') }
            if (Invoke-ScheduledExtraction -AttemptPath $attemptPath) {
                if ($null -ne $exportMarker) {
                    [ordered]@{ WindowDate = $windowStart.ToString('yyyy-MM-dd'); CompletedUtc = [DateTimeOffset]::UtcNow.ToString('o') } |
                        ConvertTo-Json | Set-Content -LiteralPath $exportMarker -Encoding utf8
                }
                Write-RunEvent -Event 'export-completed'
                if ($loadsHealthy) { $null = Send-PendingSnapshots }
            }
        }
    }
    Write-RunEvent -Event 'run-completed' -Details @{ Mode = $mode }
    exit 0
} catch {
    Write-RunEvent -Event 'run-failed' -Details @{ ErrorType = $_.Exception.GetType().Name; HResult = ('0x{0:X8}' -f $_.Exception.HResult) }
    exit 1
} finally {
    Remove-Item Env:\QBD_POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
    $lock.Dispose()
}
