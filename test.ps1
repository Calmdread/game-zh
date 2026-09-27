$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sdk = Join-Path $root '.sdk\dotnet.exe'
if (-not (Test-Path $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:APPDATA = Join-Path $root '.build-appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $root 'tests\GameZh.Tests.csproj'
if (Test-Path (Join-Path $root 'packages')) {
    & $sdk restore $project --configfile (Join-Path $root 'NuGet.Config') -r win-x64 -p:NuGetAudit=false
} else {
    & $sdk restore $project -r win-x64 -p:NuGetAudit=false
}
if ($LASTEXITCODE -ne 0) { throw '测试项目还原失败' }
& $sdk build $project -c Release --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '测试项目构建失败' }
$bin = Join-Path $root 'tests\bin\Release\net10.0-windows\win-x64'
Copy-Item -LiteralPath (Join-Path $root 'dist\GameZh\OcrBridge.exe') -Destination (Join-Path $bin 'OcrBridge.exe') -Force
& (Join-Path $bin 'GameZh.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw '测试失败' }
