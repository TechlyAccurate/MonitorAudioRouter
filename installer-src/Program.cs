using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Win32;
using MonitorAudioRouter.Setup;
using MonitorAudioRouter.UpdateSupport;

const string AppName = "Monitor Audio Router";
const string AppId = "MonitorAudioRouter";
const string AppVersion = "0.1.16";
const string HostName = "com.monitoraudiorouter.router";
const string ChromeWebStoreListingUrl = "https://chromewebstore.google.com/detail/jnjminkakfohjeffdpeamngcnfneckog";
const string FirefoxAddOnsListingUrl = "https://addons.mozilla.org/en-US/firefox/addon/monitor-audio-router-bridge/";
const string SetupAssetName = "MonitorAudioRouterSetup.exe";
const string ChecksumsAssetName = "SHA256SUMS.txt";
const string RunValueName = "Monitor Audio Router";

WriteInstallerLog($"Setup started. Version={AppVersion}; ProcessId={Environment.ProcessId}; Arguments={FormatArgumentsForLog(args)}");
var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    AppName);
var previousInstallInfo = ReadInstallInfo(installDir);
var commandLineOptions = InstallerOptionResolver.ResolvePrimaryOptions(
    args,
    installInfoJson: null,
    legacyBrowserExtensionsInstalled: false,
    legacyAutostartInstalled: false);
var legacyBrowserExtensionsInstalled = previousInstallInfo is not null &&
    HasInstalledBrowserPolicy(previousInstallInfo.ResolveOptions(ToInstalledOptions(commandLineOptions)));
var legacyAutostartInstalled = previousInstallInfo is not null && HasInstalledAutostart();
var options = InstallerOptionResolver.ResolvePrimaryOptions(
    args,
    previousInstallInfo?.Serialize(),
    legacyBrowserExtensionsInstalled,
    legacyAutostartInstalled);
options = ApplyInteractiveOptions(options);
if (options.Canceled)
{
    Environment.ExitCode = 1223;
    return;
}

if (options.UpdateToLatestDuringInstall && TryLaunchNewerInstaller(options, previousInstallInfo))
{
    WriteInstallerLog("Setup handed off to a newer published installer.");
    return;
}

