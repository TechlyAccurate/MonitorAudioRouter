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
$totalSuites = 7

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
