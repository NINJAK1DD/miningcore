[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$assembly = Join-Path $root "src/Miningcore/bin/$Configuration/net10.0/Miningcore.dll"
$example = Join-Path $root 'config.example.json'
$snapshot = Join-Path $root 'src/Miningcore.Tests/Fixtures/config-diagnostics-v2.json'
if(-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw 'Build Miningcore in the requested configuration before regenerating the snapshot.'
}

# Only the checked-in public example is accepted; never point this helper at a
# live configuration. Validate the complete result before replacing the fixture.
$start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach($argument in @($assembly, '--dumpconfig', '-c', $example)) {
    $start.ArgumentList.Add($argument)
}
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
$output = [System.IO.MemoryStream]::new()
try {
    if(-not $process.Start()) { throw 'Unable to start diagnostics.' }
    # Drain both pipes concurrently; stderr must never be replayed. Read raw
    # stdout bytes so invalid UTF-8 is rejected rather than silently replaced.
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync($output)
    $stderr = $process.StandardError.BaseStream.CopyToAsync([System.IO.Stream]::Null)
    if(-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Diagnostics timed out.'
    }
    $null = $stdout.GetAwaiter().GetResult()
    $null = $stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0) { throw 'Diagnostics failed.' }
    $json = [System.Text.UTF8Encoding]::new($false, $true).GetString($output.ToArray())
}
catch {
    throw 'Configuration diagnostics failed or returned invalid UTF-8; the snapshot was not changed.'
}
finally {
    $process.Dispose()
    $output.Dispose()
}
if($json.StartsWith([string][char]0xfeff, [System.StringComparison]::Ordinal)) {
    $json = $json.Substring(1)
}
$json = $json.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char]10) + "`n"
try {
    $document = ConvertFrom-Json -InputObject $json -NoEnumerate
}
catch {
    throw 'Invalid diagnostic JSON; the snapshot was not changed.'
}
# The [pscustomobject] accelerator also matches wrapped arrays. Require the
# concrete JSON object type so a one-element array cannot pass via enumeration.
if($document -isnot [System.Management.Automation.PSCustomObject] -or
    ($document.diagnosticFormatVersion -isnot [int] -and $document.diagnosticFormatVersion -isnot [long]) -or
    $document.diagnosticFormatVersion -ne 2 -or
    $document.configuration -isnot [System.Management.Automation.PSCustomObject]) {
    throw 'Unexpected diagnostic contract; review the format before replacing the snapshot.'
}
[System.IO.File]::WriteAllText($snapshot, $json, [System.Text.UTF8Encoding]::new($false))
Write-Output 'Updated diagnostic snapshot. Review the fixture diff before committing.'