var priorInstallationWasInterrupted = false;
try
{
    var tempDir = Path.Combine(Path.GetTempPath(), AppId + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    var replacementRoot = Path.GetDirectoryName(installDir)
        ?? throw new InvalidOperationException("The installation directory does not have a parent directory.");
    var replacementId = Guid.NewGuid().ToString("N");
    var stagingContainer = Path.Combine(replacementRoot, $".{AppId}-stage-{replacementId}");
    var stagedInstallDir = Path.Combine(stagingContainer, AppName);
    var backupDir = Path.Combine(replacementRoot, $".{AppId}-backup-{replacementId}");
    try
    {
        WriteInstallerLog($"Extracting payload to {tempDir}.");
        ExtractPayload(tempDir);
        WriteInstallerLog($"Staging and validating replacement at {stagedInstallDir}.");
        StageInstallation(tempDir, installDir, stagedInstallDir);
        ValidateStagedInstallation(stagedInstallDir);
        using var nativeMessagingManifestGate = NativeMessagingManifestGate.Block(
            GetNativeMessagingManifestPaths(installDir)
                .Concat(GetNativeMessagingManifestPaths(stagedInstallDir)),
            exception => WriteInstallerLog($"Native messaging manifest restore failed: {exception}"));
        WriteInstallerLog($"Stopping existing app processes before installing to {installDir}.");
        StopExistingApp(installDir, () => priorInstallationWasInterrupted = true);
        WriteInstallerLog("Replacing application files transactionally.");
        var browserExtensionDeployment = InstallerOrchestration.Execute(
            stagedInstallDir,
            installDir,
            backupDir,
            stateTransaction =>
            {
                var registryOwnership = new RegistryOwnershipRecorder(previousInstallInfo?.RegistryValues);
                WriteInstallerLog("Registering native messaging hosts.");
                RegisterNativeMessagingHosts(installDir, registryOwnership, stateTransaction);
                WriteInstallerLog("Registering browser extension deployment policies.");
                var deployment = RegisterBrowserExtensionPolicies(options, registryOwnership, stateTransaction);
                WriteInstallerLog("Applying startup and shortcut settings.");
                SetStartup(installDir, options.Autostart, registryOwnership, stateTransaction);
                InstallStartMenuShortcut(installDir, stateTransaction);
                WriteUserAutostartSetting(options.Autostart, stateTransaction);
                RegisterUninstaller(installDir, registryOwnership, stateTransaction);
                WriteInstallInfo(
                    installDir,
                    options,
                    registryOwnership.Values,
                    previousInstallInfo?.RequiresLegacyBrowserCleanup == true,
                    previousInstallInfo);
                return deployment;
            },
            beforeCommit: () =>
            {
                WriteInstallerLog("Writing native messaging manifests.");
                WriteNativeMessagingManifests(installDir, options);
                nativeMessagingManifestGate.Commit();
            });
        WriteInstallerLog("Install registry state written.");

        if (options.Launch)
        {
            WriteInstallerLog("Launching installed tray app.");
            StartAppForUser(Path.Combine(installDir, "MonitorAudioRouter.exe"));
        }

        var openedExtensionPages = OpenBrowserExtensionPagesIfNeeded(options, browserExtensionDeployment);
        if (options.OpenBrowserSetup && !openedExtensionPages)
        {
            StartProcess(Path.Combine(installDir, "BrowserSetup.html"));
        }

        Console.WriteLine("Monitor Audio Router installed.");
        WriteInstallerLog("Setup completed successfully.");
    }
    finally
    {
        TryDeleteDirectory(tempDir);
        TryDeleteDirectory(stagingContainer);
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine("Install failed:");
    Console.Error.WriteLine(exception);
    WriteInstallerLog($"Setup failed: {exception}");
    InstallerFailureRecovery.TryRestartStoppedApp(
        priorInstallationWasInterrupted,
        Path.Combine(installDir, "MonitorAudioRouter.exe"),
        File.Exists,
        StartAppForUser,
        WriteInstallerLog);
    if (!HasSwitch(args, "/quiet"))
    {
        MessageBox.Show(
            "Monitor Audio Router could not be installed.\n\n" + exception.Message + "\n\nSee installer.log in the app data folder for details.",
            "Monitor Audio Router setup failed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    Environment.ExitCode = 1;
}

static void ExtractPayload(string tempDir)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
        ?? throw new InvalidOperationException("Embedded payload.zip was not found.");
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    archive.ExtractToDirectory(tempDir, overwriteFiles: true);
}

static void StopExistingApp(string installDir, Action trayAppStopped)
{
    StopInstalledProcesses(installDir, trayAppStopped);
    ClearManagedRoutes(installDir);
    InstallerProcessQuiescence.WaitUntilStable(
        stopInstalledProcesses: () => StopInstalledProcesses(installDir, trayAppStopped),
        getCurrentTime: () => DateTimeOffset.UtcNow,
        wait: Thread.Sleep,
        quietPeriod: TimeSpan.FromMilliseconds(750),
        timeout: TimeSpan.FromSeconds(10),
        pollInterval: TimeSpan.FromMilliseconds(100));
}

static int StopInstalledProcesses(string installDir, Action trayAppStopped)
{
    var trayAppPath = Path.Combine(installDir, "MonitorAudioRouter.exe");
    var expectedPaths = new[]
    {
        trayAppPath,
        Path.Combine(installDir, "MonitorAudioRouterNativeHost.exe")
    };
    var installedProcesses = new List<(Process Process, string ExecutablePath, bool IsTrayApp)>();
    var candidates = InstallDecisions.EnumerateInstalledProcessCandidates(Process.GetProcessesByName);
    foreach (var process in candidates)
    {
        try
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            string? executablePath = null;
            var processIsActive = InstallerProcessRace.TryExecuteWhileActive(
                hasExited: () => process.HasExited,
                operation: () => executablePath = process.MainModule?.FileName);
            if (!processIsActive)
            {
                process.Dispose();
                continue;
            }

            if (executablePath is null ||
                !expectedPaths.Any(expected => InstallDecisions.IsExactExecutablePath(executablePath, expected)))
            {
                process.Dispose();
                continue;
            }

            installedProcesses.Add((
                process,
                executablePath,
                InstallDecisions.IsExactExecutablePath(executablePath, trayAppPath)));
        }
        catch (Exception exception)
        {
            var processId = process.Id;
            process.Dispose();
            foreach (var installedProcess in installedProcesses)
            {
                installedProcess.Process.Dispose();
            }

            throw new InvalidOperationException(
                $"Process PID {processId} has an installed executable name, but its path could not be verified.",
                exception);
        }
    }

    try
    {
        foreach (var installedProcess in installedProcesses)
        {
            WriteInstallerLog(
                $"Stopping installed executable {installedProcess.ExecutablePath} PID {installedProcess.Process.Id}.");
            var processWasStopped = InstallerProcessRace.TryExecuteWhileActive(
                hasExited: () => installedProcess.Process.HasExited,
                operation: () => installedProcess.Process.Kill(entireProcessTree: false));
            if (processWasStopped)
            {
                if (installedProcess.IsTrayApp)
                {
                    trayAppStopped();
                }
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        foreach (var installedProcess in installedProcesses)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                installedProcess.Process.WaitForExit(
                    (int)Math.Min(remaining.TotalMilliseconds, int.MaxValue));
            }

            if (!installedProcess.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Installed process PID {installedProcess.Process.Id} did not exit within the shutdown timeout.");
            }
        }

        // Any verified process activity resets the quiet window, even if it exits before Kill.
        return installedProcesses.Count;
    }
    finally
    {
        foreach (var process in installedProcesses)
        {
            process.Process.Dispose();
        }
    }
}

static void ClearManagedRoutes(string installDir)
{
    var existingExe = Path.Combine(installDir, "MonitorAudioRouter.exe");
    if (!File.Exists(existingExe))
    {
        return;
    }

    WriteInstallerLog("Clearing managed audio routes after installed processes stopped.");
    using var process = Process.Start(new ProcessStartInfo(existingExe, "--clear-managed-routes")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("The installed route cleanup helper did not start.");
    if (!process.WaitForExit(5000))
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch
        {
            // Report the bounded wait failure even if terminating the helper also fails.
        }

        throw new TimeoutException("The installed route cleanup helper did not exit within five seconds.");
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"The installed route cleanup helper exited with code {process.ExitCode}.");
    }
}

static void StageInstallation(string payloadRoot, string installDir, string stagedInstallDir)
{
    Directory.CreateDirectory(stagedInstallDir);

    CopyDirectory(Path.Combine(payloadRoot, "app"), stagedInstallDir);
    CopyDirectory(Path.Combine(payloadRoot, "extensions"), Path.Combine(stagedInstallDir, "extensions"));
    CopyDirectory(Path.Combine(payloadRoot, "packages"), Path.Combine(stagedInstallDir, "packages"));

    CopyIfExists(Path.Combine(payloadRoot, "README.md"), Path.Combine(stagedInstallDir, "README.md"));
    CopyIfExists(Path.Combine(payloadRoot, "PUBLISHING.md"), Path.Combine(stagedInstallDir, "PUBLISHING.md"));
    CopyIfExists(Path.Combine(payloadRoot, "BrowserSetup.html"), Path.Combine(stagedInstallDir, "BrowserSetup.html"));
    CopyIfExists(
        Path.Combine(payloadRoot, "Uninstall-MonitorAudioRouter.ps1"),
        Path.Combine(stagedInstallDir, "Uninstall-MonitorAudioRouter.ps1"));

    CopyIfExists(Path.Combine(installDir, "config.json"), Path.Combine(stagedInstallDir, "config.json"));
    CopyIfExists(Path.Combine(installDir, "install-info.json"), Path.Combine(stagedInstallDir, "install-info.json"));

    var configPath = Path.Combine(stagedInstallDir, "config.json");
    var defaultConfigPath = Path.Combine(stagedInstallDir, "config.default.json");
    if (!File.Exists(configPath) && File.Exists(defaultConfigPath))
    {
        File.Copy(defaultConfigPath, configPath);
    }
}

static void ValidateStagedInstallation(string stagedInstallDir)
{
    if (!InstallDecisions.IsSafeUpdateDirectory(File.GetAttributes(stagedInstallDir)))
    {
        throw new InvalidOperationException("The staged installation root is a reparse point or is not a directory.");
    }

    var requiredFiles = new[]
    {
        "MonitorAudioRouter.exe",
        "MonitorAudioRouterNativeHost.exe",
        "Uninstall-MonitorAudioRouter.ps1"
    };
    foreach (var relativePath in requiredFiles)
    {
        var requiredPath = Path.Combine(stagedInstallDir, relativePath);
        if (!InstallDecisions.IsPathWithinRoot(stagedInstallDir, requiredPath) || !File.Exists(requiredPath))
        {
            throw new InvalidOperationException($"The staged payload is missing required file {relativePath}.");
        }
    }

    foreach (var path in Directory.EnumerateFileSystemEntries(stagedInstallDir, "*", SearchOption.AllDirectories))
    {
        if (!InstallDecisions.IsPathWithinRoot(stagedInstallDir, path))
        {
            throw new InvalidOperationException($"The staged payload escaped its root: {path}");
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"The staged payload contains a reparse point: {path}");
        }
    }
}

static void RequireChildPath(string rootPath, string candidatePath, string description)
{
    if (!InstallDecisions.IsPathWithinRoot(rootPath, candidatePath))
    {
        throw new InvalidOperationException($"The {description} is outside its bounded root: {candidatePath}");
    }

}

static void WriteNativeMessagingManifests(string installDir, InstallerOptions options)
{
    var nativeHostExe = Path.Combine(installDir, "MonitorAudioRouterNativeHost.exe");
    var hostDir = Path.Combine(installDir, "native-hosts");
    Directory.CreateDirectory(hostDir);

    var chromiumAllowedOrigins = new List<string>();
    AddChromiumOrigin(chromiumAllowedOrigins, options.ChromeExtensionId);
    AddChromiumOrigin(chromiumAllowedOrigins, options.EdgeExtensionId);

    var chromiumManifest = new
    {
        name = HostName,
        description = "Monitor Audio Router browser bridge",
        path = nativeHostExe,
        type = "stdio",
        allowed_origins = chromiumAllowedOrigins.ToArray()
    };

    var firefoxManifest = new
    {
        name = HostName,
        description = "Monitor Audio Router browser bridge",
        path = nativeHostExe,
        type = "stdio",
        allowed_extensions = HasPublishedValue(options.FirefoxExtensionId)
            ? new[] { options.FirefoxExtensionId }
            : Array.Empty<string>()
    };

    var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(
        Path.Combine(hostDir, "chromium-com.monitoraudiorouter.router.json"),
        JsonSerializer.Serialize(chromiumManifest, serializerOptions));
    File.WriteAllText(
        Path.Combine(hostDir, "firefox-com.monitoraudiorouter.router.json"),
        JsonSerializer.Serialize(firefoxManifest, serializerOptions));
}

