[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Install-QuickBooksExporter.ps1')
exit $LASTEXITCODE
