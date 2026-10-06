param(
    [switch]$HarnessSelfTestOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$csharpTestProject = Join-Path $PSScriptRoot "MonitorAudioRouter.RegressionTests\MonitorAudioRouter.RegressionTests.csproj"
$browserTestRunner = Join-Path $PSScriptRoot "browser-extension-regression-tests.js"
$installerPowerShellTestRunner = Join-Path $PSScriptRoot "installer-uninstaller-regression-tests.ps1"
$installerBuild = Join-Path $repositoryRoot "Build-Installer.ps1"
$testAppData = Join-Path $PSScriptRoot ".appdata"

function Assert-TestEntryPoint([string]$Name, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Name at $Path"
    }
}

Assert-TestEntryPoint "C# regression test project" $csharpTestProject
Assert-TestEntryPoint "browser extension test runner" $browserTestRunner
Assert-TestEntryPoint "installer PowerShell test runner" $installerPowerShellTestRunner

$nugetDirectory = Join-Path $testAppData "NuGet"
$nugetConfigPath = Join-Path $nugetDirectory "NuGet.Config"
New-Item -ItemType Directory -Path $nugetDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $nugetConfigPath -PathType Leaf)) {
    $nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($nugetConfigPath, $nugetConfig, $utf8NoBom)
}
$env:APPDATA = $testAppData
$env:NuGetAudit = "false"

function Assert-NativeSuccess([string]$Action, [int]$ExitCode) {
    if ($ExitCode -ne 0) {
        throw "$Action failed with exit code $ExitCode"
    }
}

function Invoke-CheckedCommand([string]$Action, [scriptblock]$Command) {
    & $Command
    $exitCode = $LASTEXITCODE
    Assert-NativeSuccess $Action $exitCode
}

function Test-PowerShellSyntax([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors) | Out-Null

    if ($parseErrors.Count -gt 0) {
        $messages = ($parseErrors | ForEach-Object { $_.Message }) -join "; "
        throw "PowerShell syntax check failed for $Path`: $messages"
    }
}

function Add-PackageFailure(
    [System.Collections.Generic.List[string]]$Failures,
    [string]$Message) {
    $Failures.Add($Message)
}

function Assert-EqualPackageValue(
    [System.Collections.Generic.List[string]]$Failures,
    [string]$Name,
    [string]$Expected,
    [string]$Actual) {
    if ($Actual -ne $Expected) {
        Add-PackageFailure $Failures "$Name expected '$Expected' but found '$Actual'."
    }
}

