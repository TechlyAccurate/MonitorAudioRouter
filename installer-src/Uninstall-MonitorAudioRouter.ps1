param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$hostName = "com.monitoraudiorouter.router"
$runValueName = "Monitor Audio Router"
$uninstallSubKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorAudioRouter"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "Monitor Audio Router.lnk"
$installDir = [IO.Path]::GetFullPath($PSScriptRoot)
$trayExe = Join-Path $installDir "MonitorAudioRouter.exe"
$nativeHostExe = Join-Path $installDir "MonitorAudioRouterNativeHost.exe"
$installInfoPath = Join-Path $installDir "install-info.json"
$installInfo = $null
$chromeExtensionIds = @()
$edgeExtensionIds = @()
$firefoxExtensionIds = @()
$registryValues = @()

function Add-ConfiguredIds([object]$Value) {
    return @($Value) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
}

if (Test-Path -LiteralPath $installInfoPath -PathType Leaf) {
    try {
        $installInfo = Get-Content -LiteralPath $installInfoPath -Raw | ConvertFrom-Json
        $chromeExtensionIds = Add-ConfiguredIds $installInfo.ChromeExtensionIds
        $edgeExtensionIds = Add-ConfiguredIds $installInfo.EdgeExtensionIds
        $firefoxExtensionIds = Add-ConfiguredIds $installInfo.FirefoxExtensionIds
        if ($installInfo.PSObject.Properties.Name -contains "RegistryValues") {
            $registryValues = @($installInfo.RegistryValues)
        }
    }
    catch {
        $installInfo = $null
        $registryValues = @()
    }
}

function Test-ExactExecutablePath([string]$CandidatePath, [string]$ExpectedPath) {
    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $false
    }

    try {
        $candidate = [IO.Path]::GetFullPath($CandidatePath)
        $expected = [IO.Path]::GetFullPath($ExpectedPath)
        return [string]::Equals($candidate, $expected, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Stop-InstalledProcesses {
    $expectedPaths = @($trayExe, $nativeHostExe)
    $installedProcesses = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
    $candidates = @(Get-Process -Name "MonitorAudioRouter", "MonitorAudioRouterNativeHost" -ErrorAction SilentlyContinue)
    foreach ($process in $candidates) {
        try {
            $processPath = $process.MainModule.FileName
            $matches = $false
            foreach ($expectedPath in $expectedPaths) {
                if (Test-ExactExecutablePath $processPath $expectedPath) {
                    $matches = $true
                    break
                }
            }

            if ($matches) {
                $installedProcesses.Add($process)
            }
            else {
                $process.Dispose()
            }
        }
        catch {
            $processId = $process.Id
            $failureMessage = $_.Exception.Message
            $process.Dispose()
            foreach ($installedProcess in $installedProcesses) {
                $installedProcess.Dispose()
            }
            throw "Process PID $processId has an installed executable name, but its path could not be verified: $failureMessage"
        }
    }

    try {
        foreach ($process in $installedProcesses) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        foreach ($process in $installedProcesses) {
            $remaining = $deadline - [DateTime]::UtcNow
            if ($remaining -gt [TimeSpan]::Zero) {
                [void]$process.WaitForExit([Math]::Min([int]$remaining.TotalMilliseconds, [int]::MaxValue))
            }

            $process.Refresh()
            if (-not $process.HasExited) {
                throw "Installed process PID $($process.Id) did not exit within the shutdown timeout."
            }
        }
    }
    finally {
        foreach ($process in $installedProcesses) {
            $process.Dispose()
        }
    }
}

function Clear-ManagedRoutes {
    if (-not (Test-Path -LiteralPath $trayExe -PathType Leaf)) {
        return
    }

    $cleanupProcess = Start-Process -FilePath $trayExe -ArgumentList "--clear-managed-routes" -PassThru -WindowStyle Hidden
    try {
        if (-not $cleanupProcess.WaitForExit(5000)) {
            Stop-Process -Id $cleanupProcess.Id -Force -ErrorAction SilentlyContinue
            throw "The installed route cleanup helper did not exit within five seconds."
        }

        if ($cleanupProcess.ExitCode -ne 0) {
            throw "The installed route cleanup helper exited with code $($cleanupProcess.ExitCode)."
        }
    }
    finally {
        $cleanupProcess.Dispose()
    }
}

function Get-RegistryPath([string]$Hive, [string]$SubKey) {
    return "${Hive}:\$SubKey"
}

function Get-RegistrySnapshot([string]$Path, [string]$ValueName) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null }
    }

    $key = Get-Item -LiteralPath $Path
    $exists = @($key.GetValueNames()) -contains $ValueName
    if (-not $exists) {
        return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null }
    }

    return [pscustomobject]@{
        Exists = $true
        Value = [string]$key.GetValue($ValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind = $key.GetValueKind($ValueName).ToString()
    }
}

