[CmdletBinding()]
param(
    [string]$CompanyFile,
    [string]$SnapshotRoot,
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$DailyAt,
    [ValidateRange(15, 720)][int]$WindowMinutes,
    [ValidateRange(5, 60)][int]$HeartbeatMinutes,
    [ValidateRange(1, 1024)][int]$MinimumFreeGiB,
    [ValidateRange(1, 1440)][int]$MaxMinutes,
    [ValidateRange(1, 5000)][int]$PageSize,
    [string]$PostgresSchema,
    [AllowEmptyCollection()][string[]]$AllowedUnavailableEntities,
    [switch]$ResetCredential
)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'QuickBooksExporter'
$configRoot = Join-Path $installRoot 'config'
$configPath = Join-Path $configRoot 'config.json'
$secretPath = Join-Path $configRoot 'postgres-connection.dpapi'
$legacyRoot = Join-Path $PSScriptRoot 'private\scheduled-export'
$legacyConfigPath = Join-Path $legacyRoot 'config.json'
$legacySecretPath = Join-Path $legacyRoot 'postgres-connection.dpapi'
$taskPath = '\QuickBooksExporter\'
$schedulerFolderPath = '\QuickBooksExporter'
$taskName = 'Nightly Export'

foreach ($scheduled in @(
    Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
    Get-ScheduledTask -TaskName 'QuickBooks Desktop Export and Load' -ErrorAction SilentlyContinue
)) {
    if ($scheduled.State -eq 'Running') { throw 'The existing exporter task is running; update after it completes.' }
}

$existingConfigPath = if (Test-Path -LiteralPath $configPath -PathType Leaf) { $configPath } elseif (Test-Path -LiteralPath $legacyConfigPath -PathType Leaf) { $legacyConfigPath } else { $null }
$existing = if ($existingConfigPath) { Get-Content -LiteralPath $existingConfigPath -Raw | ConvertFrom-Json } else { $null }

if (-not $PSBoundParameters.ContainsKey('CompanyFile')) {
    if ($existing -and $existing.CompanyFile) {
        $CompanyFile = [string]$existing.CompanyFile
    } else {
        $companyRoot = Split-Path -Parent $PSScriptRoot
        $companyCandidates = @(Get-ChildItem -LiteralPath $companyRoot -Filter '*.qbw' -File -ErrorAction SilentlyContinue)
        if ($companyCandidates.Count -eq 1) {
            $CompanyFile = $companyCandidates[0].FullName
            Write-Output 'Using the single top-level QuickBooks company file found beside the repository.'
        } else {
            $CompanyFile = Read-Host 'Absolute QuickBooks company file (.qbw)'
        }
    }
}
if ($CompanyFile -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetExtension($CompanyFile) -ine '.qbw' -or -not (Test-Path -LiteralPath $CompanyFile -PathType Leaf)) {
    throw 'CompanyFile must be an existing absolute .qbw path.'
}

