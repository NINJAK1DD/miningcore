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
$lines = @(& dotnet $assembly --dumpconfig -c $example)
if($LASTEXITCODE -ne 0) {
    throw 'Configuration diagnostics failed; the snapshot was not changed.'
}
$json = ($lines -join "`n") + "`n"
$document = ConvertFrom-Json -InputObject $json
if($document.diagnosticFormatVersion -ne 2 -or $null -eq $document.configuration) {
    throw 'Unexpected diagnostic contract; review the format before replacing the snapshot.'
}
[System.IO.File]::WriteAllText($snapshot, $json, [System.Text.UTF8Encoding]::new($false))
Write-Output 'Updated diagnostic snapshot. Review the fixture diff before committing.'
