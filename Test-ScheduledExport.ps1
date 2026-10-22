[CmdletBinding()]
param(
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'QuickBooksExporter'
$runner = Join-Path $installRoot 'Invoke-ScheduledExport.ps1'
$configPath = Join-Path $installRoot 'config\config.json'
$logPath = Join-Path $installRoot ('config\logs\' + (Get-Date).ToString('yyyy-MM') + '.jsonl')
$taskPath = '\QuickBooksExporter\'
$nightlyTaskName = 'Nightly Export'
$canaryTaskName = 'Canary-' + [Guid]::NewGuid().ToString('N')

if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw 'Installed coordinator is missing.' }
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'Installed configuration is missing.' }

$nightly = Get-ScheduledTask -TaskPath $taskPath -TaskName $nightlyTaskName -ErrorAction Stop
if ($nightly.State -eq 'Running') { throw 'The nightly exporter is running; test after it completes.' }

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$actionArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}" -Canary' -f $runner, $configPath
$action = New-ScheduledTaskAction -Execute $windowsPowerShell -Argument $actionArguments
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddDays(1)
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -StartWhenAvailable:$false
$startedUtc = [DateTimeOffset]::UtcNow
$finished = $false
$info = $null

try {
    Register-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Temporary no-data QuickBooks exporter canary.' | Out-Null
    $previousRun = (Get-ScheduledTaskInfo -TaskPath $taskPath -TaskName $canaryTaskName).LastRunTime
    Start-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $observed = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $canary = Get-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName
        $info = Get-ScheduledTaskInfo -TaskPath $taskPath -TaskName $canaryTaskName
        if ($info.LastRunTime -gt $previousRun) { $observed = $true }
        if ($observed -and $canary.State -eq 'Ready') {
            Start-Sleep -Milliseconds 500
            $info = Get-ScheduledTaskInfo -TaskPath $taskPath -TaskName $canaryTaskName
            $finished = $true
            break
        }
    }

    if (-not $finished) { throw "Canary task did not finish within $TimeoutSeconds seconds." }
    if ($info.LastTaskResult -ne 0) { throw "Canary task returned $($info.LastTaskResult)." }

    $events = @(Get-Content -LiteralPath $logPath | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { }
    } | Where-Object { [DateTimeOffset]::Parse($_.TimestampUtc) -ge $startedUtc.AddSeconds(-2) })
    $started = @($events | Where-Object { $_.Event -eq 'run-started' -and $_.Mode -eq 'canary' })
    $completed = @($events | Where-Object { $_.Event -eq 'canary-completed' })
    $unsafe = @($events | Where-Object { $_.Event -match '^(export|load)-' })
    if ($started.Count -ne 1 -or $completed.Count -ne 1) { throw 'Expected canary identity events were not found.' }
    if ($unsafe.Count -ne 0) { throw 'Canary unexpectedly entered an export or load path.' }

    [pscustomobject]@{
        Success = $true
        TaskResult = $info.LastTaskResult
        RunAs = $principal.UserId
        ReleaseId = $completed[0].ReleaseId
        CoordinatorSha256 = $completed[0].CoordinatorSha256
        ExportOrLoadEvents = $unsafe.Count
    }
} finally {
    $canary = Get-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName -ErrorAction SilentlyContinue
    if ($canary) {
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while ($canary.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
            $canary = Get-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName
        }
        if ($canary.State -ne 'Running') {
            Unregister-ScheduledTask -TaskPath $taskPath -TaskName $canaryTaskName -Confirm:$false
        }
    }
}
