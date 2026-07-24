param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = Join-Path $root "app"
$hostName = "com.monitoraudiorouter.router"
$firefoxExtensionId = "monitor-audio-router@example.local"
$chromeStoreExtensionId = "jnjminkakfohjeffdpeamngcnfneckog"
$chromiumExtensionId = "hapnllbljmoigdhecifbifcdkkmjhlik"
$unpackedChromiumExtensionId = "ikgjfiahjkfjaalkdcbekpfjeaednafn"

$programDataRoot = Join-Path $env:ProgramData "Monitor Audio Router"
$devRuntimeDir = Join-Path $programDataRoot "dev-app"
$nativeHostManifestDir = Join-Path $programDataRoot "native-hosts"
$nativeHostExe = Join-Path $devRuntimeDir "MonitorAudioRouterNativeHost.exe"
$chromiumHostManifest = Join-Path $nativeHostManifestDir "chromium-com.monitoraudiorouter.router.json"
$firefoxHostManifest = Join-Path $nativeHostManifestDir "firefox-com.monitoraudiorouter.router.json"
$statusPath = Join-Path $programDataRoot "hklm-native-host-repair-status.json"

function Assert-UnderDirectory([string]$RootPath, [string]$TargetPath) {
    $resolvedRoot = [IO.Path]::GetFullPath($RootPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedTarget = [IO.Path]::GetFullPath($TargetPath)
    $rootPrefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside expected directory: $resolvedTarget"
    }
}

function Set-DefaultRegistryValue([string]$SubKey, [string]$Value) {
    & reg.exe add $SubKey /ve /t REG_SZ /d $Value /f | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "reg.exe add failed for $SubKey with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $appDir "MonitorAudioRouterNativeHost.exe"))) {
    throw "Native host executable was not found under $appDir"
}

New-Item -ItemType Directory -Force $programDataRoot, $nativeHostManifestDir | Out-Null
Assert-UnderDirectory $programDataRoot $devRuntimeDir

Get-Process MonitorAudioRouterNativeHost -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $devRuntimeDir) {
    Get-ChildItem -LiteralPath $devRuntimeDir -Force |
        Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Force $devRuntimeDir | Out-Null
}

Get-ChildItem -LiteralPath $appDir -Force |
    Where-Object { $_.Name -notmatch '^(router\.log|state\.json|config\.json|browser-bridge\.token|.*\.pdb)$' } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $devRuntimeDir $_.Name) -Recurse -Force
    }

$chromiumManifest = [ordered]@{
    name = $hostName
    description = "Monitor Audio Router browser bridge"
    path = (Resolve-Path -LiteralPath $nativeHostExe).Path
    type = "stdio"
    allowed_origins = @(
        "chrome-extension://$chromeStoreExtensionId/",
        "chrome-extension://$chromiumExtensionId/",
        "chrome-extension://$unpackedChromiumExtensionId/"
    )
}
$chromiumManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $chromiumHostManifest -Encoding utf8

$firefoxManifest = [ordered]@{
    name = $hostName
    description = "Monitor Audio Router browser bridge"
    path = (Resolve-Path -LiteralPath $nativeHostExe).Path
    type = "stdio"
    allowed_extensions = @($firefoxExtensionId)
}
$firefoxManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $firefoxHostManifest -Encoding utf8

$registryEntries = @(
    @{ Key = "HKLM\Software\Google\Chrome\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\Chromium\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\Mozilla\NativeMessagingHosts\$hostName"; Value = $firefoxHostManifest },
    @{ Key = "HKLM\Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\WOW6432Node\Chromium\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\$hostName"; Value = $chromiumHostManifest },
    @{ Key = "HKLM\Software\WOW6432Node\Mozilla\NativeMessagingHosts\$hostName"; Value = $firefoxHostManifest }
)

foreach ($entry in $registryEntries) {
    Set-DefaultRegistryValue $entry.Key $entry.Value
}

[pscustomobject]@{
    UpdatedAt = [DateTimeOffset]::Now.ToString("o")
    NativeHostExe = $nativeHostExe
    ChromiumManifest = $chromiumHostManifest
    FirefoxManifest = $firefoxHostManifest
    RegistryKeys = @($registryEntries | ForEach-Object { $_.Key })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusPath -Encoding utf8
