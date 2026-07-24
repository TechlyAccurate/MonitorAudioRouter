param(
    [switch]$MachineWide,
    [switch]$EnablePrivateBrowsing
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostName = "com.monitoraudiorouter.router"
$firefoxExtensionId = "monitor-audio-router@example.local"
$chromeStoreExtensionId = "jnjminkakfohjeffdpeamngcnfneckog"
$chromiumExtensionId = "hapnllbljmoigdhecifbifcdkkmjhlik"
$unpackedChromiumExtensionId = "ikgjfiahjkfjaalkdcbekpfjeaednafn"

$appDir = Join-Path $root "app"
$trayExe = Join-Path $appDir "MonitorAudioRouter.exe"
$nativeHostExe = Join-Path $appDir "MonitorAudioRouterNativeHost.exe"
$packagesDir = Join-Path $root "packages"
$chromiumSource = Join-Path $packagesDir "chromium-src"
$firefoxXpi = Join-Path $packagesDir "monitor-audio-router-firefox.xpi"
$manifestDataRoot = if ($MachineWide) {
    [Environment]::GetFolderPath("CommonApplicationData")
}
else {
    [Environment]::GetFolderPath("LocalApplicationData")
}
$nativeHostManifestDir = Join-Path (Join-Path $manifestDataRoot "Monitor Audio Router") "native-hosts"
$devRuntimeDir = Join-Path (Join-Path $manifestDataRoot "Monitor Audio Router") "dev-app"
$devNativeHostExe = Join-Path $devRuntimeDir "MonitorAudioRouterNativeHost.exe"
$chromiumHostManifest = Join-Path $nativeHostManifestDir "chromium-com.monitoraudiorouter.router.json"
$firefoxHostManifest = Join-Path $nativeHostManifestDir "firefox-com.monitoraudiorouter.router.json"
$legacyNativeHostManifestDir = Join-Path $root "native-hosts"
$legacyChromiumHostManifest = Join-Path $legacyNativeHostManifestDir "chromium-com.monitoraudiorouter.router.json"
$legacyFirefoxHostManifest = Join-Path $legacyNativeHostManifestDir "firefox-com.monitoraudiorouter.router.json"

function ConvertTo-FileUrl([string]$Path) {
    return ([Uri](Resolve-Path $Path).Path).AbsoluteUri
}

function Compress-DirectoryWithForwardSlashes([string]$SourceDirectory, [string]$DestinationPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory).Path
    Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue

    $archive = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem -LiteralPath $resolvedSource -File -Recurse
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($resolvedSource.Length)
            $relativePath = $relativePath.TrimStart([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
            $entryName = $relativePath -replace "\\", "/"
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-RegistryHiveName {
    if ($MachineWide) {
        return "HKLM"
    }

    return "HKCU"
}

function Get-RegistryDriveRoot {
    if ($MachineWide) {
        return "HKLM:"
    }

    return "HKCU:"
}

function Set-DefaultRegistryValue([string]$SubKey, [string]$Value) {
    $hive = Get-RegistryHiveName
    & reg.exe add "$hive\$SubKey" /ve /t REG_SZ /d "$Value" /f | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "reg.exe add failed for $hive\$SubKey with exit code $LASTEXITCODE"
    }
}

function Set-StringPolicy([string]$Path, [string]$Name, [string]$Value) {
    New-Item -Path $Path -Force | Out-Null
    New-ItemProperty -Path $Path -Name $Name -PropertyType String -Value $Value -Force | Out-Null
}

function Prepare-ChromiumPackage {
    New-Item -ItemType Directory -Force $packagesDir, $chromiumSource | Out-Null
    Remove-Item -LiteralPath (Join-Path $packagesDir "chromium-src.crx") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $packagesDir "chromium-updates.xml") -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "extensions\chromium\background.js") (Join-Path $chromiumSource "background.js") -Force
    Remove-Item -LiteralPath (Join-Path $chromiumSource "icons") -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath (Join-Path $root "extensions\chromium\icons") -Destination (Join-Path $chromiumSource "icons") -Recurse -Force

    $manifest = Get-Content (Join-Path $root "extensions\chromium\manifest.json") -Raw | ConvertFrom-Json
    # Keep the key in the unpacked dev package so Chrome assigns the native-host allowed extension ID.
    $manifest | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $chromiumSource "manifest.json") -Encoding utf8
}

