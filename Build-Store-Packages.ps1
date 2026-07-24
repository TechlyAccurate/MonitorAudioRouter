$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageDir = Join-Path $root "packages\browser-store"
$chromeDir = Join-Path $packageDir "chrome"
$firefoxDir = Join-Path $packageDir "firefox"
$chromeBuild = Join-Path $chromeDir "unpacked"
$firefoxBuild = Join-Path $firefoxDir "unpacked"
$chromeZip = Join-Path $chromeDir "monitor-audio-router.zip"
$firefoxZip = Join-Path $firefoxDir "monitor-audio-router.zip"
$firefoxXpi = Join-Path $firefoxDir "monitor-audio-router.xpi"

function Compress-DirectoryWithForwardSlashes([string]$SourceDirectory, [string]$DestinationPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory).Path
    Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue

    $archive = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem -LiteralPath $resolvedSource -File -Recurse | Sort-Object FullName
        $archiveTimestamp = [DateTimeOffset]::Parse("2020-01-01T00:00:00Z")
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($resolvedSource.Length)
            $relativePath = $relativePath.TrimStart([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
            $entryName = $relativePath -replace "\\", "/"
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $archiveTimestamp
            $inputStream = [System.IO.File]::OpenRead($file.FullName)
            $outputStream = $entry.Open()
            try {
                $inputStream.CopyTo($outputStream)
            }
            finally {
                $outputStream.Dispose()
                $inputStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($generatedDir in @($chromeDir, $firefoxDir)) {
    Remove-Item -LiteralPath $generatedDir -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $chromeBuild, $firefoxBuild | Out-Null

Copy-Item (Join-Path $root "extensions\chromium\background.js") (Join-Path $chromeBuild "background.js") -Force
Copy-Item -LiteralPath (Join-Path $root "extensions\chromium\icons") -Destination (Join-Path $chromeBuild "icons") -Recurse -Force
Copy-Item (Join-Path $root "extensions\firefox\background.js") (Join-Path $firefoxBuild "background.js") -Force
Copy-Item -LiteralPath (Join-Path $root "extensions\firefox\icons") -Destination (Join-Path $firefoxBuild "icons") -Recurse -Force
Copy-Item (Join-Path $root "extensions\firefox\manifest.json") (Join-Path $firefoxBuild "manifest.json") -Force

$chromeManifest = Get-Content (Join-Path $root "extensions\chromium\manifest.json") -Raw | ConvertFrom-Json
$chromeManifest.PSObject.Properties.Remove("key")
$chromeManifest.PSObject.Properties.Remove("optional_permissions")
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$chromeManifestJson = ($chromeManifest | ConvertTo-Json -Depth 20) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $chromeBuild "manifest.json"),
    ($chromeManifestJson.TrimEnd() + "`n"),
    $utf8NoBom)

$chromeBackground = Get-Content (Join-Path $chromeBuild "background.js") -Raw
$chromeBackground = $chromeBackground -replace '(?s)async function processIdsForTabs\(tabs\) \{.*?\r?\n\}\r?\n\r?\nasync function collectAudibleWindows', 'async function processIdsForTabs(tabs) {
  return [];
}

async function collectAudibleWindows'
$chromeBackground = $chromeBackground -replace '(?s)\r?\n  chrome\.action\.onClicked\.addListener\(\(\) => \{.*?\r?\n  \}\);\r?\n', "`n  chrome.action.onClicked.addListener(sendSnapshot);`n"
$chromeBackground = $chromeBackground -replace "`r`n", "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $chromeBuild "background.js"),
    ($chromeBackground.TrimEnd() + "`n"),
    $utf8NoBom)

Compress-DirectoryWithForwardSlashes $chromeBuild $chromeZip
Compress-DirectoryWithForwardSlashes $firefoxBuild $firefoxZip
Compress-DirectoryWithForwardSlashes $firefoxBuild $firefoxXpi

Write-Host "Created:"
Write-Host $chromeZip
Write-Host $firefoxZip
Write-Host $firefoxXpi