static IReadOnlyList<string> GetNativeMessagingManifestPaths(string installDir)
{
    var hostDirectory = Path.Combine(installDir, "native-hosts");
    return
    [
        Path.Combine(hostDirectory, "chromium-com.monitoraudiorouter.router.json"),
        Path.Combine(hostDirectory, "firefox-com.monitoraudiorouter.router.json")
    ];
}

static void RegisterNativeMessagingHosts(
    string installDir,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var chromiumManifest = Path.Combine(installDir, "native-hosts", "chromium-com.monitoraudiorouter.router.json");
    var firefoxManifest = Path.Combine(installDir, "native-hosts", "firefox-com.monitoraudiorouter.router.json");
    var softwareRoots = Environment.Is64BitOperatingSystem
        ? new[] { "Software", @"Software\WOW6432Node" }
        : new[] { "Software" };

    foreach (var registryLocation in new[]
    {
        (Hive: Registry.LocalMachine, Name: "HKLM"),
        (Hive: Registry.CurrentUser, Name: "HKCU")
    })
    {
        foreach (var softwareRoot in softwareRoots)
        {
            WriteOwnedRegistryValue(
                registryLocation.Hive,
                registryLocation.Name,
                $@"{softwareRoot}\Google\Chrome\NativeMessagingHosts\{HostName}",
                string.Empty,
                chromiumManifest,
                registryOwnership,
                stateTransaction);
            WriteOwnedRegistryValue(
                registryLocation.Hive,
                registryLocation.Name,
                $@"{softwareRoot}\Chromium\NativeMessagingHosts\{HostName}",
                string.Empty,
                chromiumManifest,
                registryOwnership,
                stateTransaction);
            WriteOwnedRegistryValue(
                registryLocation.Hive,
                registryLocation.Name,
                $@"{softwareRoot}\Microsoft\Edge\NativeMessagingHosts\{HostName}",
                string.Empty,
                chromiumManifest,
                registryOwnership,
                stateTransaction);
            WriteOwnedRegistryValue(
                registryLocation.Hive,
                registryLocation.Name,
                $@"{softwareRoot}\Mozilla\NativeMessagingHosts\{HostName}",
                string.Empty,
                firefoxManifest,
                registryOwnership,
                stateTransaction);
        }
    }
}

static void AddChromiumOrigin(List<string> origins, string extensionId)
{
    if (!HasPublishedValue(extensionId))
    {
        return;
    }

    var origin = $"chrome-extension://{extensionId}/";
    if (!origins.Any(existing => existing.Equals(origin, StringComparison.OrdinalIgnoreCase)))
    {
        origins.Add(origin);
    }
}

static BrowserExtensionDeploymentResult RegisterBrowserExtensionPolicies(
    InstallerOptions options,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var result = new BrowserExtensionDeploymentResult();
    if (!options.InstallBrowserExtensions)
    {
        result.SkippedByUser = true;
        Console.WriteLine("Browser extension policy install skipped by installer option.");
        return result;
    }

    var installedAny = false;
    if (HasPublishedValue(options.ChromeExtensionId))
    {
        installedAny |= TryRegisterPolicy(
            "Chrome extension policy",
            () => AddExtensionForcelistEntry(
                Registry.LocalMachine,
                "HKLM",
                @"Software\Policies\Google\Chrome\ExtensionInstallForcelist",
                options.ChromeExtensionId,
                options.ChromeUpdateUrl,
                registryOwnership,
                stateTransaction),
            () => result.ChromePolicyInstalled = true,
            () => result.ChromePolicyFailed = true);
        installedAny |= TryRegisterPolicy(
            "Chromium extension policy",
            () => AddExtensionForcelistEntry(
                Registry.LocalMachine,
                "HKLM",
                @"Software\Policies\Chromium\ExtensionInstallForcelist",
                options.ChromeExtensionId,
                options.ChromeUpdateUrl,
                registryOwnership,
                stateTransaction),
            () => result.ChromiumPolicyInstalled = true,
            () => result.ChromiumPolicyFailed = true);
    }

    if (HasPublishedValue(options.EdgeExtensionId))
    {
        installedAny |= TryRegisterPolicy(
            "Edge extension policy",
            () => AddExtensionForcelistEntry(
                Registry.LocalMachine,
                "HKLM",
                @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
                options.EdgeExtensionId,
                options.EdgeUpdateUrl,
                registryOwnership,
                stateTransaction),
            () => result.EdgePolicyInstalled = true,
            () => result.EdgePolicyFailed = true);
    }

    if (HasPublishedValue(options.FirefoxExtensionId) && HasPublishedValue(options.FirefoxInstallUrl))
    {
        installedAny |= TryRegisterPolicy(
            "Firefox extension policy",
            () => SetFirefoxExtensionPolicy(
                options.FirefoxExtensionId,
                options.FirefoxInstallUrl,
                options.EnablePrivateBrowsing,
                registryOwnership,
                stateTransaction),
            () => result.FirefoxPolicyInstalled = true,
            () => result.FirefoxPolicyFailed = true);
    }

    if (!installedAny)
    {
        Console.WriteLine("Browser extension policy install skipped because published extension IDs/URLs are not configured in this build.");
    }

    return result;
}

static bool TryRegisterPolicy(string name, Action action, Action onSuccess, Action onFailure)
{
    try
    {
        action();
        onSuccess();
        return true;
    }
    catch (Exception ex)
    {
        onFailure();
        Console.Error.WriteLine($"{name} failed: {ex.Message}");
        return false;
    }
}

static void AddExtensionForcelistEntry(
    RegistryKey hive,
    string hiveName,
    string subKey,
    string extensionId,
    string updateUrl,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var entry = $"{extensionId};{updateUrl}";
    using var key = hive.CreateSubKey(subKey, writable: true);
    if (key is null)
    {
        return;
    }

    foreach (var valueName in key.GetValueNames())
    {
        var existing = key.GetValue(valueName)?.ToString();
        if (existing is not null && existing.StartsWith(extensionId + ";", StringComparison.OrdinalIgnoreCase))
        {
            WriteOwnedRegistryValue(hive, hiveName, subKey, valueName, entry, registryOwnership, stateTransaction);
            return;
        }
    }

    var index = 1;
    while (key.GetValue(index.ToString()) is not null)
    {
        index++;
    }

    WriteOwnedRegistryValue(hive, hiveName, subKey, index.ToString(), entry, registryOwnership, stateTransaction);
}