function Prepare-FirefoxPackage {
    New-Item -ItemType Directory -Force $packagesDir | Out-Null
    Remove-Item $firefoxXpi -Force -ErrorAction SilentlyContinue
    Compress-DirectoryWithForwardSlashes (Join-Path $root "extensions\firefox") $firefoxXpi
}

function Copy-DevRuntime {
    if (Test-Path -LiteralPath $devRuntimeDir) {
        Get-ChildItem -LiteralPath $devRuntimeDir -Force | Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Force $devRuntimeDir | Out-Null
    }

    Get-ChildItem -LiteralPath $appDir -Force | Where-Object {
        $_.Name -notmatch '^(router\.log|state\.json|config\.json|browser-bridge\.token|.*\.pdb)$'
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $devRuntimeDir $_.Name) -Recurse -Force
    }
}

function Register-NativeMessagingHosts {
    New-Item -ItemType Directory -Force $nativeHostManifestDir, $legacyNativeHostManifestDir | Out-Null

    $chromiumManifest = [ordered]@{
        name = $hostName
        description = "Monitor Audio Router browser bridge"
        path = (Resolve-Path $devNativeHostExe).Path
        type = "stdio"
        allowed_origins = @(
            "chrome-extension://$chromeStoreExtensionId/",
            "chrome-extension://$chromiumExtensionId/",
            "chrome-extension://$unpackedChromiumExtensionId/"
        )
    }
    $chromiumManifest | ConvertTo-Json -Depth 10 | Set-Content $chromiumHostManifest -Encoding utf8

    $firefoxManifest = [ordered]@{
        name = $hostName
        description = "Monitor Audio Router browser bridge"
        path = (Resolve-Path $devNativeHostExe).Path
        type = "stdio"
        allowed_extensions = @($firefoxExtensionId)
    }
    $firefoxManifest | ConvertTo-Json -Depth 10 | Set-Content $firefoxHostManifest -Encoding utf8
    $chromiumManifest | ConvertTo-Json -Depth 10 | Set-Content $legacyChromiumHostManifest -Encoding utf8
    $firefoxManifest | ConvertTo-Json -Depth 10 | Set-Content $legacyFirefoxHostManifest -Encoding utf8

    Set-DefaultRegistryValue "Software\Google\Chrome\NativeMessagingHosts\$hostName" $chromiumHostManifest
    Set-DefaultRegistryValue "Software\Chromium\NativeMessagingHosts\$hostName" $chromiumHostManifest
    Set-DefaultRegistryValue "Software\Microsoft\Edge\NativeMessagingHosts\$hostName" $chromiumHostManifest
    Set-DefaultRegistryValue "Software\Mozilla\NativeMessagingHosts\$hostName" $firefoxHostManifest
}

function Remove-ChromiumDevPolicies {
    $rootKey = Get-RegistryDriveRoot
    $policyRoots = @(
        "$rootKey\Software\Policies\Google\Chrome",
        "$rootKey\Software\Policies\Chromium",
        "$rootKey\Software\Policies\Microsoft\Edge"
    )

    foreach ($policyRoot in $policyRoots) {
        if (-not (Test-Path -LiteralPath $policyRoot)) {
            continue
        }

        $settingsValue = (Get-ItemProperty -LiteralPath $policyRoot -Name "ExtensionSettings" -ErrorAction SilentlyContinue).ExtensionSettings
        if ($settingsValue) {
            try {
                $settings = $settingsValue | ConvertFrom-Json
                if ($settings.PSObject.Properties.Name -contains $chromiumExtensionId) {
                    $settings.PSObject.Properties.Remove($chromiumExtensionId)
                    if ($settings.PSObject.Properties.Count -gt 0) {
                        Set-StringPolicy $policyRoot "ExtensionSettings" ($settings | ConvertTo-Json -Compress -Depth 10)
                    }
                    else {
                        Remove-ItemProperty -LiteralPath $policyRoot -Name "ExtensionSettings" -ErrorAction SilentlyContinue
                    }
                }
            }
            catch {
                Write-Warning "Could not parse Chromium ExtensionSettings at $policyRoot; leaving it unchanged."
            }
        }

        $forcePath = Join-Path $policyRoot "ExtensionInstallForcelist"
        if (Test-Path -LiteralPath $forcePath) {
            $forceItem = Get-ItemProperty -LiteralPath $forcePath
            foreach ($property in $forceItem.PSObject.Properties) {
                if ($property.Name -like "PS*") {
                    continue
                }

                if (($property.Value -as [string]) -like "$chromiumExtensionId;*") {
                    Remove-ItemProperty -LiteralPath $forcePath -Name $property.Name -ErrorAction SilentlyContinue
                }
            }
        }
    }
}

