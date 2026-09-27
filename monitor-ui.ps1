param([Parameter(Mandatory=$true)][int]$TargetPid, [Parameter(Mandatory=$true)][string]$ExpectedExe, [int]$Minutes = 30, [string]$OutputName = 'ui-soak-output.csv')
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot $OutputName
'time,elapsed_minutes,private_mb,cpu_ms,alive' | Set-Content -LiteralPath $out -Encoding utf8
for ($minute = 0; $minute -le $Minutes; $minute++) {
    $p = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if (-not $p -or $p.Path -ne $ExpectedExe) {
        "$(Get-Date -Format o),$minute,,,false" | Add-Content -LiteralPath $out
        break
    }
    $p.Refresh()
    "$(Get-Date -Format o),$minute,$([math]::Round($p.PrivateMemorySize64 / 1MB,2)),$([math]::Round($p.TotalProcessorTime.TotalMilliseconds,0)),true" | Add-Content -LiteralPath $out
    if ($minute -lt $Minutes) { Start-Sleep -Seconds 60 }
}