static void SetFirefoxExtensionPolicy(
    string extensionId,
    string installUrl,
    bool enablePrivateBrowsing,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    const string subKey = @"Software\Policies\Mozilla\Firefox";
    const string valueName = "ExtensionSettings";
    var keyExisted = RegistryKeyExists(Registry.LocalMachine, subKey);
    var currentValue = ReadRegistryValueAtPath(Registry.LocalMachine, subKey, valueName);
    EnsureStringRegistryValue(currentValue, "HKLM", subKey, valueName);
    var extensionPolicy = new JsonObject
    {
        ["installation_mode"] = "force_installed",
        ["install_url"] = installUrl,
        ["updates_disabled"] = false
    };

    if (enablePrivateBrowsing)
    {
        extensionPolicy["private_browsing"] = true;
    }

    var mutation = FirefoxExtensionSettingsMutation.Create(
        currentValue.Value,
        extensionId,
        extensionPolicy);
    stateTransaction.Apply(
        InstallerMutationKind.Registry,
        () =>
        {
            using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Registry key could not be created: HKLM\\{subKey}");
            key.SetValue(
                valueName,
                mutation.WrittenValue,
                RegistryValueKind.String);
            registryOwnership.Record(
                "HKLM",
                subKey,
                valueName,
                mutation.Prior,
                mutation.Written,
                extensionId);
        },
        () => RestoreFirefoxExtensionSettingsMutation(
            Registry.LocalMachine,
            subKey,
            valueName,
            mutation,
            keyExisted));
}

static JsonObject ParseJsonObject(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return new JsonObject();
    }

    try
    {
        return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }
    catch
    {
        return new JsonObject();
    }
}

static void SetStartup(
    string installDir,
    bool enabled,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    const string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    if (enabled)
    {
        WriteOwnedRegistryValue(
            Registry.CurrentUser,
            "HKCU",
            subKey,
            RunValueName,
            Quote(Path.Combine(installDir, "MonitorAudioRouter.exe")),
            registryOwnership,
            stateTransaction);
    }
    else
    {
        DeleteOwnedRegistryValue(
            Registry.CurrentUser,
            "HKCU",
            subKey,
            RunValueName,
            registryOwnership,
            stateTransaction);
    }
}

static void WriteUserAutostartSetting(bool enabled, InstallerStateTransaction stateTransaction)
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var configPath = Path.Combine(localAppData, AppName, "config.json");
    try
    {
        InstallerExternalFileMutation.Apply(
            stateTransaction,
            InstallerMutationKind.UserConfiguration,
            configPath,
            () =>
            {
                var settings = ParseJsonObject(File.Exists(configPath) ? File.ReadAllText(configPath) : null);
                settings["AutostartEnabled"] = enabled;
                File.WriteAllText(configPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            });
    }
    catch (AggregateException)
    {
        throw;
    }
    catch (Exception exception)
    {
        WriteInstallerLog($"User autostart configuration sync failed: {exception.Message}");
        // The tray app can still manage autostart if the config sync fails.
    }
}

static void RegisterUninstaller(
    string installDir,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var subKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}";
    var uninstallCommand = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(Path.Combine(installDir, "Uninstall-MonitorAudioRouter.ps1"))}";
    var values = new Dictionary<string, string>
    {
        ["DisplayName"] = AppName,
        ["DisplayVersion"] = AppVersion,
        ["Publisher"] = "Local",
        ["InstallLocation"] = installDir,
        ["DisplayIcon"] = Path.Combine(installDir, "MonitorAudioRouter.exe"),
        ["UninstallString"] = uninstallCommand,
        ["QuietUninstallString"] = uninstallCommand + " -Quiet"
    };
    foreach (var value in values)
    {
        WriteOwnedRegistryValue(
            Registry.LocalMachine,
            "HKLM",
            subKey,
            value.Key,
            value.Value,
            registryOwnership,
            stateTransaction);
    }
}

static void InstallStartMenuShortcut(string installDir, InstallerStateTransaction stateTransaction)
{
    var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    var shortcutPath = Path.Combine(programsDir, $"{AppName}.lnk");
    try
    {
        InstallerExternalFileMutation.Apply(
            stateTransaction,
            InstallerMutationKind.StartMenuShortcut,
            shortcutPath,
            () =>
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    return;
                }

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = Path.Combine(installDir, "MonitorAudioRouter.exe");
                shortcut.WorkingDirectory = installDir;
                shortcut.IconLocation = Path.Combine(installDir, "MonitorAudioRouter.ico") + ",0";
                shortcut.Description = AppName;
                shortcut.Save();
            });
    }
    catch (AggregateException)
    {
        throw;
    }
    catch (Exception exception)
    {
        WriteInstallerLog($"Start Menu shortcut creation failed: {exception.Message}");
        // Shortcut creation should not block the core install.
    }
}

static void WriteInstallInfo(
    string installDir,
    InstallerOptions options,
    IReadOnlyList<RegistryValueOwnership> registryOwnership,
    bool legacyBrowserCleanupRequired,
    InstallInfo? previousInstallInfo)
{
    var installInfo = InstallInfo.FromOptions(
        ToInstalledOptions(options),
        registryOwnership,
        legacyBrowserCleanupRequired,
        previousInstallInfo);
    File.WriteAllText(
        Path.Combine(installDir, "install-info.json"),
        installInfo.Serialize());
}

static InstallInfo? ReadInstallInfo(string installDir)
{
    var installInfoPath = Path.Combine(installDir, "install-info.json");
    if (!File.Exists(installInfoPath))
    {
        return null;
    }

    try
    {
        return InstallInfo.Deserialize(File.ReadAllText(installInfoPath));
    }
    catch (Exception exception)
    {
        WriteInstallerLog($"Existing install information could not be read: {exception.Message}");
        return null;
    }
}

static InstalledOptions ToInstalledOptions(InstallerOptions options) =>
    new(
        options.InstallBrowserExtensions,
        options.Autostart,
        options.EnablePrivateBrowsing,
        options.ChromeExtensionId,
        options.ChromeUpdateUrl,
        options.EdgeExtensionId,
        options.EdgeUpdateUrl,
        options.FirefoxExtensionId,
        options.FirefoxInstallUrl);

static bool HasInstalledAutostart()
{
    var installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        AppName);
    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
    var current = key?.GetValue(RunValueName)?.ToString();
    return string.Equals(
        current,
        Quote(Path.Combine(installDir, "MonitorAudioRouter.exe")),
        StringComparison.OrdinalIgnoreCase);
}

static bool HasInstalledBrowserPolicy(InstalledOptions options)
{
    return HasExtensionForcelistEntry(
               Registry.LocalMachine,
               @"Software\Policies\Google\Chrome\ExtensionInstallForcelist",
               options.ChromeExtensionId,
               options.ChromeUpdateUrl) ||
           HasExtensionForcelistEntry(
               Registry.LocalMachine,
               @"Software\Policies\Chromium\ExtensionInstallForcelist",
               options.ChromeExtensionId,
               options.ChromeUpdateUrl) ||
           HasExtensionForcelistEntry(
               Registry.LocalMachine,
               @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
               options.EdgeExtensionId,
               options.EdgeUpdateUrl) ||
           HasFirefoxExtensionPolicy(options.FirefoxExtensionId, options.FirefoxInstallUrl);
}

static bool HasExtensionForcelistEntry(
    RegistryKey hive,
    string subKey,
    string extensionId,
    string updateUrl)
{
    if (!HasPublishedValue(extensionId) || !HasPublishedValue(updateUrl))
    {
        return false;
    }

    using var key = hive.OpenSubKey(subKey);
    var expected = $"{extensionId};{updateUrl}";
    return key?.GetValueNames().Any(valueName => string.Equals(
        key.GetValue(valueName)?.ToString(),
        expected,
        StringComparison.OrdinalIgnoreCase)) == true;
}