if (-not $PSBoundParameters.ContainsKey('SnapshotRoot')) { $SnapshotRoot = if ($existing -and $existing.SnapshotRoot) { [string]$existing.SnapshotRoot } else { Join-Path $env:USERPROFILE 'qbd-snapshots' } }
if ($SnapshotRoot -notmatch '^[A-Za-z]:\\') { throw 'SnapshotRoot must be an absolute local-drive path.' }
if (-not $PSBoundParameters.ContainsKey('DailyAt')) { $DailyAt = if ($existing -and $existing.DailyAt) { [string]$existing.DailyAt } else { '02:00' } }
if ($DailyAt -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { throw 'DailyAt must use HH:mm.' }
if (-not $PSBoundParameters.ContainsKey('WindowMinutes')) { $WindowMinutes = if ($existing -and $existing.WindowMinutes) { [int]$existing.WindowMinutes } else { 180 } }
if (-not $PSBoundParameters.ContainsKey('HeartbeatMinutes')) { $HeartbeatMinutes = 15 }
if (-not $PSBoundParameters.ContainsKey('MinimumFreeGiB')) { $MinimumFreeGiB = if ($existing -and $existing.MinimumFreeGiB) { [int]$existing.MinimumFreeGiB } else { 5 } }
if (-not $PSBoundParameters.ContainsKey('MaxMinutes')) { $MaxMinutes = if ($existing -and $existing.MaxMinutes) { [int]$existing.MaxMinutes } else { 120 } }
if (-not $PSBoundParameters.ContainsKey('PageSize')) { $PageSize = if ($existing -and $existing.PageSize) { [int]$existing.PageSize } else { 5000 } }
if (-not $PSBoundParameters.ContainsKey('PostgresSchema')) { $PostgresSchema = if ($existing -and $existing.PostgresSchema) { [string]$existing.PostgresSchema } else { 'quickbooks_ingest' } }
# Availability depends on the QuickBooks edition, region, and subscriptions. The loader requires
# this set to match each snapshot exactly and reports the snapshot's actual set when it does not.
if (-not $PSBoundParameters.ContainsKey('AllowedUnavailableEntities')) { $AllowedUnavailableEntities = if ($existing -and $null -ne $existing.AllowedUnavailableEntities) { [string[]]$existing.AllowedUnavailableEntities } else { @() } }
$AllowedUnavailableEntities = @($AllowedUnavailableEntities | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
foreach ($entity in $AllowedUnavailableEntities) { if ($entity -notmatch '^[A-Za-z]+$') { throw 'AllowedUnavailableEntities must contain catalog entity names.' } }

$previousErrorActionPreference = $ErrorActionPreference
try {
    # Windows PowerShell 5.1 can promote native stderr to a terminating
    # RemoteException while Stop is active, even though a local fallback exists.
    $ErrorActionPreference = 'Continue'
    $sourceCommit = (& git -c "safe.directory=$($PSScriptRoot -replace '\\','/')" rev-parse --short HEAD 2>$null)
    $gitExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($gitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = 'local' }
$releaseId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + $sourceCommit.Trim()
$releaseRoot = Join-Path (Join-Path $installRoot 'releases') $releaseId
New-Item -ItemType Directory -Force -Path $releaseRoot, $configRoot, (Join-Path $installRoot 'state'), (Join-Path $installRoot 'logs') | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'private\build\dotnet'
$env:APPDATA = Join-Path $PSScriptRoot 'private\build\appdata'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'private\build\packages'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $releaseRoot
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$runner = Join-Path $installRoot 'Invoke-ScheduledExport.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-ScheduledExport.ps1') -Destination $runner -Force

if ($ResetCredential -or -not (Test-Path -LiteralPath $secretPath -PathType Leaf)) {
    if (-not $ResetCredential -and (Test-Path -LiteralPath $legacySecretPath -PathType Leaf)) {
        [IO.File]::WriteAllText($secretPath, (Get-Content -LiteralPath $legacySecretPath -Raw).Trim(), [Text.Encoding]::ASCII)
    } else {
        $credential = Read-Host 'Postgres connection string (stored with Windows DPAPI for this user)' -AsSecureString
        if ($credential.Length -eq 0) { throw 'The Postgres connection string cannot be empty.' }
        [IO.File]::WriteAllText($secretPath, (ConvertFrom-SecureString $credential), [Text.Encoding]::ASCII)
    }
}

foreach ($name in 'state', 'logs') {
    $legacy = Join-Path $legacyRoot $name
    $current = Join-Path $installRoot $name
    if (Test-Path -LiteralPath $legacy -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $legacy -Force) { Copy-Item -LiteralPath $item.FullName -Destination $current -Recurse -Force }
    }
}

$deliveryCutoff = if ($existing -and $existing.DeliverSnapshotsCompletedAfterUtc) { [string]$existing.DeliverSnapshotsCompletedAfterUtc } else { [DateTimeOffset]::UtcNow.ToString('o') }
$config = [ordered]@{
    CompanyFile = [IO.Path]::GetFullPath($CompanyFile)
    SnapshotRoot = [IO.Path]::GetFullPath($SnapshotRoot)
    Executable = Join-Path $releaseRoot 'QuickBooksProbe.exe'
    PostgresSchema = $PostgresSchema
    AllowedUnavailableEntities = [string[]]$AllowedUnavailableEntities
    MinimumFreeGiB = $MinimumFreeGiB
    MaxMinutes = $MaxMinutes
    PageSize = $PageSize
    DailyAt = $DailyAt
    WindowMinutes = $WindowMinutes
    DeliverSnapshotsCompletedAfterUtc = $deliveryCutoff
}
$config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8

$installation = [ordered]@{ InstalledUtc = [DateTimeOffset]::UtcNow.ToString('o'); ReleaseId = $releaseId; SourceCommit = $sourceCommit.Trim(); Executable = $config.Executable }
$installation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'installation.json') -Encoding utf8

$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$schedulerRoot = $scheduler.GetFolder('\')
try { $null = $scheduler.GetFolder($schedulerFolderPath) } catch { $null = $schedulerRoot.CreateFolder('QuickBooksExporter') }

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$actionArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $runner, $configPath
$action = New-ScheduledTaskAction -Execute $windowsPowerShell -Argument $actionArguments
$trigger = New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today.Add([TimeSpan]::Parse($DailyAt)))
$trigger.Repetition = New-CimInstance -ClientOnly -Namespace 'Root/Microsoft/Windows/TaskScheduler' -ClassName 'MSFT_TaskRepetitionPattern' -Property @{
    Interval = [Xml.XmlConvert]::ToString([TimeSpan]::FromMinutes($HeartbeatMinutes))
    Duration = [Xml.XmlConvert]::ToString([TimeSpan]::FromMinutes($WindowMinutes))
    StopAtDurationEnd = $false
}
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable:$false
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Attempts an authorized QuickBooks export, then idempotently loads verified snapshots.' -Force | Out-Null

if (Get-ScheduledTask -TaskName 'QuickBooks Desktop Export and Load' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'QuickBooks Desktop Export and Load' -Confirm:$false
}

Write-Output "Installed release $releaseId under $installRoot. The task wakes every $HeartbeatMinutes minutes during the daily $DailyAt-$(([DateTime]::Today.Add([TimeSpan]::Parse($DailyAt)).AddMinutes($WindowMinutes)).ToString('HH:mm')) window."