function Test-SnapshotEqual([object]$Left, [object]$Right) {
    if ([bool]$Left.Exists -ne [bool]$Right.Exists) {
        return $false
    }

    if (-not [bool]$Left.Exists) {
        return $true
    }

    return [string]::Equals([string]$Left.Value, [string]$Right.Value, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Left.Kind, [string]$Right.Kind, [StringComparison]::Ordinal)
}

function Restore-RegistrySnapshot([string]$Path, [string]$ValueName, [object]$Snapshot) {
    if (-not [bool]$Snapshot.Exists) {
        if (Test-Path -LiteralPath $Path) {
            $key = Get-Item -LiteralPath $Path
            $key.DeleteValue($ValueName, $false)
        }
        return
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }

    $key = Get-Item -LiteralPath $Path
    $kind = [Microsoft.Win32.RegistryValueKind][Enum]::Parse(
        [Microsoft.Win32.RegistryValueKind],
        [string]$Snapshot.Kind)
    $key.SetValue($ValueName, [string]$Snapshot.Value, $kind)
}

function ConvertTo-CompactJson([object]$Value) {
    if ($null -eq $Value) {
        return $null
    }

    return $Value | ConvertTo-Json -Compress -Depth 20
}

function Restore-OwnedJsonProperty([object]$Ownership) {
    $path = Get-RegistryPath ([string]$Ownership.Hive) ([string]$Ownership.SubKey)
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $key = Get-Item -LiteralPath $path
    $raw = $key.GetValue([string]$Ownership.ValueName)
    if ([string]::IsNullOrWhiteSpace([string]$raw)) {
        return
    }

    try {
        $settings = [string]$raw | ConvertFrom-Json
    }
    catch {
        return
    }

    $propertyName = [string]$Ownership.JsonPropertyName
    $property = $settings.PSObject.Properties[$propertyName]
    $current = [pscustomobject]@{
        Exists = $null -ne $property
        Value = if ($null -ne $property) { ConvertTo-CompactJson $property.Value } else { $null }
        Kind = "Json"
    }
    if (-not (Test-SnapshotEqual $current $Ownership.Written)) {
        return
    }

    if ([bool]$Ownership.Prior.Exists) {
        $settings | Add-Member -NotePropertyName $propertyName -NotePropertyValue ([string]$Ownership.Prior.Value | ConvertFrom-Json) -Force
    }
    else {
        $settings.PSObject.Properties.Remove($propertyName)
    }

    if ($settings.PSObject.Properties.Count -eq 0) {
        $key.DeleteValue([string]$Ownership.ValueName, $false)
    }
    else {
        $key.SetValue(
            [string]$Ownership.ValueName,
            (ConvertTo-CompactJson $settings),
            [Microsoft.Win32.RegistryValueKind]::String)
    }
}

function Restore-OwnedRegistryValues([object[]]$OwnershipValues) {
    for ($valueIndex = $OwnershipValues.Count - 1; $valueIndex -ge 0; $valueIndex--) {
        $ownership = $OwnershipValues[$valueIndex]
        if (-not [string]::IsNullOrWhiteSpace([string]$ownership.JsonPropertyName)) {
            Restore-OwnedJsonProperty $ownership
            continue
        }

        $path = Get-RegistryPath ([string]$ownership.Hive) ([string]$ownership.SubKey)
        $current = Get-RegistrySnapshot $path ([string]$ownership.ValueName)
        if (Test-SnapshotEqual $current $ownership.Written) {
            Restore-RegistrySnapshot $path ([string]$ownership.ValueName) $ownership.Prior
        }
    }
}

function Remove-LegacyForcelistEntries([string]$Path, [object[]]$ExtensionIds, [string]$ExpectedUpdateUrl) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $key = Get-Item -LiteralPath $Path
    foreach ($name in $key.GetValueNames()) {
        $value = [string]$key.GetValue($name)
        foreach ($extensionId in $ExtensionIds) {
            if ([string]::Equals($value, "$extensionId;$ExpectedUpdateUrl", [StringComparison]::OrdinalIgnoreCase)) {
                $key.DeleteValue($name, $false)
                break
            }
        }
    }
}

