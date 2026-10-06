$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$uninstallerPath = Join-Path $repositoryRoot "installer-src\Uninstall-MonitorAudioRouter.ps1"
. $uninstallerPath -LoadFunctionsOnly

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Message Expected: $Expected; Actual: $Actual"
    }
}

$temporarySubKey = "Software\MonitorAudioRouter\RegressionTests\$([Guid]::NewGuid().ToString('N'))"
$temporaryRegistryPath = "HKCU:\$temporarySubKey"
$passed = 0
$key = $null
$forcelistKey = $null
$firefoxKey = $null

try {
    $expectedPath = "C:\Program Files\Monitor Audio Router\MonitorAudioRouter.exe"
    $replacementPath = "C:\Other\MonitorAudioRouter.exe"
    $stopCalled = $false
    $identityFailure = $null
    try {
        Stop-VerifiedInstalledProcess `
            -Process ([pscustomobject]@{ Id = 4001 }) `
            -ExpectedPaths @($expectedPath) `
            -PathReader { param($Process) $replacementPath } `
            -Stopper { param($Process) $script:stopCalled = $true }
    }
    catch {
        $identityFailure = $_.Exception.Message
    }

    Assert-True (-not $stopCalled) "An identity change must prevent process termination."
    Assert-True ($identityFailure -like "*identity changed*") "An identity change must fail closed with a specific error."
    Write-Host "[PASS] Uninstaller revalidates executable identity immediately before termination"
    $passed++

    New-Item -Path $temporaryRegistryPath -Force | Out-Null
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($temporarySubKey, $true)
    $key.SetValue("OwnedValue", "installer", [Microsoft.Win32.RegistryValueKind]::String)
    $ownership = [pscustomobject]@{
        Hive = "HKCU"
        SubKey = $temporarySubKey
        ValueName = "OwnedValue"
        JsonPropertyName = $null
        Prior = [pscustomobject]@{ Exists = $true; Value = "prior"; Kind = "String" }
        Written = [pscustomobject]@{ Exists = $true; Value = "installer"; Kind = "String" }
    }

    Restore-OwnedRegistryValues @($ownership)
    Assert-Equal "prior" ([string]$key.GetValue("OwnedValue")) "The actual uninstaller path must restore a matching owned value."
    $key.SetValue("OwnedValue", "changed-after-install", [Microsoft.Win32.RegistryValueKind]::String)
    Restore-OwnedRegistryValues @($ownership)
    Assert-Equal "changed-after-install" ([string]$key.GetValue("OwnedValue")) "The actual uninstaller path must retain a changed value."
    Write-Host "[PASS] Uninstaller registry comparison restores only installer-owned values"
    $passed++

    $installInformation = [pscustomobject]@{
        InstallBrowserExtensions = $false
        LegacyBrowserCleanupRequired = $true
    }
    Assert-True (Test-ShouldRunLegacyBrowserCleanup $installInformation @($ownership)) "A migrated legacy install must retain cleanup ownership even with new registry records."

    $forcelistPath = Join-Path $temporaryRegistryPath "Forcelist"
    New-Item -Path $forcelistPath -Force | Out-Null
    $forcelistKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$temporarySubKey\Forcelist", $true)
    $forcelistKey.SetValue("1", "legacy-chrome;https://clients2.google.com/service/update2/crx")
    $forcelistKey.SetValue("2", "unrelated;https://example.test/update")
    Remove-LegacyForcelistEntries $forcelistPath @("legacy-chrome") "https://clients2.google.com/service/update2/crx"
    Assert-True ($null -eq $forcelistKey.GetValue("1")) "Legacy Chrome policy must be removed during marked cleanup."
    Assert-Equal "unrelated;https://example.test/update" ([string]$forcelistKey.GetValue("2")) "Unrelated Chrome policy must be retained."

    $firefoxPath = Join-Path $temporaryRegistryPath "Firefox"
    New-Item -Path $firefoxPath -Force | Out-Null
    $firefoxKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$temporarySubKey\Firefox", $true)
    $firefoxKey.SetValue(
        "ExtensionSettings",
        '{"legacy-firefox@example.test":{"installation_mode":"force_installed"},"unrelated@example.test":{"installation_mode":"allowed"}}',
        [Microsoft.Win32.RegistryValueKind]::String)
    Remove-FirefoxExtensionProperties $firefoxPath @("legacy-firefox@example.test")
    $remainingFirefoxPolicy = [string]$firefoxKey.GetValue("ExtensionSettings") | ConvertFrom-Json
    Assert-True ($null -eq $remainingFirefoxPolicy.PSObject.Properties["legacy-firefox@example.test"]) "Legacy Firefox policy must be removed during marked cleanup."
    Assert-True ($null -ne $remainingFirefoxPolicy.PSObject.Properties["unrelated@example.test"]) "Unrelated Firefox policy must be retained."
    Write-Host "[PASS] Disabled browser deployment preserves marked legacy cleanup on uninstall"
    $passed++
}
finally {
    foreach ($openedKey in @($key, $forcelistKey, $firefoxKey)) {
        if ($null -ne $openedKey) {
            $openedKey.Dispose()
        }
    }
    Remove-Item -LiteralPath $temporaryRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installer PowerShell regression tests passed: $passed/3."
