using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

const string AppName = "Monitor Audio Router";
const string AppId = "MonitorAudioRouter";
const string AppVersion = "0.1.6";
const string HostName = "com.monitoraudiorouter.router";
const string DefaultChromeExtensionId = "jnjminkakfohjeffdpeamngcnfneckog";
const string DefaultEdgeExtensionId = "";
const string DefaultFirefoxExtensionId = "monitor-audio-router@example.local";
const string DefaultFirefoxInstallUrl = "https://addons.mozilla.org/firefox/downloads/latest/monitor-audio-router-bridge/latest.xpi";
const string ChromeWebStoreListingUrl = "https://chromewebstore.google.com/detail/jnjminkakfohjeffdpeamngcnfneckog";
const string FirefoxAddOnsListingUrl = "https://addons.mozilla.org/en-US/firefox/addon/monitor-audio-router-bridge/";
const string ChromeWebStoreUpdateUrl = "https://clients2.google.com/service/update2/crx";
const string EdgeAddOnsUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";
const string RunValueName = "Monitor Audio Router";

var options = ParseOptions(args);
var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    AppName);

try
{
    var tempDir = Path.Combine(Path.GetTempPath(), AppId + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        ExtractPayload(tempDir);
        StopExistingApp(installDir);
        InstallFiles(tempDir, installDir);
        WriteNativeMessagingManifests(installDir, options);
        RegisterNativeMessagingHosts(installDir);
        var browserExtensionDeployment = RegisterBrowserExtensionPolicies(options);
        RegisterStartup(installDir);
        InstallStartMenuShortcut(installDir);
        WriteInstallInfo(installDir, options);
        RegisterUninstaller(installDir);

        if (options.Launch)
        {
            StartAppForUser(Path.Combine(installDir, "MonitorAudioRouter.exe"));
        }

        var openedExtensionPages = OpenBrowserExtensionPagesIfNeeded(options, browserExtensionDeployment);
        if (options.OpenBrowserSetup && !openedExtensionPages)
        {
            StartProcess(Path.Combine(installDir, "BrowserSetup.html"));
        }

        Console.WriteLine("Monitor Audio Router installed.");
    }
    finally
    {
        TryDeleteDirectory(tempDir);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Install failed:");
    Console.Error.WriteLine(ex);
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
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // Best effort only. File replacement below will fail if the app is still locked.
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
        using var process = Process.Start(new ProcessStartInfo(existingExe, "--clear-managed-routes")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit(5000);
    }
    catch
    {
        // Older builds may not have the cleanup command; upgrade can still continue.
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

    SetDefaultValue(Registry.LocalMachine, $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}", chromiumManifest);
    SetDefaultValue(Registry.LocalMachine, $@"Software\Chromium\NativeMessagingHosts\{HostName}", chromiumManifest);
    SetDefaultValue(Registry.LocalMachine, $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}", chromiumManifest);
    SetDefaultValue(Registry.LocalMachine, $@"Software\Mozilla\NativeMessagingHosts\{HostName}", firefoxManifest);
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

static void RegisterStartup(string installDir)
{
    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
    key?.SetValue(RunValueName, Quote(Path.Combine(installDir, "MonitorAudioRouter.exe")), RegistryValueKind.String);
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

static InstallerOptions ParseOptions(string[] args)
{
    return new InstallerOptions(
        Launch: !HasSwitch(args, "/nolaunch"),
        OpenBrowserSetup: !HasSwitch(args, "/nobrowsersetup"),
        InstallBrowserExtensions: !HasSwitch(args, "/nobrowserextensions"),
        EnablePrivateBrowsing: HasSwitch(args, "/enableprivatebrowsing") || HasSwitch(args, "/browserprivate"),
        ChromeExtensionId: GetOptionValue(args, "ChromeExtensionId", DefaultChromeExtensionId),
        EdgeExtensionId: GetOptionValue(args, "EdgeExtensionId", DefaultEdgeExtensionId),
        FirefoxExtensionId: GetOptionValue(args, "FirefoxExtensionId", DefaultFirefoxExtensionId),
        FirefoxInstallUrl: GetOptionValue(args, "FirefoxInstallUrl", DefaultFirefoxInstallUrl));
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
    bool InstallBrowserExtensions,
    bool EnablePrivateBrowsing,
    string ChromeExtensionId,
    string EdgeExtensionId,
    string FirefoxExtensionId,
    string FirefoxInstallUrl);

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
