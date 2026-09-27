$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$appDir = Join-Path $dist 'GameZh'
if (-not (Test-Path (Join-Path $appDir 'GameZh.exe'))) { throw '请先运行 publish.ps1' }

function Write-ZipFile([string]$zipPath, [object[]]$items) {
    $stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
        try {
            foreach ($item in $items) {
                $entry = $archive.CreateEntry($item.Name.Replace('\','/'), [System.IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [System.IO.File]::OpenRead($item.Path)
                try {
                    $outputStream = $entry.Open()
                    try { $inputStream.CopyTo($outputStream) }
                    finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

$portable = @()
foreach ($file in Get-ChildItem -LiteralPath $appDir -Recurse -File) {
    $portable += [pscustomobject]@{ Path=$file.FullName; Name=('GameZh/' + [System.IO.Path]::GetRelativePath($appDir,$file.FullName)) }
}
Write-ZipFile (Join-Path $dist 'GameZh-portable-win-x64.zip') $portable

$source = @()
foreach ($part in @('app','ocr','tests')) {
    $dir = Join-Path $root $part
    foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)/') { continue }
        $source += [pscustomobject]@{ Path=$file.FullName; Name=$relative }
    }
}
foreach ($name in @('README.md','验证记录.md','publish.ps1','build.ps1','test.ps1','package.ps1','NuGet.Config')) {
    $source += [pscustomobject]@{ Path=(Join-Path $root $name); Name=$name }
}
Write-ZipFile (Join-Path $dist 'GameZh-source.zip') $source
Get-ChildItem -LiteralPath $dist -Filter '*.zip' | Select-Object Name,Length
