using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Win32;
using MonitorAudioRouter.Setup;

const string AppName = "Monitor Audio Router";
const string AppId = "MonitorAudioRouter";
const string AppVersion = "0.1.14";
const string HostName = "com.monitoraudiorouter.router";
const string DefaultChromeExtensionId = "jnjminkakfohjeffdpeamngcnfneckog";
const string DefaultEdgeExtensionId = "";
const string DefaultFirefoxExtensionId = "monitor-audio-router@example.local";
const string DefaultFirefoxInstallUrl = "https://addons.mozilla.org/firefox/downloads/latest/monitor-audio-router-bridge/latest.xpi";
const string ChromeWebStoreListingUrl = "https://chromewebstore.google.com/detail/jnjminkakfohjeffdpeamngcnfneckog";
const string FirefoxAddOnsListingUrl = "https://addons.mozilla.org/en-US/firefox/addon/monitor-audio-router-bridge/";
const string ChromeWebStoreUpdateUrl = "https://clients2.google.com/service/update2/crx";
const string EdgeAddOnsUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";
const string LatestReleaseApiUrl = "https://api.github.com/repos/TechlyAccurate/MonitorAudioRouter/releases/latest";
const string SetupAssetName = "MonitorAudioRouterSetup.exe";
const string ChecksumsAssetName = "SHA256SUMS.txt";
const string RunValueName = "Monitor Audio Router";

var options = ApplyInteractiveOptions(ParseOptions(args));
if (options.Canceled)
{
    Environment.ExitCode = 1223;
    return;
}

WriteInstallerLog($"Setup started. Version={AppVersion}; ProcessId={Environment.ProcessId}; Arguments={FormatArgumentsForLog(args)}");

var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    AppName);
var previousInstallInfo = ReadInstallInfo(installDir);
if (options.UpdateToLatestDuringInstall && TryLaunchNewerInstaller(options, previousInstallInfo))
{
    WriteInstallerLog("Setup handed off to a newer published installer.");
    return;
}

var replacementApplied = false;
string? pendingBackupDir = null;
InstallerStateTransaction? stateTransaction = null;
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
        WriteInstallerLog($"Stopping existing app processes before installing to {installDir}.");
        StopExistingApp(installDir);
        WriteInstallerLog("Replacing application files transactionally.");
        var backupCreated = ReplaceInstallation(stagedInstallDir, installDir, backupDir);
        replacementApplied = true;
        pendingBackupDir = backupCreated ? backupDir : null;
        stateTransaction = new InstallerStateTransaction();
        var registryOwnership = new RegistryOwnershipRecorder(previousInstallInfo?.RegistryValues);
        WriteInstallerLog("Writing native messaging manifests.");
        WriteNativeMessagingManifests(installDir, options);
        WriteInstallerLog("Registering native messaging hosts.");
        RegisterNativeMessagingHosts(installDir, registryOwnership, stateTransaction);
        WriteInstallerLog("Registering browser extension deployment policies.");
        var browserExtensionDeployment = RegisterBrowserExtensionPolicies(options, registryOwnership, stateTransaction);
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
        stateTransaction.Commit();
        WriteInstallerLog("Install registry state written.");
        if (pendingBackupDir is not null)
        {
            TryDeleteDirectory(pendingBackupDir);
            pendingBackupDir = null;
        }

        replacementApplied = false;

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
    var rollbackFailures = new List<Exception>();
    if (stateTransaction is not null)
    {
        try
        {
            stateTransaction.RollBack();
        }
        catch (Exception rollbackException)
        {
            rollbackFailures.Add(rollbackException);
        }
    }

    if (replacementApplied)
    {
        try
        {
            RollBackInstallation(installDir, pendingBackupDir);
        }
        catch (Exception rollbackException)
        {
            rollbackFailures.Add(rollbackException);
        }
    }

    Exception reportedException = rollbackFailures.Count == 0
        ? exception
        : new AggregateException(
            "Installation failed and one or more parts of the prior state could not be restored.",
            new[] { exception }.Concat(rollbackFailures));

    Console.Error.WriteLine("Install failed:");
    Console.Error.WriteLine(reportedException);
    WriteInstallerLog($"Setup failed: {reportedException}");
    if (!HasSwitch(args, "/quiet"))
    {
        MessageBox.Show(
            "Monitor Audio Router could not be installed.\n\n" + reportedException.Message + "\n\nSee installer.log in the app data folder for details.",
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

static void StopExistingApp(string installDir)
{
    var expectedPaths = new[]
    {
        Path.Combine(installDir, "MonitorAudioRouter.exe"),
        Path.Combine(installDir, "MonitorAudioRouterNativeHost.exe")
    };
    var installedProcesses = new List<Process>();
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

            var executablePath = process.MainModule?.FileName;
            if (executablePath is null ||
                !expectedPaths.Any(expected => InstallDecisions.IsExactExecutablePath(executablePath, expected)))
            {
                process.Dispose();
                continue;
            }

            installedProcesses.Add(process);
        }
        catch (Exception exception)
        {
            var processId = process.Id;
            process.Dispose();
            foreach (var installedProcess in installedProcesses)
            {
                installedProcess.Dispose();
            }

            throw new InvalidOperationException(
                $"Process PID {processId} has an installed executable name, but its path could not be verified.",
                exception);
        }
    }

    try
    {
        foreach (var process in installedProcesses)
        {
            WriteInstallerLog($"Stopping installed executable {process.MainModule?.FileName} PID {process.Id}.");
            process.Kill(entireProcessTree: false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        foreach (var process in installedProcesses)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                process.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue));
            }

            if (!process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Installed process PID {process.Id} did not exit within the shutdown timeout.");
            }
        }
    }
    finally
    {
        foreach (var process in installedProcesses)
        {
            process.Dispose();
        }
    }

    ClearManagedRoutes(installDir);
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

