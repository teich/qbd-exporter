param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $buildArguments = @('build', '-c', 'Release', '--no-restore', '--nologo', '-p:UseSharedCompilation=false')
    if ($OutputDirectory) { $buildArguments += @('--output', $OutputDirectory) }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $testExecutable = if ($OutputDirectory) { Join-Path $OutputDirectory 'QuickBooksProbe.exe' } else { '.\bin\Release\net10.0-windows\QuickBooksProbe.exe' }
    & $testExecutable --self-test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $PSScriptRoot 'Test-PendingDeliveries.ps1')
    & (Join-Path $PSScriptRoot 'Test-ExportRecovery.ps1')
    exit 0
} finally { Pop-Location }
