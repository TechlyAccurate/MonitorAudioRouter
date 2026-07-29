param(
    [switch]$Quiet
)

$ErrorActionPreference = "SilentlyContinue"

$hostName = "com.monitoraudiorouter.router"
$runValueName = "Monitor Audio Router"
$uninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorAudioRouter"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "Monitor Audio Router.lnk"
$trayExe = Join-Path $PSScriptRoot "MonitorAudioRouter.exe"
$installInfoPath = Join-Path $PSScriptRoot "install-info.json"
$chromeExtensionIds = @()
$edgeExtensionIds = @()
$firefoxExtensionIds = @()

function Add-ConfiguredIds([object]$Value) {
    return @($Value) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
}

if (Test-Path -LiteralPath $installInfoPath) {
    try {
        $installInfo = Get-Content -LiteralPath $installInfoPath -Raw | ConvertFrom-Json
        $chromeExtensionIds = Add-ConfiguredIds $installInfo.ChromeExtensionIds
        $edgeExtensionIds = Add-ConfiguredIds $installInfo.EdgeExtensionIds
        $firefoxExtensionIds = Add-ConfiguredIds $installInfo.FirefoxExtensionIds
    }
    catch {
        $chromeExtensionIds = @()
        $edgeExtensionIds = @()
        $firefoxExtensionIds = @()
    }
}

function Remove-ForcelistEntries([string]$Path, [object[]]$ExtensionIds) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $key = Get-Item -LiteralPath $Path
    foreach ($name in $key.GetValueNames()) {
        $value = [string]$key.GetValue($name)
        foreach ($id in $ExtensionIds) {
            if (-not [string]::IsNullOrWhiteSpace([string]$id) -and $value.StartsWith("$id;", [StringComparison]::OrdinalIgnoreCase)) {
                Remove-ItemProperty -LiteralPath $Path -Name $name
                break
            }
        }
    }
}

function Remove-FirefoxExtensionSettings([string]$Path, [object[]]$ExtensionIds) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $raw = (Get-ItemProperty -LiteralPath $Path -Name ExtensionSettings).ExtensionSettings
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return
    }

    try {
        $settings = $raw | ConvertFrom-Json
    }
    catch {
        return
    }

    foreach ($id in $ExtensionIds) {
        if (-not [string]::IsNullOrWhiteSpace([string]$id)) {
            $settings.PSObject.Properties.Remove([string]$id)
        }
    }

    if ($settings.PSObject.Properties.Count -eq 0) {
        Remove-ItemProperty -LiteralPath $Path -Name ExtensionSettings
    }
    else {
        Set-ItemProperty -LiteralPath $Path -Name ExtensionSettings -Value ($settings | ConvertTo-Json -Compress -Depth 10)
    }
}

if (Test-Path -LiteralPath $trayExe) {
    & $trayExe --clear-managed-routes | Out-Null
}

Get-Process MonitorAudioRouterNativeHost,MonitorAudioRouter -ErrorAction SilentlyContinue | Stop-Process -Force

$nativeHostKeys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Chromium\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName",
    "HKCU:\Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\WOW6432Node\Chromium\NativeMessagingHosts\$hostName",
    "HKCU:\Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKCU:\Software\WOW6432Node\Mozilla\NativeMessagingHosts\$hostName",
    "HKLM:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKLM:\Software\Chromium\NativeMessagingHosts\$hostName",
    "HKLM:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKLM:\Software\Mozilla\NativeMessagingHosts\$hostName",
    "HKLM:\Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKLM:\Software\WOW6432Node\Chromium\NativeMessagingHosts\$hostName",
    "HKLM:\Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKLM:\Software\WOW6432Node\Mozilla\NativeMessagingHosts\$hostName"
)

foreach ($key in $nativeHostKeys) {
    Remove-Item -LiteralPath $key -Recurse -Force
}

Remove-ForcelistEntries "HKLM:\Software\Policies\Google\Chrome\ExtensionInstallForcelist" $chromeExtensionIds
Remove-ForcelistEntries "HKLM:\Software\Policies\Chromium\ExtensionInstallForcelist" $chromeExtensionIds
Remove-ForcelistEntries "HKLM:\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist" $edgeExtensionIds
Remove-FirefoxExtensionSettings "HKLM:\Software\Policies\Mozilla\Firefox" $firefoxExtensionIds

Remove-ItemProperty -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name $runValueName
Remove-Item -LiteralPath $uninstallKey -Recurse -Force
Remove-Item -LiteralPath $shortcutPath -Force

$installDir = $PSScriptRoot
$escapedInstallDir = $installDir.Replace("'", "''")
$deleteCommand = "Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '$escapedInstallDir' -Recurse -Force"
Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $deleteCommand) -WindowStyle Hidden

if (-not $Quiet) {
    Write-Host "Monitor Audio Router uninstalled."
}