static bool HasFirefoxExtensionPolicy(string extensionId, string installUrl)
{
    if (!HasPublishedValue(extensionId) || !HasPublishedValue(installUrl))
    {
        return false;
    }

    using var key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Mozilla\Firefox");
    var settings = ParseJsonObject(key?.GetValue("ExtensionSettings")?.ToString());
    return settings[extensionId] is JsonObject extensionPolicy &&
           string.Equals(
               extensionPolicy["install_url"]?.GetValue<string>(),
               installUrl,
               StringComparison.OrdinalIgnoreCase);
}

static void WriteOwnedRegistryValue(
    RegistryKey hive,
    string hiveName,
    string subKey,
    string valueName,
    string value,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var keyExisted = RegistryKeyExists(hive, subKey);
    var current = ReadRegistryValueAtPath(hive, subKey, valueName);
    EnsureStringRegistryValue(current, hiveName, subKey, valueName);
    RegistryValueSnapshot? written = null;
    stateTransaction.Apply(
        InstallerMutationKind.Registry,
        () =>
        {
            using var key = hive.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Registry key could not be created: {hiveName}\\{subKey}");
            key.SetValue(valueName, value, RegistryValueKind.String);
            written = ReadRegistryValue(key, valueName);
            registryOwnership.Record(hiveName, subKey, valueName, current, written, jsonPropertyName: null);
        },
        () => RestoreRegistryMutation(hive, subKey, valueName, current, written, keyExisted));
}

static void DeleteOwnedRegistryValue(
    RegistryKey hive,
    string hiveName,
    string subKey,
    string valueName,
    RegistryOwnershipRecorder registryOwnership,
    InstallerStateTransaction stateTransaction)
{
    var keyExisted = RegistryKeyExists(hive, subKey);
    var current = ReadRegistryValueAtPath(hive, subKey, valueName);
    EnsureStringRegistryValue(current, hiveName, subKey, valueName);
    RegistryValueSnapshot? written = null;
    stateTransaction.Apply(
        InstallerMutationKind.Registry,
        () =>
        {
            using var key = hive.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Registry key could not be created: {hiveName}\\{subKey}");
            key.DeleteValue(valueName, throwOnMissingValue: false);
            written = ReadRegistryValue(key, valueName);
            registryOwnership.Record(hiveName, subKey, valueName, current, written, jsonPropertyName: null);
        },
        () => RestoreRegistryMutation(hive, subKey, valueName, current, written, keyExisted));
}

static bool RegistryKeyExists(RegistryKey hive, string subKey)
{
    using var key = hive.OpenSubKey(subKey);
    return key is not null;
}

static RegistryValueSnapshot ReadRegistryValueAtPath(RegistryKey hive, string subKey, string valueName)
{
    using var key = hive.OpenSubKey(subKey);
    return key is null
        ? new RegistryValueSnapshot(false, null, null)
        : ReadRegistryValue(key, valueName);
}

static void RestoreRegistryMutation(
    RegistryKey hive,
    string subKey,
    string valueName,
    RegistryValueSnapshot prior,
    RegistryValueSnapshot? written,
    bool keyExisted)
{
    if (written is not null)
    {
        var ownership = new RegistryValueOwnership(
            string.Empty,
            subKey,
            valueName,
            prior,
            written,
            JsonPropertyName: null);
        var current = ReadRegistryValueAtPath(hive, subKey, valueName);
        var decision = InstallDecisions.DecideRegistryRemoval(ownership, current);
        if (decision.Action == RegistryRemovalAction.RetainCurrent)
        {
            return;
        }
    }

    RestoreRegistrySnapshot(hive, subKey, valueName, prior);
    RemoveEmptyRegistryKeyCreatedByInstaller(hive, subKey, keyExisted);
}