static bool ReplaceInstallation(string stagedInstallDir, string installDir, string backupDir)
{
    var replacementRoot = Path.GetDirectoryName(installDir)
        ?? throw new InvalidOperationException("The installation directory does not have a parent directory.");
    RequireChildPath(replacementRoot, stagedInstallDir, "staged installation");
    RequireChildPath(replacementRoot, installDir, "installation directory");
    RequireChildPath(replacementRoot, backupDir, "backup directory");

    var backupCreated = false;
    var replacementStarted = false;
    try
    {
        if (Directory.Exists(backupDir))
        {
            throw new InvalidOperationException($"The backup directory already exists: {backupDir}");
        }

        if (Directory.Exists(installDir))
        {
            Directory.Move(installDir, backupDir);
            backupCreated = true;
        }

        replacementStarted = true;
        Directory.Move(stagedInstallDir, installDir);
    }
    catch (Exception replacementException)
    {
        try
        {
            if (replacementStarted && Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            if (backupCreated)
            {
                Directory.Move(backupDir, installDir);
            }
        }
        catch (Exception rollbackException)
        {
            throw new AggregateException(
                "Installation replacement and rollback both failed.",
                replacementException,
                rollbackException);
        }

        throw;
    }

    return backupCreated;
}

static void RollBackInstallation(string installDir, string? backupDir)
{
    var replacementRoot = Path.GetDirectoryName(installDir)
        ?? throw new InvalidOperationException("The installation directory does not have a parent directory.");
    RequireChildPath(replacementRoot, installDir, "installation directory");
    if (Directory.Exists(installDir))
    {
        Directory.Delete(installDir, recursive: true);
    }

    if (backupDir is not null)
    {
        RequireChildPath(replacementRoot, backupDir, "backup directory");
        Directory.Move(backupDir, installDir);
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
    var settings = ParseExistingJsonObjectForMerge(currentValue.Value);
    var priorExtensionPolicy = settings[extensionId];
    var prior = new RegistryValueSnapshot(
        priorExtensionPolicy is not null,
        priorExtensionPolicy?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
        "Json");
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

    settings[extensionId] = extensionPolicy;
    var writtenExtensionPolicy = new RegistryValueSnapshot(
        true,
        extensionPolicy.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
        "Json");
    RegistryValueSnapshot? writtenValue = null;
    stateTransaction.Apply(
        InstallerMutationKind.Registry,
        () =>
        {
            using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true)
                ?? throw new InvalidOperationException($"Registry key could not be created: HKLM\\{subKey}");
            key.SetValue(
                valueName,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                RegistryValueKind.String);
            writtenValue = ReadRegistryValue(key, valueName);
            registryOwnership.Record(
                "HKLM",
                subKey,
                valueName,
                prior,
                writtenExtensionPolicy,
                extensionId);
        },
        () => RestoreRegistryMutation(
            Registry.LocalMachine,
            subKey,
            valueName,
            currentValue,
            writtenValue,
            keyExisted));
}

static JsonObject ParseExistingJsonObjectForMerge(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return new JsonObject();
    }

    return JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException("The existing Firefox ExtensionSettings value is not a JSON object.");
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
    var priorState = FileStateSnapshot.Capture(configPath);
    try
    {
        stateTransaction.Apply(
            InstallerMutationKind.UserConfiguration,
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var settings = ParseJsonObject(File.Exists(configPath) ? File.ReadAllText(configPath) : null);
                settings["AutostartEnabled"] = enabled;
                File.WriteAllText(configPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            },
            () => priorState.Restore(configPath));
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
    var priorState = FileStateSnapshot.Capture(shortcutPath);
    try
    {
        stateTransaction.Apply(
            InstallerMutationKind.StartMenuShortcut,
            () =>
            {
                Directory.CreateDirectory(programsDir);
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
            },
            () => priorState.Restore(shortcutPath));
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

static InstalledOptions ResolvePersistedOptionsForUpdate(
    InstallerOptions options,
    InstallInfo? previousInstallInfo)
{
    var currentOptions = ToInstalledOptions(options);
    if (previousInstallInfo is null)
    {
        return currentOptions;
    }

    var legacyFallback = currentOptions with
    {
        InstallBrowserExtensions = HasInstalledBrowserPolicy(previousInstallInfo.ResolveOptions(currentOptions)),
        Autostart = HasInstalledAutostart()
    };
    return previousInstallInfo.ResolveOptions(legacyFallback);
}

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
    if (!keyExisted)
    {
        using var key = hive.OpenSubKey(subKey);
        if (key is not null && key.ValueCount == 0 && key.SubKeyCount == 0)
        {
            key.Dispose();
            hive.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        }
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
    string? updateDir = null;
    try
    {
        using var httpClient = CreateHttpClient();
        var release = GetLatestReleaseAsync(httpClient).GetAwaiter().GetResult();
        var latestVersion = ParseVersion(release.TagName);
        var installerVersion = ParseVersion(AppVersion);
        if (latestVersion is null ||
            installerVersion is null ||
            CompareVersions(latestVersion, installerVersion) <= 0)
        {
            return false;
        }

        updateDir = CreateRestrictedUpdateDirectory();
        var setupPath = Path.Combine(updateDir, SetupAssetName);
        var checksumsPath = Path.Combine(updateDir, ChecksumsAssetName);
        RequireChildPath(updateDir, setupPath, "downloaded installer");
        RequireChildPath(updateDir, checksumsPath, "downloaded checksum file");

        DownloadFileAsync(httpClient, release.ChecksumsDownloadUrl, checksumsPath).GetAwaiter().GetResult();
        DownloadFileAsync(httpClient, release.SetupDownloadUrl, setupPath).GetAwaiter().GetResult();

        var expectedHash = ReadExpectedHash(checksumsPath, SetupAssetName);
        if (expectedHash is null)
        {
            throw new InvalidOperationException($"The latest release checksum file does not include {SetupAssetName}.");
        }

        var actualHash = ComputeSha256(setupPath);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The latest installer did not match the release checksum.");
        }

        MessageBox.Show(
            $"A newer Monitor Audio Router installer was downloaded and verified.\n\nThis installer: {AppVersion}\nLatest release: {release.TagName}\n\nWindows will ask for permission to run the newer installer.",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        ValidateUpdateDirectory(updateDir);
        var immediatePreLaunchHash = ComputeSha256(setupPath);
        if (!InstallDecisions.CanLaunchVerifiedInstaller(
                expectedHash,
                actualHash,
                immediatePreLaunchHash,
                updateDirectoryIsSafe: true))
        {
            throw new InvalidOperationException("The latest installer changed after download verification.");
        }

        var persistedOptions = ResolvePersistedOptionsForUpdate(options, previousInstallInfo);
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
            TryDeleteDirectory(updateDir);
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
            TryDeleteDirectory(updateDir);
        }

        MessageBox.Show(
            $"Could not update to the latest installer. This installer will continue.\n\n{ex.Message}",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }
}

static string CreateRestrictedUpdateDirectory()
{
    var updateDir = Path.Combine(Path.GetTempPath(), AppId + "-latest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(updateDir);

    var currentUser = WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(currentUser);
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    security.AddAccessRule(new FileSystemAccessRule(
        currentUser,
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(updateDir), security);

    ValidateUpdateDirectory(updateDir);
    return updateDir;
}

static void ValidateUpdateDirectory(string updateDir)
{
    if (!InstallDecisions.IsPathWithinRoot(Path.GetTempPath(), updateDir))
    {
        throw new InvalidOperationException("The update directory is outside the system temporary directory.");
    }

    var attributes = File.GetAttributes(updateDir);
    if (!InstallDecisions.IsSafeUpdateDirectory(attributes))
    {
        throw new InvalidOperationException("The update directory is a reparse point or is not a directory.");
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

static async Task<ReleaseInfo> GetLatestReleaseAsync(HttpClient httpClient)
{
    if (!InstallDecisions.IsAllowedReleaseUri(LatestReleaseApiUrl, isApiRequest: true))
    {
        throw new InvalidOperationException("The configured release API URL is not trusted.");
    }

    using var response = await httpClient.GetAsync(LatestReleaseApiUrl);
    response.EnsureSuccessStatusCode();
    if (response.RequestMessage?.RequestUri is not Uri finalUri ||
        !InstallDecisions.IsAllowedReleaseUri(finalUri.AbsoluteUri, isApiRequest: true))
    {
        throw new InvalidOperationException("The release API redirected to an untrusted URL.");
    }

    await using var stream = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(stream);
    var root = document.RootElement;
    var tagName = root.GetProperty("tag_name").GetString();
    if (string.IsNullOrWhiteSpace(tagName))
    {
        throw new InvalidOperationException("GitHub did not return a release tag.");
    }

    var setupUrl = FindAssetDownloadUrl(root, SetupAssetName);
    var checksumsUrl = FindAssetDownloadUrl(root, ChecksumsAssetName);
    if (setupUrl is null || checksumsUrl is null)
    {
        throw new InvalidOperationException("The latest GitHub release is missing the installer or checksum asset.");
    }

    return new ReleaseInfo(tagName, setupUrl, checksumsUrl);
}

static string? FindAssetDownloadUrl(JsonElement releaseRoot, string assetName)
{
    if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    foreach (var asset in assets.EnumerateArray())
    {
        var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var url = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    return null;
}

static async Task DownloadFileAsync(HttpClient httpClient, string url, string destinationPath)
{
    if (!InstallDecisions.IsAllowedReleaseUri(url, isApiRequest: false))
    {
        throw new InvalidOperationException("A release asset URL is not trusted.");
    }

    var tempPath = destinationPath + ".download";
    File.Delete(tempPath);
    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    if (response.RequestMessage?.RequestUri is not Uri finalUri ||
        !InstallDecisions.IsAllowedReleaseUri(finalUri.AbsoluteUri, isApiRequest: false))
    {
        throw new InvalidOperationException("A release asset redirected to an untrusted URL.");
    }

    await using (var input = await response.Content.ReadAsStreamAsync())
    await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await input.CopyToAsync(output);
    }

    File.Move(tempPath, destinationPath, overwrite: true);
}

static string? ReadExpectedHash(string checksumsPath, string assetName)
{
    foreach (var line in File.ReadLines(checksumsPath))
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && string.Equals(parts[1], assetName, StringComparison.OrdinalIgnoreCase))
        {
            return parts[0];
        }
    }

    return null;
}

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

static InstallerOptions ParseOptions(string[] args)
{
    var noOptions = HasSwitch(args, "/nooptions") || HasSwitch(args, "/quiet");
    var hasExplicitBrowserExtensionChoice =
        HasSwitch(args, "/browserextensions") ||
        HasSwitch(args, "/nobrowserextensions");
    var hasExplicitUpdateChoice =
        HasSwitch(args, "/updatetolatest") ||
        HasSwitch(args, "/update") ||
        HasSwitch(args, "/noupdatetolatest") ||
        HasSwitch(args, "/noupdate") ||
        HasSwitch(args, "/noupdateduringinstall");
    var hasExplicitAutostartChoice =
        HasSwitch(args, "/autostart") ||
        HasSwitch(args, "/noautostart");
    var hasAnyExplicitInstallOption = hasExplicitBrowserExtensionChoice ||
                                      hasExplicitUpdateChoice ||
                                      hasExplicitAutostartChoice;

    return new InstallerOptions(
        Launch: !HasSwitch(args, "/nolaunch"),
        OpenBrowserSetup: !HasSwitch(args, "/nobrowsersetup"),
        ShowOptions: !noOptions && !hasAnyExplicitInstallOption,
        Canceled: false,
        InstallBrowserExtensions: HasSwitch(args, "/browserextensions") || !HasSwitch(args, "/nobrowserextensions"),
        UpdateToLatestDuringInstall: HasSwitch(args, "/updatetolatest") ||
                                     HasSwitch(args, "/update") ||
                                     (!noOptions &&
                                      !HasSwitch(args, "/noupdatetolatest") &&
                                      !HasSwitch(args, "/noupdate") &&
                                      !HasSwitch(args, "/noupdateduringinstall")),
        Autostart: HasSwitch(args, "/autostart") || !HasSwitch(args, "/noautostart"),
        EnablePrivateBrowsing: HasSwitch(args, "/enableprivatebrowsing") || HasSwitch(args, "/browserprivate"),
        ChromeExtensionId: GetOptionValue(args, "ChromeExtensionId", DefaultChromeExtensionId),
        ChromeUpdateUrl: GetOptionValue(args, "ChromeUpdateUrl", ChromeWebStoreUpdateUrl),
        EdgeExtensionId: GetOptionValue(args, "EdgeExtensionId", DefaultEdgeExtensionId),
        EdgeUpdateUrl: GetOptionValue(args, "EdgeUpdateUrl", EdgeAddOnsUpdateUrl),
        FirefoxExtensionId: GetOptionValue(args, "FirefoxExtensionId", DefaultFirefoxExtensionId),
        FirefoxInstallUrl: GetOptionValue(args, "FirefoxInstallUrl", DefaultFirefoxInstallUrl));
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

        result = options with
        {
            InstallBrowserExtensions = form.InstallBrowserExtensions,
            UpdateToLatestDuringInstall = form.UpdateToLatestDuringInstall,
            Autostart = form.Autostart,
            EnablePrivateBrowsing = form.EnablePrivateBrowsing
        };
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

static string GetOptionValue(string[] args, string name, string fallback)
{
    foreach (var prefix in new[] { "/" + name + "=", "/" + name + ":" })
    {
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match[prefix.Length..].Trim().Trim('"');
        }
    }

    return fallback;
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

sealed record ReleaseInfo(string TagName, string SetupDownloadUrl, string ChecksumsDownloadUrl);

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
