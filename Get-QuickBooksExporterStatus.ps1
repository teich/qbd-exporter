[CmdletBinding()]
param([switch]$ProbePostgres)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'QuickBooksExporter'
$configPath = Join-Path $installRoot 'config\config.json'
$secretPath = Join-Path $installRoot 'config\postgres-connection.dpapi'
$installationPath = Join-Path $installRoot 'installation.json'
$task = Get-ScheduledTask -TaskPath '\QuickBooksExporter\' -TaskName 'Nightly Export' -ErrorAction SilentlyContinue
$info = if ($task) { Get-ScheduledTaskInfo -TaskPath '\QuickBooksExporter\' -TaskName 'Nightly Export' } else { $null }
$config = if (Test-Path -LiteralPath $configPath -PathType Leaf) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
$installation = if (Test-Path -LiteralPath $installationPath -PathType Leaf) { Get-Content -LiteralPath $installationPath -Raw | ConvertFrom-Json } else { $null }

[pscustomobject]@{
    Installed = [bool]($task -and $config -and $installation)
    InstallRoot = $installRoot
    ReleaseId = if ($installation) { $installation.ReleaseId } else { $null }
    SourceCommit = if ($installation) { $installation.SourceCommit } else { $null }
    TaskState = if ($task) { [string]$task.State } else { $null }
    LastRunTime = if ($info) { $info.LastRunTime } else { $null }
    LastTaskResult = if ($info) { $info.LastTaskResult } else { $null }
    NextRunTime = if ($info) { $info.NextRunTime } else { $null }
    Heartbeat = if ($task) { $task.Triggers[0].Repetition.Interval } else { $null }
    DailyAt = if ($config) { $config.DailyAt } else { $null }
    WindowMinutes = if ($config) { $config.WindowMinutes } else { $null }
    ExecutablePresent = [bool]($config -and (Test-Path -LiteralPath $config.Executable -PathType Leaf))
    CompanyFilePresent = [bool]($config -and (Test-Path -LiteralPath $config.CompanyFile -PathType Leaf))
    CredentialPresent = Test-Path -LiteralPath $secretPath -PathType Leaf
    DeliveriesNeedingReview = @(Get-ChildItem -LiteralPath (Join-Path $installRoot 'config\state\needs-review') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count
}

if ($ProbePostgres) {
    if (-not $config -or -not (Test-Path -LiteralPath $secretPath -PathType Leaf)) { throw 'Installed configuration or credential is missing.' }
    $secure = ConvertTo-SecureString ((Get-Content -LiteralPath $secretPath -Raw).Trim())
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $env:QBD_POSTGRES_CONNECTION_STRING = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $env:QBD_POSTGRES_SCHEMA = $config.PostgresSchema
    try {
        & $config.Executable postgres probe
        if ($LASTEXITCODE -ne 0) { throw "Postgres probe failed with exit code $LASTEXITCODE." }
    } finally {
        Remove-Item Env:\QBD_POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
    }
}