static void RestoreFirefoxExtensionSettingsMutation(
    RegistryKey hive,
    string subKey,
    string valueName,
    FirefoxExtensionSettingsMutation mutation,
    bool keyExisted)
{
    var current = ReadRegistryValueAtPath(hive, subKey, valueName);
    if (!current.Exists ||
        !string.Equals(current.Kind, RegistryValueKind.String.ToString(), StringComparison.Ordinal))
    {
        return;
    }

    var rollback = mutation.DecideRollback(current.Value);
    switch (rollback.Action)
    {
        case JsonPropertyRollbackAction.RetainCurrent:
            return;
        case JsonPropertyRollbackAction.SetValue:
            using (var key = hive.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Registry key could not be restored: {subKey}"))
            {
                key.SetValue(valueName, rollback.Value!, RegistryValueKind.String);
            }
            break;
        case JsonPropertyRollbackAction.DeleteValue:
            using (var key = hive.OpenSubKey(subKey, writable: true))
            {
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            break;
        default:
            throw new InvalidOperationException($"Unsupported Firefox rollback action: {rollback.Action}");
    }

    RemoveEmptyRegistryKeyCreatedByInstaller(hive, subKey, keyExisted);
}

static void RemoveEmptyRegistryKeyCreatedByInstaller(
    RegistryKey hive,
    string subKey,
    bool keyExisted)
{
    if (keyExisted)
    {
        return;
    }

    var shouldDelete = false;
    using (var key = hive.OpenSubKey(subKey))
    {
        shouldDelete = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
    }

    if (shouldDelete)
    {
        hive.DeleteSubKey(subKey, throwOnMissingSubKey: false);
    }
}

static void RestoreRegistrySnapshot(
    RegistryKey hive,
    string subKey,
    string valueName,
    RegistryValueSnapshot snapshot)
{
    if (!snapshot.Exists)
    {
        using var existingKey = hive.OpenSubKey(subKey, writable: true);
        existingKey?.DeleteValue(valueName, throwOnMissingValue: false);
        return;
    }

    using var key = hive.CreateSubKey(subKey, writable: true)
        ?? throw new InvalidOperationException($"Registry key could not be restored: {subKey}");
    var kind = Enum.Parse<RegistryValueKind>(snapshot.Kind ?? RegistryValueKind.String.ToString());
    key.SetValue(valueName, snapshot.Value ?? string.Empty, kind);
}

static RegistryValueSnapshot ReadRegistryValue(RegistryKey key, string valueName)
{
    var exists = key.GetValueNames().Any(name => name.Equals(valueName, StringComparison.OrdinalIgnoreCase));
    if (!exists)
    {
        return new RegistryValueSnapshot(false, null, null);
    }

    return new RegistryValueSnapshot(
        true,
        key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString(),
        key.GetValueKind(valueName).ToString());
}

static void EnsureStringRegistryValue(
    RegistryValueSnapshot value,
    string hiveName,
    string subKey,
    string valueName)
{
    if (value.Exists && !string.Equals(value.Kind, RegistryValueKind.String.ToString(), StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Registry value {hiveName}\\{subKey}\\{valueName} is not a string and will not be overwritten.");
    }
}

static void CopyDirectory(string source, string destination)
{
    if (!Directory.Exists(source))
    {
        return;
    }

    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        var targetDirectory = Path.Combine(destination, Path.GetRelativePath(source, directory));
        RequireChildPath(destination, targetDirectory, "staged directory");
        Directory.CreateDirectory(targetDirectory);
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var targetFile = Path.Combine(destination, Path.GetRelativePath(source, file));
        RequireChildPath(destination, targetFile, "staged file");
        File.Copy(file, targetFile, overwrite: true);
    }
}

static void CopyIfExists(string source, string destination)
{
    if (File.Exists(source))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}

static void WriteInstallerLog(string message)
{
    try
    {
        var logPath = GetInstallerLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
    }
    catch
    {
        // Setup logging is diagnostic only and must not block install or rollback.
    }
}

static string GetInstallerLogPath()
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var root = Path.Combine(localAppData, AppName);
    return Path.Combine(root, "installer.log");
}

static string FormatArgumentsForLog(string[] args)
{
    return args.Length == 0 ? "<none>" : string.Join(" ", args.Select(Quote));
}

static void StartProcess(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

static void StartAppForUser(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    if (IsElevated())
    {
        try
        {
            var explorer = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false
            };
            explorer.ArgumentList.Add(path);
            Process.Start(explorer);
            return;
        }
        catch
        {
            // Fall back to the direct launch below.
        }
    }

    StartProcess(path);
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static bool OpenBrowserExtensionPagesIfNeeded(InstallerOptions options, BrowserExtensionDeploymentResult deployment)
{
    var opened = false;
    if (HasPublishedValue(options.ChromeExtensionId) &&
        (deployment.SkippedByUser || deployment.ChromePolicyFailed || deployment.ChromiumPolicyFailed))
    {
        opened |= StartBrowserUrl(new[] { "chrome.exe", "chromium.exe" }, ChromeWebStoreListingUrl);
    }

    if (HasPublishedValue(options.FirefoxExtensionId) &&
        (deployment.SkippedByUser || deployment.FirefoxPolicyFailed))
    {
        opened |= StartBrowserUrl(new[] { "firefox.exe" }, FirefoxAddOnsListingUrl);
    }

    return opened;
}

static bool StartBrowserUrl(string[] browserExeNames, string url)
{
    foreach (var browserExeName in browserExeNames)
    {
        var browserPath = FindBrowserExecutable(browserExeName);
        if (browserPath is null)
        {
            continue;
        }

        try
        {
            Process.Start(new ProcessStartInfo(browserPath, url) { UseShellExecute = false });
            return true;
        }
        catch
        {
            // Fall through to the next browser or shell fallback.
        }
    }

    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }
    catch
    {
        return false;
    }
}

static string? FindBrowserExecutable(string fileName)
{
    if (fileName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
    {
        var normalFirefoxPath = FindBrowserExecutableCandidate(fileName);
        if (normalFirefoxPath is not null)
        {
            return normalFirefoxPath;
        }
    }

    foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
    {
        try
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
            var path = key?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }
        catch
        {
            // App Paths lookup is best effort.
        }
    }

    return FindBrowserExecutableCandidate(fileName);
}

static string? FindBrowserExecutableCandidate(string fileName)
{
    foreach (var candidate in BrowserExecutableCandidates(fileName))
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static IEnumerable<string> BrowserExecutableCandidates(string fileName)
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return fileName.ToLowerInvariant() switch
    {
        "chrome.exe" => new[]
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", fileName),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", fileName),
            Path.Combine(localAppData, "Google", "Chrome", "Application", fileName)
        },
        "chromium.exe" => new[]
        {
            Path.Combine(programFiles, "Chromium", "Application", fileName),
            Path.Combine(programFilesX86, "Chromium", "Application", fileName),
            Path.Combine(localAppData, "Chromium", "Application", fileName)
        },
        "firefox.exe" => new[]
        {
            Path.Combine(programFiles, "Mozilla Firefox", fileName),
            Path.Combine(programFilesX86, "Mozilla Firefox", fileName),
            Path.Combine(localAppData, "Mozilla Firefox", fileName)
        },
        _ => Array.Empty<string>()
    };
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                WriteInstallerLog($"Refusing to recursively delete reparse-point directory {path}.");
                return;
            }

            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
        // Temporary extraction can be cleaned later by Windows if a file is still held open.
    }
}

static bool TryLaunchNewerInstaller(
    InstallerOptions options,
    InstallInfo? previousInstallInfo)
{
    var updateRoot = Path.GetTempPath();
    string? updateDir = null;
    try
    {
        using var httpClient = CreateHttpClient();
        var release = UpdatePackage.GetLatestReleaseAsync(
            httpClient,
            SetupAssetName,
            ChecksumsAssetName).GetAwaiter().GetResult();
        var latestVersion = ParseVersion(release.TagName);
        var installerVersion = ParseVersion(AppVersion);
        if (latestVersion is null ||
            installerVersion is null ||
            CompareVersions(latestVersion, installerVersion) <= 0)
        {
            return false;
        }

        updateDir = UpdatePackage.CreateRestrictedUpdateDirectory(updateRoot, AppId + "-latest-");
        var setupPath = Path.Combine(updateDir, SetupAssetName);
        var checksumsPath = Path.Combine(updateDir, ChecksumsAssetName);
        RequireChildPath(updateDir, setupPath, "downloaded installer");
        RequireChildPath(updateDir, checksumsPath, "downloaded checksum file");

        UpdatePackage.DownloadFileAsync(
            httpClient,
            release.ChecksumsDownloadUrl,
            updateDir,
            checksumsPath).GetAwaiter().GetResult();
        UpdatePackage.DownloadFileAsync(
            httpClient,
            release.SetupDownloadUrl,
            updateDir,
            setupPath).GetAwaiter().GetResult();

        var expectedHash = UpdatePackage.ReadExpectedHash(checksumsPath, SetupAssetName);
        if (expectedHash is null)
        {
            throw new InvalidOperationException($"The latest release checksum file does not include {SetupAssetName}.");
        }

        var actualHash = UpdatePackage.ComputeSha256(setupPath);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The latest installer did not match the release checksum.");
        }

        MessageBox.Show(
            $"A newer Monitor Audio Router installer was downloaded and verified.\n\nThis installer: {AppVersion}\nLatest release: {release.TagName}\n\nWindows will ask for permission to run the newer installer.",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        UpdatePackage.ValidateInstallerPath(updateRoot, updateDir, setupPath);
        var immediatePreLaunchHash = UpdatePackage.ComputeSha256(setupPath);
        if (!UpdatePackage.CanLaunchVerifiedInstaller(
                expectedHash,
                actualHash,
                immediatePreLaunchHash,
                updateDirectoryIsSafe: true))
        {
            throw new InvalidOperationException("The latest installer changed after download verification.");
        }

        var persistedOptions = ToInstalledOptions(options);
        _ = Process.Start(new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = updateDir,
            Arguments = BuildForwardedInstallerArguments(options, persistedOptions)
        }) ?? throw new InvalidOperationException("The verified newer installer did not start.");
        return true;
    }
    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
    {
        if (updateDir is not null)
        {
            _ = UpdatePackage.TryDeleteRestrictedUpdateDirectory(updateRoot, updateDir);
        }

        MessageBox.Show(
            "The latest installer was canceled. This installer will continue.",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }
    catch (Exception ex)
    {
        if (updateDir is not null)
        {
            _ = UpdatePackage.TryDeleteRestrictedUpdateDirectory(updateRoot, updateDir);
        }

        MessageBox.Show(
            $"Could not update to the latest installer. This installer will continue.\n\n{ex.Message}",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }
}

static HttpClient CreateHttpClient()
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MonitorAudioRouterSetup", "1.0"));
    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    return httpClient;
}

