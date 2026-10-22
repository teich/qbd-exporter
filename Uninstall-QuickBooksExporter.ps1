[CmdletBinding(SupportsShouldProcess)]
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'QuickBooksExporter'))
$expectedParent = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if (-not $installRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe install root.' }

$task = Get-ScheduledTask -TaskPath '\QuickBooksExporter\' -TaskName 'Nightly Export' -ErrorAction SilentlyContinue
if ($task -and $task.State -eq 'Running') { throw 'The exporter task is running; uninstall after it completes.' }
if ($task -and $PSCmdlet.ShouldProcess('\QuickBooksExporter\Nightly Export', 'Unregister scheduled task')) {
    Unregister-ScheduledTask -TaskPath '\QuickBooksExporter\' -TaskName 'Nightly Export' -Confirm:$false
}

if ($PSCmdlet.ShouldProcess($installRoot, $(if ($Purge) { 'Remove application, configuration, state, and logs' } else { 'Remove application and releases; retain configuration, state, and logs' }))) {
    foreach ($path in @((Join-Path $installRoot 'releases'), (Join-Path $installRoot 'Invoke-ScheduledExport.ps1'), (Join-Path $installRoot 'installation.json'))) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if ($Purge -and (Test-Path -LiteralPath $installRoot)) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
}
