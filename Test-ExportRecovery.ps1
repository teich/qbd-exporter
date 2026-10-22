# Synthetic checks only; never execute coordinator top-level code or access QuickBooks.
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-ScheduledExport.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Coordinator parse failed.' }
foreach ($name in @('Invoke-Probe', 'Invoke-ScheduledExtraction', 'Protect-LogMessage')) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    Invoke-Expression $node.Extent.Text
}
function Assert-Recovery { param($Value, $Message) if (-not $Value) { throw $Message } }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('qbd-recovery-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:Config = [pscustomobject]@{ CompanyFile = 'C:\synthetic\private.qbw'; PageSize = 5000; MaxMinutes = 120 }
$script:SnapshotRoot = 'C:\synthetic\snapshots'
$script:Executable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:Events = [Collections.Generic.List[object]]::new()
$script:AckPath = Join-Path $testRoot 'ack'
function Write-RunEvent {
    param($Event, $Details)
    $script:Events.Add([pscustomobject]@{ Event = $Event; Details = $Details })
    if ($Event -eq 'probe-output' -and $Details.Message -eq 'Reading ReceivePayment...') {
        [IO.File]::WriteAllText($script:AckPath, 'received')
    }
}
try {
    # Child waits for the logger's acknowledgment before exiting: buffering would fail.
    $child = "[Console]::Error.WriteLine('Reading ReceivePayment...'); [Console]::Out.WriteLine('C:\synthetic\private.qbw'); `$deadline = [DateTime]::UtcNow.AddSeconds(8); while (-not (Test-Path -LiteralPath '$($script:AckPath)') -and [DateTime]::UtcNow -lt `$deadline) { Start-Sleep -Milliseconds 50 }; if (Test-Path -LiteralPath '$($script:AckPath)') { exit 17 } else { exit 18 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
    $code = Invoke-Probe -Arguments @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
    Assert-Recovery ($code -eq 17) 'Output must reach the logger before child exit, preserving native exit code.'
    Assert-Recovery $script:ProbeReadStarted 'Stderr progress must mark extraction as started.'
    Assert-Recovery ($ErrorActionPreference -eq 'Stop') 'Native invocation must restore strict error handling.'
    Assert-Recovery (@($script:Events | Where-Object { $_.Details.Message -eq '[company-file]' }).Count -eq 1) 'Streamed output must redact configured paths.'

    $script:Calls = 0
    $script:ReadStarted = $false
    $script:TestExit = 3
    function Invoke-Probe {
        param($Arguments)
        $script:Calls++
        $script:ProbeReadStarted = $script:ReadStarted
        return $script:TestExit
    }
    $path = Join-Path $testRoot 'startup.json'
    1..4 | ForEach-Object { Assert-Recovery (-not (Invoke-ScheduledExtraction $path)) 'Startup failure must not report success.' }
    Assert-Recovery ($script:Calls -eq 3) 'Startup failures must stop after three attempts.'
    $path = Join-Path $testRoot 'extraction.json'
    $script:ReadStarted = $true
    Assert-Recovery (-not (Invoke-ScheduledExtraction $path)) 'Extraction failure must not succeed.'
    Assert-Recovery (-not (Invoke-ScheduledExtraction $path)) 'Extraction failure must block the next heartbeat.'
    Assert-Recovery ($script:Calls -eq 4) 'Extraction failure must not repeat.'
    $path = Join-Path $testRoot 'interrupted.json'
    @{ Attempts = 1; Blocked = $true; Reason = 'previous-attempt-incomplete' } | ConvertTo-Json | Set-Content $path
    Assert-Recovery (-not (Invoke-ScheduledExtraction $path)) 'Interrupted attempt must block retry.'
    Assert-Recovery ($script:Calls -eq 4) 'Interrupted attempt must not launch a child.'
    $script:TestExit = 0
    Assert-Recovery (Invoke-ScheduledExtraction (Join-Path $testRoot 'next-night.json')) 'A new night must permit recovery.'
    Assert-Recovery (Invoke-ScheduledExtraction $null) 'Explicit RunNow must bypass automatic attempt markers.'
    Write-Output 'PASS: streaming, redaction, exit handling, startup cap, mid-extraction suppression, interruption, next-night recovery, and explicit override.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'qbd-recovery-test-*') { throw 'Unexpected cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