static Version? ParseVersion(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var normalized = value.Trim();
    if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
    {
        normalized = normalized[1..];
    }

    var metadataIndex = normalized.IndexOfAny(new[] { '+', '-' });
    if (metadataIndex >= 0)
    {
        normalized = normalized[..metadataIndex];
    }

    return Version.TryParse(normalized, out var version) ? version : null;
}

static int CompareVersions(Version left, Version right)
{
    var leftParts = new[] { left.Major, left.Minor, Math.Max(0, left.Build), Math.Max(0, left.Revision) };
    var rightParts = new[] { right.Major, right.Minor, Math.Max(0, right.Build), Math.Max(0, right.Revision) };
    for (var i = 0; i < leftParts.Length; i++)
    {
        var comparison = leftParts[i].CompareTo(rightParts[i]);
        if (comparison != 0)
        {
            return comparison;
        }
    }

    return 0;
}

static string BuildForwardedInstallerArguments(InstallerOptions options, InstalledOptions installedOptions)
{
    var args = new List<string>
    {
        "/nooptions",
        "/noupdatetolatest"
    };

    if (!options.Launch)
    {
        args.Add("/nolaunch");
    }

    if (!options.OpenBrowserSetup)
    {
        args.Add("/nobrowsersetup");
    }

    args.AddRange(InstallDecisions.BuildForwardedArguments(installedOptions));

    return string.Join(" ", args.Select(QuoteArgument));
}

static string QuoteArgument(string value)
{
    if (value.Length == 0)
    {
        return "\"\"";
    }

    var needsQuotes = value.Any(char.IsWhiteSpace) || value.Contains('"');
    if (!needsQuotes)
    {
        return value;
    }

    var builder = new StringBuilder();
    builder.Append('"');
    var backslashCount = 0;
    foreach (var c in value)
    {
        if (c == '\\')
        {
            backslashCount++;
            continue;
        }

        if (c == '"')
        {
            builder.Append('\\', backslashCount * 2 + 1);
            builder.Append('"');
        }
        else
        {
            builder.Append('\\', backslashCount);
            builder.Append(c);
        }

        backslashCount = 0;
    }

    builder.Append('\\', backslashCount * 2);
    builder.Append('"');
    return builder.ToString();
}

static InstallerOptions ApplyInteractiveOptions(InstallerOptions options)
{
    if (!options.ShowOptions)
    {
        return options;
    }

    InstallerOptions result = options;
    var thread = new Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new InstallOptionsForm(options);
        if (form.ShowDialog() != DialogResult.OK)
        {
            result = options with { Canceled = true };
            return;
        }

        result = InstallerOptionResolver.ApplyInteractiveChoices(
            options,
            form.InstallBrowserExtensions,
            form.UpdateToLatestDuringInstall,
            form.Autostart,
            form.EnablePrivateBrowsing);
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return result;
}

static bool HasSwitch(string[] args, string switchName)
{
    return args.Any(arg => arg.Equals(switchName, StringComparison.OrdinalIgnoreCase));
}

static bool HasPublishedValue(string? value)
{
    return !string.IsNullOrWhiteSpace(value) &&
           !value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}

sealed record InstallerOptions(
    bool Launch,
    bool OpenBrowserSetup,
    bool ShowOptions,
    bool Canceled,
    bool InstallBrowserExtensions,
    bool UpdateToLatestDuringInstall,
    bool Autostart,
    bool EnablePrivateBrowsing,
    string ChromeExtensionId,
    string ChromeUpdateUrl,
    string EdgeExtensionId,
    string EdgeUpdateUrl,
    string FirefoxExtensionId,
    string FirefoxInstallUrl);

internal static class InstallerOptionResolver
{
    internal static InstallerOptions ResolvePrimaryOptions(
        string[] arguments,
        string? installInfoJson,
        bool legacyBrowserExtensionsInstalled,
        bool legacyAutostartInstalled)
    {
        var parsed = Parse(arguments);
        if (installInfoJson is null)
        {
            return parsed.Options;
        }

        var persisted = InstallInfo.Deserialize(installInfoJson).ResolveOptions(new InstalledOptions(
            legacyBrowserExtensionsInstalled,
            legacyAutostartInstalled,
            parsed.Options.EnablePrivateBrowsing,
            parsed.Options.ChromeExtensionId,
            parsed.Options.ChromeUpdateUrl,
            parsed.Options.EdgeExtensionId,
            parsed.Options.EdgeUpdateUrl,
            parsed.Options.FirefoxExtensionId,
            parsed.Options.FirefoxInstallUrl));

        return parsed.Options with
        {
            InstallBrowserExtensions = parsed.Overrides.InstallBrowserExtensions
                ? parsed.Options.InstallBrowserExtensions
                : persisted.InstallBrowserExtensions,
            Autostart = parsed.Overrides.Autostart
                ? parsed.Options.Autostart
                : persisted.Autostart,
            EnablePrivateBrowsing = parsed.Overrides.EnablePrivateBrowsing
                ? parsed.Options.EnablePrivateBrowsing
                : persisted.EnablePrivateBrowsing,
            ChromeExtensionId = parsed.Overrides.ChromeExtensionId
                ? parsed.Options.ChromeExtensionId
                : persisted.ChromeExtensionId,
            ChromeUpdateUrl = parsed.Overrides.ChromeUpdateUrl
                ? parsed.Options.ChromeUpdateUrl
                : persisted.ChromeUpdateUrl,
            EdgeExtensionId = parsed.Overrides.EdgeExtensionId
                ? parsed.Options.EdgeExtensionId
                : persisted.EdgeExtensionId,
            EdgeUpdateUrl = parsed.Overrides.EdgeUpdateUrl
                ? parsed.Options.EdgeUpdateUrl
                : persisted.EdgeUpdateUrl,
            FirefoxExtensionId = parsed.Overrides.FirefoxExtensionId
                ? parsed.Options.FirefoxExtensionId
                : persisted.FirefoxExtensionId,
            FirefoxInstallUrl = parsed.Overrides.FirefoxInstallUrl
                ? parsed.Options.FirefoxInstallUrl
                : persisted.FirefoxInstallUrl
        };
    }

    internal static InstallerOptions ApplyInteractiveChoices(
        InstallerOptions options,
        bool installBrowserExtensions,
        bool updateToLatestDuringInstall,
        bool autostart,
        bool enablePrivateBrowsing) =>
        options with
        {
            InstallBrowserExtensions = installBrowserExtensions,
            UpdateToLatestDuringInstall = updateToLatestDuringInstall,
            Autostart = autostart,
            EnablePrivateBrowsing = installBrowserExtensions && enablePrivateBrowsing
        };

