using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Win32;

const string AppName = "Monitor Audio Router";
const string AppId = "MonitorAudioRouter";
const string AppVersion = "0.1.12";
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

if (options.UpdateToLatestDuringInstall && TryLaunchNewerInstaller(options))
{
    WriteInstallerLog("Setup handed off to a newer published installer.");
    return;
}

var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    AppName);

try
{
    var tempDir = Path.Combine(Path.GetTempPath(), AppId + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        WriteInstallerLog($"Extracting payload to {tempDir}.");
        ExtractPayload(tempDir);
        WriteInstallerLog($"Stopping existing app processes before installing to {installDir}.");
        StopExistingApp(installDir);
        WriteInstallerLog("Copying application files.");
        InstallFiles(tempDir, installDir);
        WriteInstallerLog("Writing native messaging manifests.");
        WriteNativeMessagingManifests(installDir, options);
        WriteInstallerLog("Registering native messaging hosts.");
        RegisterNativeMessagingHosts(installDir);
        WriteInstallerLog("Registering browser extension deployment policies.");
        var browserExtensionDeployment = RegisterBrowserExtensionPolicies(options);
        WriteInstallerLog("Applying startup and shortcut settings.");
        SetStartup(installDir, options.Autostart);
        InstallStartMenuShortcut(installDir);
        WriteUserAutostartSetting(options.Autostart);
        WriteInstallInfo(installDir, options);
        RegisterUninstaller(installDir);
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
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine("Install failed:");
    Console.Error.WriteLine(exception);
    WriteInstallerLog($"Setup failed: {exception}");
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

static void StopExistingApp(string installDir)
{
    TryClearManagedRoutes(installDir);

    foreach (var processName in new[] { "MonitorAudioRouter", "MonitorAudioRouterNativeHost" })
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    WriteInstallerLog($"Stopping {process.ProcessName} PID {process.Id}.");
                    process.Kill(entireProcessTree: false);
                }
                catch (Exception exception)
                {
                    WriteInstallerLog($"Could not stop {process.ProcessName} PID {process.Id}: {exception.Message}");
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            foreach (var process in processes)
            {
                try
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        process.WaitForExit((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue));
                    }
                }
                catch
                {
                    // Best effort only. File replacement below will fail if the app is still locked.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

static void TryClearManagedRoutes(string installDir)
{
    var existingExe = Path.Combine(installDir, "MonitorAudioRouter.exe");
    if (!File.Exists(existingExe))
    {
        return;
    }

    try
    {
        WriteInstallerLog("Clearing managed audio routes before app shutdown.");
        using var process = Process.Start(new ProcessStartInfo(existingExe, "--clear-managed-routes")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(5000);
    }
    catch (Exception exception)
    {
        WriteInstallerLog($"Managed route cleanup did not complete: {exception.Message}");
    }
}

static void InstallFiles(string tempDir, string installDir)
{
    Directory.CreateDirectory(installDir);

    CopyDirectory(Path.Combine(tempDir, "app"), installDir);
    CopyDirectory(Path.Combine(tempDir, "extensions"), Path.Combine(installDir, "extensions"));
    CopyDirectory(Path.Combine(tempDir, "packages"), Path.Combine(installDir, "packages"));

    CopyIfExists(Path.Combine(tempDir, "README.md"), Path.Combine(installDir, "README.md"));
    CopyIfExists(Path.Combine(tempDir, "PUBLISHING.md"), Path.Combine(installDir, "PUBLISHING.md"));
    CopyIfExists(Path.Combine(tempDir, "BrowserSetup.html"), Path.Combine(installDir, "BrowserSetup.html"));
    CopyIfExists(Path.Combine(tempDir, "Uninstall-MonitorAudioRouter.ps1"), Path.Combine(installDir, "Uninstall-MonitorAudioRouter.ps1"));

    var configPath = Path.Combine(installDir, "config.json");
    var defaultConfigPath = Path.Combine(installDir, "config.default.json");
    if (!File.Exists(configPath) && File.Exists(defaultConfigPath))
    {
        File.Copy(defaultConfigPath, configPath);
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

static void RegisterNativeMessagingHosts(string installDir)
{
    var chromiumManifest = Path.Combine(installDir, "native-hosts", "chromium-com.monitoraudiorouter.router.json");
    var firefoxManifest = Path.Combine(installDir, "native-hosts", "firefox-com.monitoraudiorouter.router.json");
    var softwareRoots = Environment.Is64BitOperatingSystem
        ? new[] { "Software", @"Software\WOW6432Node" }
        : new[] { "Software" };

    foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
    {
        foreach (var softwareRoot in softwareRoots)
        {
            SetDefaultValue(hive, $@"{softwareRoot}\Google\Chrome\NativeMessagingHosts\{HostName}", chromiumManifest);
            SetDefaultValue(hive, $@"{softwareRoot}\Chromium\NativeMessagingHosts\{HostName}", chromiumManifest);
            SetDefaultValue(hive, $@"{softwareRoot}\Microsoft\Edge\NativeMessagingHosts\{HostName}", chromiumManifest);
            SetDefaultValue(hive, $@"{softwareRoot}\Mozilla\NativeMessagingHosts\{HostName}", firefoxManifest);
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

static BrowserExtensionDeploymentResult RegisterBrowserExtensionPolicies(InstallerOptions options)
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
                @"Software\Policies\Google\Chrome\ExtensionInstallForcelist",
                options.ChromeExtensionId,
                ChromeWebStoreUpdateUrl),
            () => result.ChromePolicyInstalled = true,
            () => result.ChromePolicyFailed = true);
        installedAny |= TryRegisterPolicy(
            "Chromium extension policy",
            () => AddExtensionForcelistEntry(
                Registry.LocalMachine,
                @"Software\Policies\Chromium\ExtensionInstallForcelist",
                options.ChromeExtensionId,
                ChromeWebStoreUpdateUrl),
            () => result.ChromiumPolicyInstalled = true,
            () => result.ChromiumPolicyFailed = true);
    }

    if (HasPublishedValue(options.EdgeExtensionId))
    {
        installedAny |= TryRegisterPolicy(
            "Edge extension policy",
            () => AddExtensionForcelistEntry(
                Registry.LocalMachine,
                @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
                options.EdgeExtensionId,
                EdgeAddOnsUpdateUrl),
            () => result.EdgePolicyInstalled = true,
            () => result.EdgePolicyFailed = true);
    }

    if (HasPublishedValue(options.FirefoxExtensionId) && HasPublishedValue(options.FirefoxInstallUrl))
    {
        installedAny |= TryRegisterPolicy(
            "Firefox extension policy",
            () => SetFirefoxExtensionPolicy(options.FirefoxExtensionId, options.FirefoxInstallUrl, options.EnablePrivateBrowsing),
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

static void AddExtensionForcelistEntry(RegistryKey hive, string subKey, string extensionId, string updateUrl)
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
            key.SetValue(valueName, entry, RegistryValueKind.String);
            return;
        }
    }

    var index = 1;
    while (key.GetValue(index.ToString()) is not null)
    {
        index++;
    }

    key.SetValue(index.ToString(), entry, RegistryValueKind.String);
}

static void SetFirefoxExtensionPolicy(string extensionId, string installUrl, bool enablePrivateBrowsing)
{
    using var key = Registry.LocalMachine.CreateSubKey(@"Software\Policies\Mozilla\Firefox", writable: true);
    if (key is null)
    {
        return;
    }

    var settings = ParseJsonObject(key.GetValue("ExtensionSettings")?.ToString());
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
    key.SetValue("ExtensionSettings", settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), RegistryValueKind.String);
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

static void SetStartup(string installDir, bool enabled)
{
    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
    if (key is null)
    {
        return;
    }

    if (enabled)
    {
        key.SetValue(RunValueName, Quote(Path.Combine(installDir, "MonitorAudioRouter.exe")), RegistryValueKind.String);
    }
    else
    {
        key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}

static void WriteUserAutostartSetting(bool enabled)
{
    try
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDir = Path.Combine(localAppData, AppName);
        Directory.CreateDirectory(appDataDir);
        var configPath = Path.Combine(appDataDir, "config.json");
        var settings = ParseJsonObject(File.Exists(configPath) ? File.ReadAllText(configPath) : null);
        settings["AutostartEnabled"] = enabled;
        File.WriteAllText(configPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    catch
    {
        // The tray app can still manage autostart if the config sync fails.
    }
}

static void RegisterUninstaller(string installDir)
{
    using var key = Registry.LocalMachine.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}");
    if (key is null)
    {
        return;
    }

    var uninstallCommand = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Quote(Path.Combine(installDir, "Uninstall-MonitorAudioRouter.ps1"))}";
    key.SetValue("DisplayName", AppName, RegistryValueKind.String);
    key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
    key.SetValue("Publisher", "Local", RegistryValueKind.String);
    key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
    key.SetValue("DisplayIcon", Path.Combine(installDir, "MonitorAudioRouter.exe"), RegistryValueKind.String);
    key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
    key.SetValue("QuietUninstallString", uninstallCommand + " -Quiet", RegistryValueKind.String);
}

static void InstallStartMenuShortcut(string installDir)
{
    try
    {
        var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programsDir);
        var shortcutPath = Path.Combine(programsDir, $"{AppName}.lnk");
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
    }
    catch
    {
        // Shortcut creation should not block the core install.
    }
}

static void WriteInstallInfo(string installDir, InstallerOptions options)
{
    var installInfo = new
    {
        ChromeExtensionIds = HasPublishedValue(options.ChromeExtensionId) ? new[] { options.ChromeExtensionId } : Array.Empty<string>(),
        EdgeExtensionIds = HasPublishedValue(options.EdgeExtensionId) ? new[] { options.EdgeExtensionId } : Array.Empty<string>(),
        FirefoxExtensionIds = HasPublishedValue(options.FirefoxExtensionId) ? new[] { options.FirefoxExtensionId } : Array.Empty<string>(),
        PrivateBrowsingEnabled = options.EnablePrivateBrowsing
    };
    File.WriteAllText(
        Path.Combine(installDir, "install-info.json"),
        JsonSerializer.Serialize(installInfo, new JsonSerializerOptions { WriteIndented = true }));
}

static void SetDefaultValue(RegistryKey hive, string subKey, string value)
{
    using var key = hive.CreateSubKey(subKey);
    key?.SetValue(null, value, RegistryValueKind.String);
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
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
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
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
        // Temporary extraction can be cleaned later by Windows if a file is still held open.
    }
}

static bool TryLaunchNewerInstaller(InstallerOptions options)
{
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

        var updateDir = Path.Combine(Path.GetTempPath(), AppId + "-latest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDir);
        var setupPath = Path.Combine(updateDir, SetupAssetName);
        var checksumsPath = Path.Combine(updateDir, ChecksumsAssetName);

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

        Process.Start(new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = updateDir,
            Arguments = BuildForwardedInstallerArguments(options)
        });
        return true;
    }
    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
    {
        MessageBox.Show(
            "The latest installer was canceled. This installer will continue.",
            "Monitor Audio Router Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return false;
    }
    catch (Exception ex)
    {
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

static async Task<ReleaseInfo> GetLatestReleaseAsync(HttpClient httpClient)
{
    using var response = await httpClient.GetAsync(LatestReleaseApiUrl);
    response.EnsureSuccessStatusCode();
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
    var tempPath = destinationPath + ".download";
    File.Delete(tempPath);
    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
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

static string BuildForwardedInstallerArguments(InstallerOptions options)
{
    var args = new List<string>
    {
        "/nooptions",
        "/noupdatetolatest",
        options.InstallBrowserExtensions ? "/browserextensions" : "/nobrowserextensions",
        options.Autostart ? "/autostart" : "/noautostart"
    };

    if (!options.Launch)
    {
        args.Add("/nolaunch");
    }

    if (!options.OpenBrowserSetup)
    {
        args.Add("/nobrowsersetup");
    }

    if (options.EnablePrivateBrowsing)
    {
        args.Add("/enableprivatebrowsing");
    }

    AddOptionValue(args, "ChromeExtensionId", options.ChromeExtensionId);
    AddOptionValue(args, "EdgeExtensionId", options.EdgeExtensionId);
    AddOptionValue(args, "FirefoxExtensionId", options.FirefoxExtensionId);
    AddOptionValue(args, "FirefoxInstallUrl", options.FirefoxInstallUrl);

    return string.Join(" ", args.Select(QuoteArgument));
}

static void AddOptionValue(List<string> args, string name, string value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        args.Add($"/{name}={value}");
    }
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
        EdgeExtensionId: GetOptionValue(args, "EdgeExtensionId", DefaultEdgeExtensionId),
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
    string EdgeExtensionId,
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
