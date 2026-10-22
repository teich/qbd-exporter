[CmdletBinding()]
param(
    [string]$CompanyFile,
    [string]$SnapshotRoot = (Join-Path $env:USERPROFILE 'qbd-snapshots'),
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$DailyAt = '02:00',
    [ValidateRange(15, 720)][int]$WindowMinutes = 180,
    [ValidateRange(5, 60)][int]$HeartbeatMinutes = 15,
    [ValidateRange(1, 1024)][int]$MinimumFreeGiB = 5,
    [ValidateRange(1, 1440)][int]$MaxMinutes = 120,
    [ValidateRange(1, 5000)][int]$PageSize = 5000,
    [string]$PostgresSchema = 'quickbooks_ingest',
    [switch]$KeepExistingCredential
)

$arguments = @{}
foreach ($name in $PSBoundParameters.Keys) {
    if ($name -ne 'KeepExistingCredential') { $arguments[$name] = $PSBoundParameters[$name] }
}
& (Join-Path $PSScriptRoot 'Install-QuickBooksExporter.ps1') @arguments
exit $LASTEXITCODE
