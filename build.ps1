& (Join-Path $PSScriptRoot 'publish.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