function Get-ZipEntryText([string]$ArchivePath, [string]$EntryName) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) {
            throw "Archive $ArchivePath does not contain $EntryName."
        }

        $stream = $entry.Open()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-DeterministicArchive(
    [System.Collections.Generic.List[string]]$Failures,
    [string]$Name,
    [string]$ArchivePath) {
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        Add-PackageFailure $Failures "$Name is missing at $ArchivePath."
        return
    }

    $archiveTimestamp = [DateTime]::SpecifyKind(
        [DateTime]::Parse("2020-01-01T00:00:00"),
        [DateTimeKind]::Unspecified)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -eq 0) {
            Add-PackageFailure $Failures "$Name contains no entries."
            return
        }

        $actualNames = [string[]]@($entries | ForEach-Object { $_.FullName })
        $expectedNames = [string[]]$actualNames.Clone()
        [Array]::Sort($expectedNames, [StringComparer]::Ordinal)
        for ($index = 0; $index -lt $actualNames.Count; $index++) {
            if ($actualNames[$index] -ne $expectedNames[$index]) {
                Add-PackageFailure $Failures "$Name entries are not in stable ordinal order."
                break
            }
        }

        foreach ($entry in $entries) {
            if ($entry.FullName.Contains([IO.Path]::DirectorySeparatorChar)) {
                Add-PackageFailure $Failures "$Name contains a non-portable entry path: $($entry.FullName)."
            }
            if ($entry.LastWriteTime.DateTime -ne $archiveTimestamp) {
                Add-PackageFailure $Failures "$Name has non-deterministic timestamp $($entry.LastWriteTime.ToString('o')) for $($entry.FullName)."
                break
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Invoke-StoreFixtureBuild([scriptblock]$MutateFixture) {
    $fixtureRoot = Join-Path $testAppData ("store-fixture-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "Build-Store-Packages.ps1") -Destination $fixtureRoot -Force
        $archiveScript = Join-Path $repositoryRoot "Build-Archive.ps1"
        if (Test-Path -LiteralPath $archiveScript -PathType Leaf) {
            Copy-Item -LiteralPath $archiveScript -Destination $fixtureRoot -Force
        }
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "extensions") -Destination $fixtureRoot -Recurse -Force
        & $MutateFixture $fixtureRoot

        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = @(
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureRoot "Build-Store-Packages.ps1") 2>&1
            )
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = ($output | Out-String)
        }
    }
    finally {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-StoreTransformValidation([System.Collections.Generic.List[string]]$Failures) {
    $missingResult = Invoke-StoreFixtureBuild {
        param($FixtureRoot)
        $manifestPath = Join-Path $FixtureRoot "extensions\chromium\manifest.json"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw
        $manifest = [regex]::Replace($manifest, '(?m)^  "key": .*\r?\n', '')
        $manifest = [regex]::Replace($manifest, '(?ms)^  "optional_permissions": \[.*?^  \],\r?\n', '')
        [IO.File]::WriteAllText($manifestPath, $manifest)
        [IO.File]::WriteAllText(
            (Join-Path $FixtureRoot "extensions\chromium\background.js"),
            '"use strict";' + [Environment]::NewLine)
    }

    foreach ($target in @('manifest key', 'optional_permissions', 'processIdsForTabs', 'chrome.action.onClicked')) {
        if ($missingResult.ExitCode -eq 0 -or $missingResult.Output -notmatch [regex]::Escape($target) -or $missingResult.Output -notmatch 'found 0') {
            Add-PackageFailure $Failures "Store build did not reject a missing $target transform target with an exact-count error."
        }
    }

    $duplicateResult = Invoke-StoreFixtureBuild {
        param($FixtureRoot)
        $manifestPath = Join-Path $FixtureRoot "extensions\chromium\manifest.json"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw
        $manifest = [regex]::Replace($manifest, '(?m)^(  "key": .*\r?\n)', '$1$1')
        $manifest = [regex]::Replace($manifest, '(?ms)^(  "optional_permissions": \[.*?^  \],\r?\n)', '$1$1')
        [IO.File]::WriteAllText($manifestPath, $manifest)

        $backgroundPath = Join-Path $FixtureRoot "extensions\chromium\background.js"
        $background = Get-Content -LiteralPath $backgroundPath -Raw
        [IO.File]::WriteAllText($backgroundPath, $background + [Environment]::NewLine + $background)
    }

    foreach ($target in @('manifest key', 'optional_permissions', 'processIdsForTabs', 'chrome.action.onClicked')) {
        if ($duplicateResult.ExitCode -eq 0 -or $duplicateResult.Output -notmatch [regex]::Escape($target) -or $duplicateResult.Output -notmatch 'found 2') {
            Add-PackageFailure $Failures "Store build did not reject a duplicate $target transform target with an exact-count error."
        }
    }
}

function Test-ReleasePackages {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $failures = New-Object System.Collections.Generic.List[string]
    $desktopVersion = "0.1.15"
    $desktopFileVersion = "0.1.15.0"
    $extensionVersion = "0.1.6"
    $chromeExtensionId = "jnjminkakfohjeffdpeamngcnfneckog"
    $firefoxExtensionId = "monitor-audio-router@example.local"

    $trayExe = Join-Path $repositoryRoot "build\app\MonitorAudioRouter.exe"
    $nativeHostExe = Join-Path $repositoryRoot "build\app\MonitorAudioRouterNativeHost.exe"
    $setupExe = Join-Path $repositoryRoot "dist\MonitorAudioRouterSetup.exe"
    foreach ($binary in @(
        @{ Name = "Tray binary"; Path = $trayExe },
        @{ Name = "Native host binary"; Path = $nativeHostExe },
        @{ Name = "Installer binary"; Path = $setupExe }
    )) {
        if (-not (Test-Path -LiteralPath $binary.Path -PathType Leaf)) {
            Add-PackageFailure $failures "$($binary.Name) is missing at $($binary.Path)."
            continue
        }
        Assert-EqualPackageValue $failures "$($binary.Name) file version" $desktopFileVersion (Get-Item -LiteralPath $binary.Path).VersionInfo.FileVersion
    }

    foreach ($manifestPath in @(
        (Join-Path $repositoryRoot "src\app.manifest"),
        (Join-Path $repositoryRoot "installer-src\app.manifest")
    )) {
        [xml]$applicationManifest = Get-Content -LiteralPath $manifestPath -Raw
        $identity = $applicationManifest.DocumentElement.FirstChild
        Assert-EqualPackageValue $failures "$manifestPath assembly version" $desktopFileVersion ([string]$identity.version)
    }

    $expectedReleaseZip = Join-Path $repositoryRoot "dist\MonitorAudioRouter-$desktopVersion-github-release.zip"
    $releaseArchives = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "dist") -Filter "MonitorAudioRouter-*-github-release.zip" -File -ErrorAction SilentlyContinue
    )
    if ($releaseArchives.Count -ne 1 -or $releaseArchives[0].FullName -ne $expectedReleaseZip) {
        $actualReleaseNames = @($releaseArchives | ForEach-Object { $_.Name }) -join ", "
        Add-PackageFailure $failures "Desktop release archive expected MonitorAudioRouter-$desktopVersion-github-release.zip but found '$actualReleaseNames'."
    }
    $releaseZip = if ($releaseArchives.Count -eq 1) { $releaseArchives[0].FullName } else { $expectedReleaseZip }

    $chromeSourceManifestPath = Join-Path $repositoryRoot "extensions\chromium\manifest.json"
    $firefoxSourceManifestPath = Join-Path $repositoryRoot "extensions\firefox\manifest.json"
    $chromeSourceManifest = Get-Content -LiteralPath $chromeSourceManifestPath -Raw | ConvertFrom-Json
    $firefoxSourceManifest = Get-Content -LiteralPath $firefoxSourceManifestPath -Raw | ConvertFrom-Json
    Assert-EqualPackageValue $failures "Chromium source manifest version" $extensionVersion ([string]$chromeSourceManifest.version)
    Assert-EqualPackageValue $failures "Firefox source manifest version" $extensionVersion ([string]$firefoxSourceManifest.version)
    Assert-EqualPackageValue $failures "Firefox published extension ID" $firefoxExtensionId ([string]$firefoxSourceManifest.browser_specific_settings.gecko.id)

    $installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "installer-src\Program.cs") -Raw
    $updateSupportSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "shared-src\UpdateSupport.cs") -Raw
    $installerVersionMatch = [regex]::Match($installerSource, 'const string AppVersion = "([^"]+)";')
    $chromeIdMatch = [regex]::Match($updateSupportSource, 'const string ChromeExtensionId = "([^"]+)";')
    $firefoxIdMatch = [regex]::Match($updateSupportSource, 'const string FirefoxExtensionId = "([^"]+)";')
    Assert-EqualPackageValue $failures "Installer application version" $desktopVersion $installerVersionMatch.Groups[1].Value
    Assert-EqualPackageValue $failures "Installer Chrome extension ID" $chromeExtensionId $chromeIdMatch.Groups[1].Value
    Assert-EqualPackageValue $failures "Installer Firefox extension ID" $firefoxExtensionId $firefoxIdMatch.Groups[1].Value

    $readme = Get-Content -LiteralPath (Join-Path $repositoryRoot "README.md") -Raw
    if ($readme -notmatch [regex]::Escape("chromewebstore.google.com/detail/$chromeExtensionId")) {
        Add-PackageFailure $failures "README does not preserve the published Chrome extension ID $chromeExtensionId."
    }

    $chromeZip = Join-Path $repositoryRoot "packages\browser-store\chrome\monitor-audio-router.zip"
    $firefoxZip = Join-Path $repositoryRoot "packages\browser-store\firefox\monitor-audio-router.zip"
    $firefoxXpi = Join-Path $repositoryRoot "packages\browser-store\firefox\monitor-audio-router.xpi"
    $payloadZip = Join-Path $repositoryRoot "installer-src\Resources\payload.zip"
    foreach ($archive in @(
        @{ Name = "Chrome store archive"; Path = $chromeZip },
        @{ Name = "Firefox store ZIP"; Path = $firefoxZip },
        @{ Name = "Firefox store XPI"; Path = $firefoxXpi },
        @{ Name = "Installer payload archive"; Path = $payloadZip },
        @{ Name = "GitHub release archive"; Path = $releaseZip }
    )) {
        Test-DeterministicArchive $failures $archive.Name $archive.Path
    }

    $expectedStoreEntries = [string[]]@(
        "background.js",
        "icons/icon-128.png",
        "icons/icon-16.png",
        "icons/icon-32.png",
        "icons/icon-48.png",
        "icons/icon-64.png",
        "icons/icon-96.png",
        "manifest.json"
    )
    foreach ($archivePath in @($chromeZip, $firefoxZip, $firefoxXpi)) {
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            continue
        }
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $actualEntries = [string[]]@($archive.Entries | ForEach-Object { $_.FullName })
            $difference = @(Compare-Object -ReferenceObject $expectedStoreEntries -DifferenceObject $actualEntries)
            if ($difference.Count -ne 0) {
                Add-PackageFailure $failures "Store archive $archivePath contains missing or development-only files: $($difference.InputObject -join ', ')."
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    if (Test-Path -LiteralPath $chromeZip -PathType Leaf) {
        $chromePackageManifest = Get-ZipEntryText $chromeZip "manifest.json" | ConvertFrom-Json
        Assert-EqualPackageValue $failures "Chromium store manifest version" $extensionVersion ([string]$chromePackageManifest.version)
        $chromePackagePropertyNames = @($chromePackageManifest.PSObject.Properties | ForEach-Object { $_.Name })
        if ($chromePackagePropertyNames -contains "key" -or
            $chromePackagePropertyNames -contains "optional_permissions" -or
            @($chromePackageManifest.permissions) -contains "processes") {
            Add-PackageFailure $failures "Chromium store manifest contains development-only key or process permission values."
        }
        $chromePackageBackground = Get-ZipEntryText $chromeZip "background.js"
        if ($chromePackageBackground -match 'chrome\.processes|chrome\.permissions') {
            Add-PackageFailure $failures "Chromium store background contains development-only process permission code."
        }
    }

    foreach ($archivePath in @($firefoxZip, $firefoxXpi)) {
        if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
            $firefoxPackageManifest = Get-ZipEntryText $archivePath "manifest.json" | ConvertFrom-Json
            Assert-EqualPackageValue $failures "$archivePath manifest version" $extensionVersion ([string]$firefoxPackageManifest.version)
            Assert-EqualPackageValue $failures "$archivePath Gecko ID" $firefoxExtensionId ([string]$firefoxPackageManifest.browser_specific_settings.gecko.id)
        }
    }

    Test-StoreTransformValidation $failures

    if ($failures.Count -ne 0) {
        throw "Package regression checks failed:`n - $($failures -join "`n - ")"
    }
}

function Assert-IsolatedNuGetAuditDisabled {
    if ($env:NuGetAudit -ne "false") {
        throw "The isolated regression harness must set NuGetAudit=false."
    }
}

function Assert-CheckedInvocationRejectsNativeFailure {
    $expectedMessage = "Native failure probe failed with exit code 23"
    $actualMessage = $null
    try {
        Invoke-CheckedCommand "Native failure probe" {
            & $env:ComSpec /d /c "exit 23"
        }
    }
    catch {
        $actualMessage = $_.Exception.Message
    }

    if ($actualMessage -ne $expectedMessage) {
        throw "Checked invocation did not reject the native failure as expected. Actual: $actualMessage"
    }
}

Assert-IsolatedNuGetAuditDisabled
Assert-CheckedInvocationRejectsNativeFailure
Write-Host "[PASS] NuGet audit disabled for isolated validation"
Write-Host "[PASS] Checked invocation rejects native failures"
if ($HarnessSelfTestOnly) {
    exit 0
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw "dotnet.exe is required to run the C# regression tests."
}

$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
if ($null -eq $nodeCommand) {
    throw "node.exe is required to run the browser extension regression tests."
}

$passedSuites = 1
$totalSuites = 8

$powerShellScripts = @(
    Get-ChildItem -LiteralPath $repositoryRoot -Filter "*.ps1" -File
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "installer-src") -Filter "*.ps1" -File
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1" -File
)
$powerShellScripts = $powerShellScripts | Sort-Object FullName -Unique
foreach ($script in $powerShellScripts) {
    Test-PowerShellSyntax $script.FullName
}
$passedSuites++
Write-Host "[PASS] PowerShell syntax ($($powerShellScripts.Count) scripts)"

Invoke-CheckedCommand "Full installer build" {
    & $installerBuild
}
$passedSuites++
Write-Host "[PASS] Full installer build"

Test-ReleasePackages
$passedSuites++
Write-Host "[PASS] Release package contracts"

$javaScriptFiles = @(
    $browserTestRunner
    (Join-Path $repositoryRoot "extensions\chromium\background.js")
    (Join-Path $repositoryRoot "extensions\firefox\background.js")
)
foreach ($javaScriptFile in $javaScriptFiles) {
    Invoke-CheckedCommand "JavaScript syntax check for $javaScriptFile" {
        & $nodeCommand.Source --check $javaScriptFile
    }
}
$passedSuites++
Write-Host "[PASS] JavaScript syntax ($($javaScriptFiles.Count) scripts)"

Invoke-CheckedCommand "C# regression tests" {
    & $dotnetCommand.Source run --project $csharpTestProject -c Release
}
$passedSuites++

Invoke-CheckedCommand "Installer PowerShell regression tests" {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPowerShellTestRunner
}
$passedSuites++

Invoke-CheckedCommand "Browser extension regression tests" {
    & $nodeCommand.Source $browserTestRunner
}
$passedSuites++

Write-Host "Regression suites passed: $passedSuites/$totalSuites."
