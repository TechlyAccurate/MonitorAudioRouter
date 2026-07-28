param(
    [switch]$UpdateLiveApp
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$liveAppDir = Join-Path $root "app"
$buildRoot = Join-Path $root "build"
$stagedAppDir = Join-Path $buildRoot "app"
$resourcesDir = Join-Path $root "installer-src\Resources"
$payloadZip = Join-Path $resourcesDir "payload.zip"
$distDir = Join-Path $root "dist"
$payloadStage = Join-Path ([IO.Path]::GetTempPath()) ("MonitorAudioRouterPayload-" + [Guid]::NewGuid().ToString("N"))
$publishProperties = @("-p:DebugType=none", "-p:DebugSymbols=false")
$releaseVersion = "0.1.8"

function Assert-NativeSuccess([string]$Action) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Action failed with exit code $LASTEXITCODE"
    }
}

function Stop-RouterProcesses {
    $trayExe = Join-Path $liveAppDir "MonitorAudioRouter.exe"
    if (Test-Path -LiteralPath $trayExe) {
        try {
            $clearProcess = Start-Process -FilePath $trayExe -ArgumentList "--clear-managed-routes" -NoNewWindow -PassThru -Wait
        }
        catch {
            # Older builds may not support the cleanup command; file replacement can still continue.
        }
    }

    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $processes = Get-Process MonitorAudioRouter,MonitorAudioRouterNativeHost -ErrorAction SilentlyContinue
        if ($null -eq $processes) {
            return
        }

        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
}

function Remove-GeneratedDirectory([string]$Path) {
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $resolvedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove generated directory outside project root: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($relativePath in @(
    "src\bin",
    "src\obj",
    "native-host-src\bin",
    "native-host-src\obj",
    "installer-src\bin",
    "installer-src\obj"
)) {
    Remove-GeneratedDirectory (Join-Path $root $relativePath)
}

Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stagedAppDir | Out-Null

dotnet publish (Join-Path $root "src\MonitorAudioRouter.csproj") -c Release -r win-x64 --self-contained true -o $stagedAppDir @publishProperties
Assert-NativeSuccess "Tray publish"

dotnet publish (Join-Path $root "native-host-src\MonitorAudioRouterNativeHost.csproj") -c Release -r win-x64 --self-contained true -o $stagedAppDir @publishProperties
Assert-NativeSuccess "Native host publish"
Get-ChildItem -LiteralPath $stagedAppDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

& (Join-Path $root "Build-Store-Packages.ps1")

New-Item -ItemType Directory -Force $resourcesDir | Out-Null
Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $distDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $payloadStage | Out-Null

try {
    Copy-Item -LiteralPath $stagedAppDir -Destination (Join-Path $payloadStage "app") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root "extensions") -Destination (Join-Path $payloadStage "extensions") -Recurse -Force

    foreach ($fileName in @("README.md", "BrowserSetup.html")) {
        $path = Join-Path $root $fileName
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $payloadStage $fileName) -Force
        }
    }

    Copy-Item -LiteralPath (Join-Path $root "installer-src\Uninstall-MonitorAudioRouter.ps1") -Destination (Join-Path $payloadStage "Uninstall-MonitorAudioRouter.ps1") -Force

    Compress-Archive -Path (Join-Path $payloadStage "*") -DestinationPath $payloadZip -Force
}
finally {
    Remove-Item -LiteralPath $payloadStage -Recurse -Force -ErrorAction SilentlyContinue
}

dotnet publish (Join-Path $root "installer-src\MonitorAudioRouterSetup.csproj") -c Release -r win-x64 --self-contained true -o $distDir @publishProperties
Assert-NativeSuccess "Setup publish"
Get-ChildItem -LiteralPath $distDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$setupExe = Join-Path $distDir "MonitorAudioRouterSetup.exe"
$chromeReleaseZip = Join-Path $distDir "chrome-monitor-audio-router.zip"
$firefoxReleaseZip = Join-Path $distDir "firefox-monitor-audio-router.zip"
$firefoxReleaseXpi = Join-Path $distDir "firefox-monitor-audio-router.xpi"
$releaseZip = Join-Path $distDir ("MonitorAudioRouter-" + $releaseVersion + "-github-release.zip")
$checksumsPath = Join-Path $distDir "SHA256SUMS.txt"
$releaseStage = Join-Path ([IO.Path]::GetTempPath()) ("MonitorAudioRouterRelease-" + [Guid]::NewGuid().ToString("N"))

Copy-Item -LiteralPath (Join-Path $root "packages\browser-store\chrome\monitor-audio-router.zip") -Destination $chromeReleaseZip -Force
Copy-Item -LiteralPath (Join-Path $root "packages\browser-store\firefox\monitor-audio-router.zip") -Destination $firefoxReleaseZip -Force
Copy-Item -LiteralPath (Join-Path $root "packages\browser-store\firefox\monitor-audio-router.xpi") -Destination $firefoxReleaseXpi -Force

Remove-Item -LiteralPath $releaseZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $checksumsPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $releaseStage | Out-Null

try {
    foreach ($asset in @(
        @{ Source = $setupExe; Name = "MonitorAudioRouterSetup.exe" },
        @{ Source = $chromeReleaseZip; Name = "chrome-monitor-audio-router.zip" },
        @{ Source = $firefoxReleaseZip; Name = "firefox-monitor-audio-router.zip" },
        @{ Source = $firefoxReleaseXpi; Name = "firefox-monitor-audio-router.xpi" },
        @{ Source = (Join-Path $root "README.md"); Name = "README.md" }
    )) {
        if (Test-Path -LiteralPath $asset.Source) {
            Copy-Item -LiteralPath $asset.Source -Destination (Join-Path $releaseStage $asset.Name) -Force
        }
    }

    Compress-Archive -Path (Join-Path $releaseStage "*") -DestinationPath $releaseZip -Force
}
finally {
    Remove-Item -LiteralPath $releaseStage -Recurse -Force -ErrorAction SilentlyContinue
}

$hashLines = foreach ($asset in @(
    @{ Source = $setupExe; Name = "MonitorAudioRouterSetup.exe" },
    @{ Source = $chromeReleaseZip; Name = "chrome-monitor-audio-router.zip" },
    @{ Source = $firefoxReleaseZip; Name = "firefox-monitor-audio-router.zip" },
    @{ Source = $firefoxReleaseXpi; Name = "firefox-monitor-audio-router.xpi" },
    @{ Source = $releaseZip; Name = (Split-Path -Leaf $releaseZip) }
)) {
    if (Test-Path -LiteralPath $asset.Source) {
        $hash = Get-FileHash -LiteralPath $asset.Source -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $asset.Name
    }
}

$hashLines | Set-Content -LiteralPath $checksumsPath -Encoding ascii

if ($UpdateLiveApp) {
    Stop-RouterProcesses
    Copy-Item -Path (Join-Path $stagedAppDir "*") -Destination $liveAppDir -Recurse -Force
}

Write-Host "Installer created in $distDir"
