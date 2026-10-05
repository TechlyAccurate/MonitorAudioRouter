$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$csharpTestProject = Join-Path $PSScriptRoot "MonitorAudioRouter.RegressionTests\MonitorAudioRouter.RegressionTests.csproj"
$browserTestRunner = Join-Path $PSScriptRoot "browser-extension-regression-tests.js"
$installerBuild = Join-Path $repositoryRoot "Build-Installer.ps1"
$testAppData = Join-Path $PSScriptRoot ".appdata"

function Assert-TestEntryPoint([string]$Name, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Name at $Path"
    }
}

Assert-TestEntryPoint "C# regression test project" $csharpTestProject
Assert-TestEntryPoint "browser extension test runner" $browserTestRunner

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

function Assert-NativeSuccess([string]$Action) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Action failed with exit code $LASTEXITCODE"
    }
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

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw "dotnet.exe is required to run the C# regression tests."
}

$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
if ($null -eq $nodeCommand) {
    throw "node.exe is required to run the browser extension regression tests."
}

$passedSuites = 0
$totalSuites = 5

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

& $installerBuild
$passedSuites++
Write-Host "[PASS] Full installer build"

$javaScriptFiles = @(
    $browserTestRunner
    (Join-Path $repositoryRoot "extensions\chromium\background.js")
    (Join-Path $repositoryRoot "extensions\firefox\background.js")
)
foreach ($javaScriptFile in $javaScriptFiles) {
    & $nodeCommand.Source --check $javaScriptFile
    Assert-NativeSuccess "JavaScript syntax check for $javaScriptFile"
}
$passedSuites++
Write-Host "[PASS] JavaScript syntax ($($javaScriptFiles.Count) scripts)"

& $dotnetCommand.Source run --project $csharpTestProject -c Release -p:NuGetAudit=false
Assert-NativeSuccess "C# regression tests"
$passedSuites++

& $nodeCommand.Source $browserTestRunner
Assert-NativeSuccess "Browser extension regression tests"
$passedSuites++

Write-Host "Regression suites passed: $passedSuites/$totalSuites."
