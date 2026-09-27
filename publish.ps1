$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sdk = Join-Path $root '.sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    $sdk = (Get-Command dotnet -ErrorAction Stop).Source
    $versions = & $sdk --list-sdks
    if (-not ($versions | Select-String '^10\.')) {
        throw '构建需要 .NET 10 SDK。请从 https://dotnet.microsoft.com/download/dotnet/10.0 安装 SDK 后重试。目标电脑运行发布包无需安装 SDK。'
    }
}
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:APPDATA = Join-Path $root '.build-appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dist = Join-Path $root 'dist\GameZh'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$project = Join-Path $root 'app\GameZh.csproj'
$localPackages = Join-Path $root 'packages'
if ((Test-Path $localPackages) -and ((Get-ChildItem -LiteralPath $localPackages -Filter '*.nupkg' | Measure-Object).Count -gt 0)) {
    & $sdk restore $project --configfile (Join-Path $root 'NuGet.Config') -r win-x64 -p:SelfContained=true
} else {
    & $sdk restore $project -r win-x64 -p:SelfContained=true
}
if ($LASTEXITCODE -ne 0) { throw '还原依赖失败' }
& $sdk publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $dist
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runtime = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime.WindowsRuntime') -Recurse -Filter '*.dll' | Select-Object -First 1 -ExpandProperty FullName
$systemRuntime = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime') -Recurse -Filter '*.dll' | Select-Object -First 1 -ExpandProperty FullName
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$refs = @($runtime,$systemRuntime)
$refs += @('Windows.Foundation','Windows.Globalization','Windows.Graphics','Windows.Media','Windows.Storage') | ForEach-Object { Join-Path $metadata ($_.ToString() + '.winmd') }
$arguments = @('/nologo','/target:exe','/platform:x64','/optimize+',('/out:' + (Join-Path $dist 'OcrBridge.exe')))
$arguments += $refs | ForEach-Object { '/r:' + $_ }
$arguments += (Join-Path $root 'ocr\OcrBridge.cs')
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw 'OCR 组件编译失败' }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $dist '使用说明.md') -Force
if (Test-Path (Join-Path $root '验证记录.md')) {
    Copy-Item -LiteralPath (Join-Path $root '验证记录.md') -Destination (Join-Path $dist '验证记录.md') -Force
}
Get-ChildItem -LiteralPath $dist | Select-Object Name,Length
