# Synthetic coordinator acceptance. Extract only the delivery function; never execute
# the coordinator's top-level code, credential loading, scheduler, or QuickBooks paths.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-ScheduledExport.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Coordinator parse failed.' }
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Send-PendingSnapshots' }, $true)
Invoke-Expression $definition.Extent.Text
function Set-PostgresConnection { return $true }
$script:Calls = [Collections.Generic.List[string]]::new()
$script:Events = [Collections.Generic.List[string]]::new()
$script:ExitCodes = @{}
function Invoke-Probe {
    param($Arguments)
    $id = [IO.Path]::GetFileName($Arguments[3])
    $script:Calls.Add($id)
    return $script:ExitCodes[$id]
}
function Write-RunEvent { param($Event, $Details) $script:Events.Add($Event) }
function Assert-Delivery { param($Condition, $Name) if (-not $Condition) { throw $Name } }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('qbd-delivery-test-' + [Guid]::NewGuid().ToString('N'))
$script:SnapshotRoot = Join-Path $testRoot 'snapshots'
$script:DeliveredRoot = Join-Path $testRoot 'delivered'
$script:ReviewRoot = Join-Path $testRoot 'needs-review'
$script:Config = [pscustomobject]@{ AllowedUnavailableEntities = @(); DeliverSnapshotsCompletedAfterUtc = '2026-01-01T00:00:00Z' }
function New-DeliveryFixture {
    param([int]$Number, [int]$ExitCode)
    $id = $Number.ToString('D32')
    $item = New-Item -ItemType Directory -Path (Join-Path $script:SnapshotRoot $id)
    @{ FormatVersion = 2; ExportMode = 'full'; CompletedUtc = ([DateTimeOffset]'2026-01-01T00:00:00Z').AddDays($Number).ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $item.FullName 'manifest.json') -Encoding utf8
    # Reverse modification order so it cannot accidentally substitute for source time.
    $item.LastWriteTimeUtc = ([DateTime]'2026-02-01').AddDays(-$Number)
    $script:ExitCodes[$id] = $ExitCode
    return $id
}
New-Item -ItemType Directory -Path $script:SnapshotRoot, $script:DeliveredRoot, $script:ReviewRoot -Force | Out-Null
try {
    $late = New-DeliveryFixture 1 14
    $newer = New-DeliveryFixture 2 0
    Assert-Delivery (Send-PendingSnapshots) 'Permanent rejection must not block newer deliveries.'
    Assert-Delivery (($script:Calls -join ',') -eq "$late,$newer") 'Delivery order must use source completion time.'
    Assert-Delivery (Test-Path -LiteralPath (Join-Path $script:ReviewRoot ($late + '.json'))) 'Permanent rejection must have a review marker.'
    Assert-Delivery (-not (Test-Path -LiteralPath (Join-Path $script:DeliveredRoot ($late + '.json')))) 'Rejected export must not be marked delivered.'
    Assert-Delivery (Test-Path -LiteralPath (Join-Path $script:SnapshotRoot ($late + '\manifest.json'))) 'Rejected export must be preserved.'
    Assert-Delivery (Test-Path -LiteralPath (Join-Path $script:DeliveredRoot ($newer + '.json'))) 'Newer delivery must complete.'
    Assert-Delivery (Send-PendingSnapshots) 'Subsequent heartbeat must succeed.'
    Assert-Delivery ($script:Calls.Count -eq 2) 'Review and delivered markers must prevent repeated attempts.'
    Assert-Delivery (@($script:Events | Where-Object { $_ -eq 'load-needs-review' }).Count -eq 1) 'Permanent rejection must be reported exactly once.'
    $transient = New-DeliveryFixture 3 13
    $waiting = New-DeliveryFixture 4 0
    Assert-Delivery (-not (Send-PendingSnapshots)) 'Transient failure must remain retryable.'
    Assert-Delivery ($script:Calls.Count -eq 3 -and $script:Calls[2] -eq $transient) 'Transient failure must preserve delivery order.'
    Assert-Delivery (-not (Test-Path -LiteralPath (Join-Path $script:ReviewRoot ($transient + '.json')))) 'Transient failure must not be quarantined.'
    $script:ExitCodes[$transient] = 0
    Assert-Delivery (Send-PendingSnapshots) 'Transient retry must resume the queue.'
    Assert-Delivery (($script:Calls -join ',') -eq "$late,$newer,$transient,$transient,$waiting") 'Recovered queue must deliver remaining snapshots.'
    Write-Output 'PASS: 14 synthetic pending-delivery assertions; no live export, credential, or database access.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'qbd-delivery-test-*') { throw 'Unexpected cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
