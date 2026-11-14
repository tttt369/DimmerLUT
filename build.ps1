Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
& "$PSScriptRoot\run.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
