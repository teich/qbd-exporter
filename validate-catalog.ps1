param([string]$Executable = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\QuickBooksProbe.exe'))
$ErrorActionPreference = 'Stop'
# The installed QBFC16 schema parser is 32-bit. This script uses it only to parse
# generated request XML; it never opens a connection or calls DoRequests.
if ([Environment]::Is64BitProcess) {
    & "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Executable $Executable
    exit $LASTEXITCODE
}
$catalog = (& $Executable --catalog) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$manager = New-Object -ComObject QBFC16.QBSessionManager
$failures = 0
function Assert-RequestFieldsRetained([xml]$Original, [xml]$RoundTrip) {
    # Verify() can succeed after silently dropping unknown fields or enum values.
    # Require every supplied leaf value and its multiplicity to survive parsing.
    foreach ($leaf in $Original.SelectNodes('//*[not(*)]')) {
        $parts = [Collections.Generic.List[string]]::new()
        $node = $leaf
        while ($node -is [System.Xml.XmlElement]) {
            $parts.Insert(0, $node.Name)
            $node = $node.ParentNode
        }
        $path = '/' + ($parts -join '/')
        $value = $leaf.InnerText
        $expected = @($Original.SelectNodes($path) | Where-Object { $_.InnerText -ceq $value }).Count
        $equivalent = $value
        if ($leaf.Name -in @('IncludeLineItems', 'IncludeLinkedTxns', 'IncludeComponentLineItems', 'DisplayReport', 'IncludeSubcolumns')) {
            # QBFC serializes XML booleans as 0/1 instead of false/true.
            if ($value -ceq 'true') { $equivalent = '1' }
            elseif ($value -ceq 'false') { $equivalent = '0' }
        }
        $actual = @($RoundTrip.SelectNodes($path) | Where-Object { $_.InnerText -ceq $value -or $_.InnerText -ceq $equivalent }).Count
        if ($actual -lt $expected) { throw "SDK parser dropped or changed request field $path" }
    }
}
try {
    Assert-RequestFieldsRetained ([xml]'<Request><DisplayReport>false</DisplayReport></Request>') ([xml]'<Request><DisplayReport>0</DisplayReport></Request>')
    foreach ($case in @(
        @('<Request><Option>All</Option></Request>', '<Request/>'),
        @('<Request><Option>TotalOnly</Option></Request>', '<Request><Option>Total</Option></Request>'),
        @('<Request><ID>A</ID><ID>A</ID></Request>', '<Request><ID>A</ID></Request>')
    )) {
        $rejected = $false
        try { Assert-RequestFieldsRetained ([xml]$case[0]) ([xml]$case[1]) }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Request retention regression check failed.' }
    }
    foreach ($entry in $catalog) {
        $request = $null
        try {
            $request = $manager.ToMsgSetRequest($entry.Xml)
            $errors = ''
            if (-not $request.Verify([ref]$errors)) { throw $errors }
            Assert-RequestFieldsRetained ([xml]$entry.Xml) ([xml]$request.ToXMLString())
            Write-Output "PASS $($entry.Kind) $($entry.Phase)"
        } catch {
            $failures++
            # Only synthetic request schemas are handled here; no company data.
            Write-Output "FAIL $($entry.Kind) $($entry.Phase): $($_.Exception.Message)"
        } finally {
            if ($null -ne $request) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($request) }
        }
    }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($manager) }
if ($failures -gt 0) { exit 1 }
exit 0