function Install-FirefoxPolicies {
    $rootKey = Get-RegistryDriveRoot
    $firefoxUrl = ConvertTo-FileUrl $firefoxXpi
    $policyRoot = "$rootKey\Software\Policies\Mozilla\Firefox"
    $extensionSetting = @{
        installation_mode = "force_installed"
        install_url = $firefoxUrl
        updates_disabled = $true
    }

    if ($EnablePrivateBrowsing) {
        $extensionSetting.private_browsing = $true
    }

    $settings = @{
        $firefoxExtensionId = $extensionSetting
    } | ConvertTo-Json -Compress -Depth 10

    Set-StringPolicy $policyRoot "ExtensionSettings" $settings
}

function Install-ShortcutsAndAutostart {
    $resolvedTrayExe = (Resolve-Path $trayExe).Path
    $resolvedAppDir = (Resolve-Path $appDir).Path
    $iconPath = Join-Path $appDir "MonitorAudioRouter.ico"
    $resolvedIcon = $null
    if (Test-Path $iconPath) {
        $resolvedIcon = (Resolve-Path $iconPath).Path
    }

    $programsDir = [Environment]::GetFolderPath("Programs")
    $shortcutPath = Join-Path $programsDir "Monitor Audio Router.lnk"

    New-Item -ItemType Directory -Force $programsDir | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $resolvedTrayExe
    $shortcut.WorkingDirectory = $resolvedAppDir
    $shortcut.IconLocation = if ($null -ne $resolvedIcon) { "$resolvedIcon,0" } else { "$resolvedTrayExe,0" }
    $shortcut.Description = "Monitor Audio Router"
    $shortcut.Save()

    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name "Monitor Audio Router" -PropertyType String -Value "`"$resolvedTrayExe`"" -Force | Out-Null
}

if (-not (Test-Path $trayExe)) {
    throw "Tray executable not found: $trayExe"
}

if (-not (Test-Path $nativeHostExe)) {
    throw "Native host executable not found: $nativeHostExe"
}

Prepare-ChromiumPackage
Prepare-FirefoxPackage
Copy-DevRuntime
Register-NativeMessagingHosts
try {
    Remove-ChromiumDevPolicies
}
catch {
    Write-Warning "Could not update Chromium development policies: $($_.Exception.Message)"
}

try {
    Install-FirefoxPolicies
}
catch {
    Write-Warning "Could not update Firefox development policy: $($_.Exception.Message)"
}

Install-ShortcutsAndAutostart

Write-Host "Monitor Audio Router browser deployment installed."
Write-Host "Scope: $(if ($MachineWide) { 'machine-wide' } else { 'current Windows user' })"
Write-Host "Chrome Web Store extension id: $chromeStoreExtensionId"
Write-Host "Chromium dev extension prepared at: $chromiumSource"
Write-Host "Load that folder unpacked in Chrome/Edge Developer Mode; current Chromium builds do not install unsigned local CRX packages by policy."
Write-Host "Firefox extension id: $firefoxExtensionId"
Write-Host "Restart Firefox, then check about:policies."
Write-Host "Start Menu shortcut and current-user autostart are installed."
Write-Host "Private/incognito browser support is optional. Use -EnablePrivateBrowsing for Firefox development policy support; enable Chromium incognito/InPrivate per profile after loading the unpacked extension."