function Remove-FirefoxExtensionProperties([string]$Path, [object[]]$ExtensionIds) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $key = Get-Item -LiteralPath $Path
    $raw = [string]$key.GetValue("ExtensionSettings")
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return
    }

    try {
        $settings = $raw | ConvertFrom-Json
    }
    catch {
        return
    }

    foreach ($extensionId in $ExtensionIds) {
        if (-not [string]::IsNullOrWhiteSpace([string]$extensionId)) {
            $settings.PSObject.Properties.Remove([string]$extensionId)
        }
    }

    if ($settings.PSObject.Properties.Count -eq 0) {
        $key.DeleteValue("ExtensionSettings", $false)
    }
    else {
        $key.SetValue("ExtensionSettings", (ConvertTo-CompactJson $settings), [Microsoft.Win32.RegistryValueKind]::String)
    }
}

function Remove-LegacyOwnedRegistryValues {
    $chromiumManifest = Join-Path $installDir "native-hosts\chromium-com.monitoraudiorouter.router.json"
    $firefoxManifest = Join-Path $installDir "native-hosts\firefox-com.monitoraudiorouter.router.json"
    $softwareRoots = if ([Environment]::Is64BitOperatingSystem) { @("Software", "Software\WOW6432Node") } else { @("Software") }
    foreach ($hive in @("HKCU", "HKLM")) {
        foreach ($softwareRoot in $softwareRoots) {
            foreach ($browserPath in @(
                "Google\Chrome\NativeMessagingHosts\$hostName",
                "Chromium\NativeMessagingHosts\$hostName",
                "Microsoft\Edge\NativeMessagingHosts\$hostName")) {
                $path = Get-RegistryPath $hive "$softwareRoot\$browserPath"
                $snapshot = Get-RegistrySnapshot $path ""
                if ($snapshot.Exists -and [string]::Equals($snapshot.Value, $chromiumManifest, [StringComparison]::OrdinalIgnoreCase)) {
                    Remove-Item -LiteralPath $path -Recurse -Force
                }
            }

            $firefoxPath = Get-RegistryPath $hive "$softwareRoot\Mozilla\NativeMessagingHosts\$hostName"
            $snapshot = Get-RegistrySnapshot $firefoxPath ""
            if ($snapshot.Exists -and [string]::Equals($snapshot.Value, $firefoxManifest, [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $firefoxPath -Recurse -Force
            }
        }
    }

    Remove-LegacyForcelistEntries "HKLM:\Software\Policies\Google\Chrome\ExtensionInstallForcelist" $chromeExtensionIds "https://clients2.google.com/service/update2/crx"
    Remove-LegacyForcelistEntries "HKLM:\Software\Policies\Chromium\ExtensionInstallForcelist" $chromeExtensionIds "https://clients2.google.com/service/update2/crx"
    Remove-LegacyForcelistEntries "HKLM:\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist" $edgeExtensionIds "https://edge.microsoft.com/extensionwebstorebase/v1/crx"
    Remove-FirefoxExtensionProperties "HKLM:\Software\Policies\Mozilla\Firefox" $firefoxExtensionIds

    $runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $runSnapshot = Get-RegistrySnapshot $runPath $runValueName
    $expectedRunValue = '"' + $trayExe + '"'
    if ($runSnapshot.Exists -and [string]::Equals($runSnapshot.Value, $expectedRunValue, [StringComparison]::OrdinalIgnoreCase)) {
        Restore-RegistrySnapshot $runPath $runValueName ([pscustomobject]@{ Exists = $false; Value = $null; Kind = $null })
    }

    $uninstallPath = Get-RegistryPath "HKLM" $uninstallSubKey
    $installLocation = Get-RegistrySnapshot $uninstallPath "InstallLocation"
    if ($installLocation.Exists -and [string]::Equals($installLocation.Value, $installDir, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $uninstallPath -Recurse -Force
    }
}

Stop-InstalledProcesses
Clear-ManagedRoutes

if ($registryValues.Count -gt 0) {
    Restore-OwnedRegistryValues $registryValues
}
else {
    Remove-LegacyOwnedRegistryValues
}

Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue

$escapedInstallDir = $installDir.Replace("'", "''")
$deleteCommand = "Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '$escapedInstallDir' -Recurse -Force"
Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $deleteCommand) -WindowStyle Hidden

if (-not $Quiet) {
    Write-Host "Monitor Audio Router uninstalled."
}