    private static ParsedInstallerOptions Parse(string[] arguments)
    {
        var noOptions = HasSwitch(arguments, "/nooptions") || HasSwitch(arguments, "/quiet");
        var hasExplicitBrowserExtensionChoice =
            HasSwitch(arguments, "/browserextensions") ||
            HasSwitch(arguments, "/nobrowserextensions");
        var hasExplicitUpdateChoice =
            HasSwitch(arguments, "/updatetolatest") ||
            HasSwitch(arguments, "/update") ||
            HasSwitch(arguments, "/noupdatetolatest") ||
            HasSwitch(arguments, "/noupdate") ||
            HasSwitch(arguments, "/noupdateduringinstall");
        var hasExplicitAutostartChoice =
            HasSwitch(arguments, "/autostart") ||
            HasSwitch(arguments, "/noautostart");
        var hasExplicitPrivateBrowsingChoice =
            HasSwitch(arguments, "/enableprivatebrowsing") ||
            HasSwitch(arguments, "/browserprivate") ||
            HasSwitch(arguments, "/disableprivatebrowsing");
        var hasAnyExplicitInstallOption = hasExplicitBrowserExtensionChoice ||
                                          hasExplicitUpdateChoice ||
                                          hasExplicitAutostartChoice;

        var chromeExtensionId = GetOptionValue(arguments, "ChromeExtensionId", InstallChoiceDefaults.ChromeExtensionId);
        var chromeUpdateUrl = GetOptionValue(arguments, "ChromeUpdateUrl", InstallChoiceDefaults.ChromeUpdateUrl);
        var edgeExtensionId = GetOptionValue(arguments, "EdgeExtensionId", InstallChoiceDefaults.EdgeExtensionId);
        var edgeUpdateUrl = GetOptionValue(arguments, "EdgeUpdateUrl", InstallChoiceDefaults.EdgeUpdateUrl);
        var firefoxExtensionId = GetOptionValue(arguments, "FirefoxExtensionId", InstallChoiceDefaults.FirefoxExtensionId);
        var firefoxInstallUrl = GetOptionValue(arguments, "FirefoxInstallUrl", InstallChoiceDefaults.FirefoxInstallUrl);

        var options = new InstallerOptions(
            Launch: !HasSwitch(arguments, "/nolaunch"),
            OpenBrowserSetup: !HasSwitch(arguments, "/nobrowsersetup"),
            ShowOptions: !noOptions && !hasAnyExplicitInstallOption,
            Canceled: false,
            InstallBrowserExtensions: HasSwitch(arguments, "/browserextensions") || !HasSwitch(arguments, "/nobrowserextensions"),
            UpdateToLatestDuringInstall: HasSwitch(arguments, "/updatetolatest") ||
                                         HasSwitch(arguments, "/update") ||
                                         (!noOptions && !hasExplicitUpdateChoice),
            Autostart: HasSwitch(arguments, "/autostart") || !HasSwitch(arguments, "/noautostart"),
            EnablePrivateBrowsing: HasSwitch(arguments, "/enableprivatebrowsing") || HasSwitch(arguments, "/browserprivate"),
            ChromeExtensionId: chromeExtensionId.Value,
            ChromeUpdateUrl: chromeUpdateUrl.Value,
            EdgeExtensionId: edgeExtensionId.Value,
            EdgeUpdateUrl: edgeUpdateUrl.Value,
            FirefoxExtensionId: firefoxExtensionId.Value,
            FirefoxInstallUrl: firefoxInstallUrl.Value);
        var overrides = new InstallerOptionOverrides(
            hasExplicitBrowserExtensionChoice,
            hasExplicitAutostartChoice,
            hasExplicitPrivateBrowsingChoice,
            chromeExtensionId.IsExplicit,
            chromeUpdateUrl.IsExplicit,
            edgeExtensionId.IsExplicit,
            edgeUpdateUrl.IsExplicit,
            firefoxExtensionId.IsExplicit,
            firefoxInstallUrl.IsExplicit);
        return new ParsedInstallerOptions(options, overrides);
    }

    private static OptionValue GetOptionValue(string[] arguments, string name, string fallback)
    {
        foreach (var prefix in new[] { "/" + name + "=", "/" + name + ":" })
        {
            var match = arguments.FirstOrDefault(argument =>
                argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new OptionValue(match[prefix.Length..].Trim().Trim('"'), IsExplicit: true);
            }
        }

        return new OptionValue(fallback, IsExplicit: false);
    }

    private static bool HasSwitch(string[] arguments, string switchName) =>
        arguments.Any(argument => argument.Equals(switchName, StringComparison.OrdinalIgnoreCase));

    private sealed record ParsedInstallerOptions(
        InstallerOptions Options,
        InstallerOptionOverrides Overrides);

    private sealed record InstallerOptionOverrides(
        bool InstallBrowserExtensions,
        bool Autostart,
        bool EnablePrivateBrowsing,
        bool ChromeExtensionId,
        bool ChromeUpdateUrl,
        bool EdgeExtensionId,
        bool EdgeUpdateUrl,
        bool FirefoxExtensionId,
        bool FirefoxInstallUrl);

    private readonly record struct OptionValue(string Value, bool IsExplicit);
}

sealed class InstallOptionsForm : Form
{
    private readonly CheckBox _browserExtensions;
    private readonly CheckBox _updateToLatest;
    private readonly CheckBox _autostart;
    private readonly CheckBox _privateBrowsing;

    public InstallOptionsForm(InstallerOptions options)
    {
        Text = "Monitor Audio Router Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(480, 255);
        ShowIcon = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Text = "Choose install options. The recommended options are enabled by default."
        }, 0, 0);

        _browserExtensions = new CheckBox
        {
            AutoSize = true,
            Checked = options.InstallBrowserExtensions,
            Margin = new Padding(0, 14, 0, 0),
            Text = "Install browser companion extensions"
        };
        root.Controls.Add(_browserExtensions, 0, 1);

        _updateToLatest = new CheckBox
        {
            AutoSize = true,
            Checked = options.UpdateToLatestDuringInstall,
            Margin = new Padding(0, 6, 0, 0),
            Text = "Update to latest version during install"
        };
        root.Controls.Add(_updateToLatest, 0, 2);

        _autostart = new CheckBox
        {
            AutoSize = true,
            Checked = options.Autostart,
            Margin = new Padding(0, 6, 0, 0),
            Text = "Start Monitor Audio Router with Windows"
        };
        root.Controls.Add(_autostart, 0, 3);

        _privateBrowsing = new CheckBox
        {
            AutoSize = true,
            Checked = options.EnablePrivateBrowsing,
            Enabled = options.InstallBrowserExtensions,
            Margin = new Padding(0, 6, 0, 0),
            Text = "Allow private/incognito browser windows"
        };
        _browserExtensions.CheckedChanged += (_, _) => _privateBrowsing.Enabled = _browserExtensions.Checked;
        root.Controls.Add(_privateBrowsing, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        var installButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Install"
        };
        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Cancel"
        };
        buttons.Controls.Add(installButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 5);

        AcceptButton = installButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    public bool InstallBrowserExtensions => _browserExtensions.Checked;
    public bool UpdateToLatestDuringInstall => _updateToLatest.Checked;
    public bool Autostart => _autostart.Checked;
    public bool EnablePrivateBrowsing => _browserExtensions.Checked && _privateBrowsing.Checked;
}

sealed class BrowserExtensionDeploymentResult
{
    public bool SkippedByUser { get; set; }
    public bool ChromePolicyInstalled { get; set; }
    public bool ChromePolicyFailed { get; set; }
    public bool ChromiumPolicyInstalled { get; set; }
    public bool ChromiumPolicyFailed { get; set; }
    public bool EdgePolicyInstalled { get; set; }
    public bool EdgePolicyFailed { get; set; }
    public bool FirefoxPolicyInstalled { get; set; }
    public bool FirefoxPolicyFailed { get; set; }
}
