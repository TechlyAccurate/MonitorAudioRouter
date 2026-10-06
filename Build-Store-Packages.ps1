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

. (Join-Path $root "Build-Archive.ps1")

$processIdsPattern = '(?s)async function processIdsForTabs\(tabs\) \{.*?\r?\n\}\r?\n\r?\nasync function collectAudibleWindows'
$actionClickPattern = '(?s)\r?\n  chrome\.action\.onClicked\.addListener\(\(\) => \{.*?\r?\n  \}\);\r?\n'

function Assert-StoreTransformTargets([string]$ManifestText, [string]$BackgroundText) {
    $targets = @(
        @{ Name = "manifest key"; Text = $ManifestText; Pattern = '(?m)^\s*"key"\s*:' },
        @{ Name = "optional_permissions"; Text = $ManifestText; Pattern = '(?m)^\s*"optional_permissions"\s*:' },
        @{ Name = "processIdsForTabs"; Text = $BackgroundText; Pattern = $processIdsPattern },
        @{ Name = "chrome.action.onClicked"; Text = $BackgroundText; Pattern = $actionClickPattern }
    )

    $failures = @(
        foreach ($target in $targets) {
            $matchCount = [regex]::Matches($target.Text, $target.Pattern).Count
            if ($matchCount -ne 1) {
                "$($target.Name) expected exactly one source match but found $matchCount."
            }
        }
    )
    if ($failures.Count -ne 0) {
        throw "Store transform validation failed:`n - $($failures -join "`n - ")"
    }
}

function Assert-StoreBuildContents([string]$BuildDirectory, [switch]$Chromium) {
    $expectedFiles = [string[]]@(
        "background.js",
        "icons/icon-128.png",
        "icons/icon-16.png",
        "icons/icon-32.png",
        "icons/icon-48.png",
        "icons/icon-64.png",
        "icons/icon-96.png",
        "manifest.json"
    )
    $resolvedBuild = (Resolve-Path -LiteralPath $BuildDirectory).Path
    $buildPrefix = $resolvedBuild.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $actualFiles = [string[]]@(
        Get-ChildItem -LiteralPath $resolvedBuild -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($buildPrefix.Length).Replace(
                    [IO.Path]::DirectorySeparatorChar,
                    "/")
            }
    )
    [Array]::Sort($actualFiles, [StringComparer]::Ordinal)
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw "Store build contains an unexpected file count: $($actualFiles -join ', ')."
    }
    for ($index = 0; $index -lt $expectedFiles.Count; $index++) {
        if ($actualFiles[$index] -ne $expectedFiles[$index]) {
            throw "Store build contains a missing or development-only file: $($actualFiles -join ', ')."
        }
    }

    if ($Chromium) {
        $manifest = Get-Content -LiteralPath (Join-Path $resolvedBuild "manifest.json") -Raw | ConvertFrom-Json
        $propertyNames = @($manifest.PSObject.Properties | ForEach-Object { $_.Name })
        if ($propertyNames -contains "key" -or
            $propertyNames -contains "optional_permissions" -or
            @($manifest.permissions) -contains "processes") {
            throw "Chromium store manifest contains development-only values."
        }

        $background = Get-Content -LiteralPath (Join-Path $resolvedBuild "background.js") -Raw
        if ($background -match 'chrome\.processes|chrome\.permissions') {
            throw "Chromium store background contains development-only process permission code."
        }
    }
}

$chromeManifestSource = Get-Content (Join-Path $root "extensions\chromium\manifest.json") -Raw
$chromeBackgroundSource = Get-Content (Join-Path $root "extensions\chromium\background.js") -Raw
Assert-StoreTransformTargets $chromeManifestSource $chromeBackgroundSource

foreach ($generatedDir in @($chromeDir, $firefoxDir)) {
    Remove-Item -LiteralPath $generatedDir -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $chromeBuild, $firefoxBuild | Out-Null

Copy-Item (Join-Path $root "extensions\chromium\background.js") (Join-Path $chromeBuild "background.js") -Force
Copy-Item -LiteralPath (Join-Path $root "extensions\chromium\icons") -Destination (Join-Path $chromeBuild "icons") -Recurse -Force
Copy-Item (Join-Path $root "extensions\firefox\background.js") (Join-Path $firefoxBuild "background.js") -Force
Copy-Item -LiteralPath (Join-Path $root "extensions\firefox\icons") -Destination (Join-Path $firefoxBuild "icons") -Recurse -Force
Copy-Item (Join-Path $root "extensions\firefox\manifest.json") (Join-Path $firefoxBuild "manifest.json") -Force

$chromeManifest = $chromeManifestSource | ConvertFrom-Json
$chromeManifest.PSObject.Properties.Remove("key")
$chromeManifest.PSObject.Properties.Remove("optional_permissions")
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$chromeManifestJson = ($chromeManifest | ConvertTo-Json -Depth 20) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $chromeBuild "manifest.json"),
    ($chromeManifestJson.TrimEnd() + "`n"),
    $utf8NoBom)

$chromeBackground = $chromeBackgroundSource -replace $processIdsPattern, 'async function processIdsForTabs(tabs) {
  return [];
}

async function collectAudibleWindows'
$chromeBackground = $chromeBackground -replace $actionClickPattern, "`n  chrome.action.onClicked.addListener(requestSnapshot);`n"
$chromeBackground = $chromeBackground -replace "`r`n", "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $chromeBuild "background.js"),
    ($chromeBackground.TrimEnd() + "`n"),
    $utf8NoBom)

Assert-StoreBuildContents $chromeBuild -Chromium
Assert-StoreBuildContents $firefoxBuild

New-DeterministicArchive $chromeBuild $chromeZip
New-DeterministicArchive $firefoxBuild $firefoxZip
New-DeterministicArchive $firefoxBuild $firefoxXpi

Write-Host "Created:"
Write-Host $chromeZip
Write-Host $firefoxZip
Write-Host $firefoxXpi
