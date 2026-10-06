using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorAudioRouter;

// The app is a small Windows tray process with one central loop:
// event watchers request scans, RoutingEngine decides the desired per-PID
// output device, and AppAudioPolicy applies that decision through Windows.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Paths.Initialize();

        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (args.Any(argument => argument.Equals("--native-host", StringComparison.OrdinalIgnoreCase)) ||
            executableName.Equals("MonitorAudioRouterNativeHost", StringComparison.OrdinalIgnoreCase))
        {
            NativeMessagingHost.Run();
            return 0;
        }
        if (args.Any(argument => argument.Equals("--list", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            CommandLineDiagnostics.ListSetupInfo();
            return 0;
        }

        if (args.Any(argument => argument.Equals("--list-audio-sessions", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            CommandLineDiagnostics.ListAudioSessions();
            return 0;
        }

        if (args.Any(argument => argument.Equals("--scan-once", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            var settings = SettingsStore.Load().Settings;
            using var engine = new RoutingEngine(settings);
            var result = engine.Scan();
            Console.WriteLine(result);
            return result.Success ? 0 : 1;
        }

        if (args.Any(argument => argument.Equals("--clear-managed-routes", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            var settings = SettingsStore.Load().Settings;
            using var engine = new RoutingEngine(settings);
            var result = engine.ClearManagedRoutes();
            Console.WriteLine(result);
            return result.Success ? 0 : 1;
        }

        if (TryGetIntArgument(args, "--clear-pid-route") is int processIdToClear)
        {
            NativeConsole.AttachToParent();
            using var policy = new AppAudioPolicy();
            if (!policy.IsAvailable)
            {
                Console.WriteLine("Policy backend unavailable. See router.log.");
                return 1;
            }

            var beforeClear = policy.GetPersistedEndpoint(processIdToClear);
            policy.ClearPersistedEndpoint(processIdToClear);
            var afterClear = policy.GetPersistedEndpoint(processIdToClear);
            Console.WriteLine($"PID {processIdToClear} before clear: {DescribePersistedEndpoint(beforeClear)}");
            Console.WriteLine($"PID {processIdToClear} after clear: {DescribePersistedEndpoint(afterClear)}");
            return afterClear.IsDefault ? 0 : 1;
        }

        if (args.Any(argument => argument.Equals("--probe-set-clear", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            using var devices = new AudioDeviceManager();
            using var policy = new AppAudioPolicy();
            if (!policy.IsAvailable)
            {
                Console.WriteLine("Policy backend unavailable. See router.log.");
                return 1;
            }

            var endpoint = devices.GetDefaultRenderEndpoint();
            if (endpoint is null)
            {
                Console.WriteLine("No default render endpoint found.");
                return 1;
            }

            var currentProcessId = Environment.ProcessId;
            var setSucceeded = policy.SetPersistedEndpoint(currentProcessId, endpoint.Id);
            var afterSet = policy.GetPersistedEndpoint(currentProcessId);
            var clearSucceeded = policy.ClearPersistedEndpoint(currentProcessId);
            var afterClear = policy.GetPersistedEndpoint(currentProcessId);
            Console.WriteLine($"Set current PID {currentProcessId} to default endpoint explicitly: {setSucceeded}; readback explicit={afterSet.HasExplicitEndpoint}");
            Console.WriteLine($"Cleared current PID {currentProcessId} back to Default: {clearSucceeded}; readback explicit={afterClear.HasExplicitEndpoint}");
            return afterSet.HasExplicitEndpoint && afterClear.IsDefault ? 0 : 1;
        }

        if (args.Any(argument => argument.Equals("--probe-tone-set-clear", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            using var devices = new AudioDeviceManager();
            using var policy = new AppAudioPolicy();
            if (!policy.IsAvailable)
            {
                Console.WriteLine("Policy backend unavailable. See router.log.");
                return 1;
            }

            var endpoint = devices.GetDefaultRenderEndpoint();
            if (endpoint is null)
            {
                Console.WriteLine("No default render endpoint found.");
                return 1;
            }

            using var stream = new MemoryStream(TestTone.GenerateWav());
            using var player = new SoundPlayer(stream);
            player.PlayLooping();
            Thread.Sleep(1000);

            var currentProcessId = Environment.ProcessId;
            var setSucceeded = policy.SetPersistedEndpoint(currentProcessId, endpoint.Id);
            var afterSet = policy.GetPersistedEndpoint(currentProcessId);
            var clearSucceeded = policy.ClearPersistedEndpoint(currentProcessId);
            var afterClear = policy.GetPersistedEndpoint(currentProcessId);
            player.Stop();

            Console.WriteLine($"Set audio-active PID {currentProcessId} to default endpoint explicitly: {setSucceeded}; readback explicit={afterSet.HasExplicitEndpoint}");
            Console.WriteLine($"Cleared audio-active PID {currentProcessId} back to Default: {clearSucceeded}; readback explicit={afterClear.HasExplicitEndpoint}");
            return afterSet.HasExplicitEndpoint && afterClear.IsDefault ? 0 : 1;
        }

        if (args.Any(argument => argument.Equals("--probe-policy", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            using var policy = new AppAudioPolicy();
            var currentProcessId = Environment.ProcessId;
            if (!policy.IsAvailable)
            {
                Console.WriteLine("Policy backend unavailable. See router.log.");
                return 1;
            }

            var endpoint = policy.GetPersistedEndpoint(currentProcessId);
            Console.WriteLine($"Policy query for PID {currentProcessId}: {DescribePersistedEndpoint(endpoint)}");
            return endpoint.Status == PersistedEndpointStatus.Unavailable ? 1 : 0;
        }

        Log.Write("Tray startup requested.");
        using var singleInstance = SingleInstanceLock.TryAcquire();
        if (!singleInstance.Acquired)
        {
            Log.Write("Another Monitor Audio Router tray instance is already running; duplicate instance exiting.");
            return 0;
        }

        Log.Write("Tray single-instance lock acquired.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new RouterTrayContext();
        Application.Run(context);
        Log.Write("Tray stopped.");
        return 0;
    }

    private static int? TryGetIntArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index + 1 < args.Length &&
                   int.TryParse(args[index + 1], out var value)
                ? value
                : null;
        }

        return null;
    }

    private static string DescribePersistedEndpoint(PersistedEndpoint endpoint)
    {
        return endpoint.Status switch
        {
            PersistedEndpointStatus.Default => "Default",
            PersistedEndpointStatus.Explicit => $"explicit endpoint {endpoint.EndpointId}",
            _ => "Unavailable"
        };
    }
}

internal static class Paths
{
    public static string Root { get; private set; } = GetUserRoot();
    public static string AppRoot => AppContext.BaseDirectory;
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string StateFile => Path.Combine(Root, "state.json");
    public static string LogFile => Path.Combine(Root, "router.log");
    public static string BrowserBridgeTokenFile => Path.Combine(Root, "browser-bridge.token");

    public static void Initialize()
    {
        Root = GetUserRoot();
        Directory.CreateDirectory(Root);
        MigrateIfMissing("config.json");
        MigrateIfMissing("state.json");
        MigrateIfMissing("router.log");
        MigrateIfMissing("browser-bridge.token");
        SettingsStore.EnsureConfigExists();
    }

    private static string GetUserRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Monitor Audio Router");
    }

    private static void MigrateIfMissing(string fileName)
    {
        var destination = Path.Combine(Root, fileName);
        if (File.Exists(destination))
        {
            return;
        }

        var source = Path.Combine(AppRoot, fileName);
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            File.Copy(source, destination);
        }
        catch (Exception exception)
        {
            Log.Write($"Could not migrate {fileName} to user data folder: {exception.Message}");
        }
    }
}

internal sealed class SingleInstanceLock : IDisposable
{
    private const string MutexPrefix = @"Local\MonitorAudioRouterTray";
    private readonly Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceLock(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool Acquired => _ownsMutex;

    public static SingleInstanceLock TryAcquire()
    {
        var mutexName = BuildMutexName();
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, mutexName);
            try
            {
                if (mutex.WaitOne(TimeSpan.Zero))
                {
                    return new SingleInstanceLock(mutex, ownsMutex: true);
                }
            }
            catch (AbandonedMutexException)
            {
                return new SingleInstanceLock(mutex, ownsMutex: true);
            }

            mutex.Dispose();
            return new SingleInstanceLock(null, ownsMutex: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SystemException)
        {
            mutex?.Dispose();
            Log.Write($"Could not acquire tray single-instance lock; exiting duplicate defensively: {exception.Message}");
            return new SingleInstanceLock(null, ownsMutex: false);
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Process shutdown should not be blocked by mutex cleanup.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }

    private static string BuildMutexName()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return $"{MutexPrefix}-{sid}";
            }
        }
        catch
        {
            // Fall back to the session-wide lock name.
        }

        return MutexPrefix;
    }
}

internal sealed class AboutForm : Form
{
    private const string GitHubUrl = "https://github.com/TechlyAccurate/MonitorAudioRouter";
    private const string LatestReleaseUrl = "https://github.com/TechlyAccurate/MonitorAudioRouter/releases/latest";
    private const string ChromeExtensionUrl = "https://chromewebstore.google.com/detail/jnjminkakfohjeffdpeamngcnfneckog";
    private const string FirefoxExtensionUrl = "https://addons.mozilla.org/en-US/firefox/addon/monitor-audio-router-bridge/";
    private readonly Button _updateButton;

    public AboutForm()
    {
        Text = "About Monitor Audio Router";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(500, 295);

        try
        {
            var iconPath = Path.Combine(Paths.AppRoot, "MonitorAudioRouter.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch
        {
            // The dialog is still usable without a window icon.
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Monitor Audio Router"
        }, 0, 0);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            Text = $"Current version: {AppUpdater.GetInstalledVersionText()}"
        }, 0, 1);

        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0)
        };
        links.Controls.Add(BuildLink("GitHub repository", GitHubUrl));
        links.Controls.Add(BuildLink("Latest release", LatestReleaseUrl));
        links.Controls.Add(BuildLink("Chrome companion extension", ChromeExtensionUrl));
        links.Controls.Add(BuildLink("Firefox companion extension", FirefoxExtensionUrl));
        root.Controls.Add(links, 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        var closeButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Close"
        };

        _updateButton = new Button
        {
            AutoSize = true,
            Text = "Check for update"
        };
        _updateButton.Click += async (_, _) => await CheckForUpdateAsync();

        actions.Controls.Add(closeButton);
        actions.Controls.Add(_updateButton);
        root.Controls.Add(actions, 0, 4);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.Add(root);
    }

    private async Task CheckForUpdateAsync()
    {
        _updateButton.Enabled = false;
        try
        {
            await AppUpdater.CheckAndInstallLatestAsync(this);
        }
        finally
        {
            if (!IsDisposed)
            {
                _updateButton.Enabled = true;
            }
        }
    }

    private static LinkLabel BuildLink(string text, string url)
    {
        var link = new LinkLabel
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Text = text,
            Tag = url
        };
        link.LinkClicked += (_, args) =>
        {
            if (args.Link?.LinkData is string linkUrl)
            {
                OpenUrl(linkUrl);
            }
        };
        link.Links.Clear();
        link.Links.Add(0, text.Length, url);
        return link;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Write($"Could not open URL {url}: {exception}");
            MessageBox.Show(exception.Message, "Could not open link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

internal sealed class RouterTrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayResourceLifecycle<ContextMenuStrip, Icon> _trayResources;
    private readonly ToolStripMenuItem _enabledMenuItem;
    private readonly ToolStripMenuItem _autostartMenuItem;
    private readonly Control _dispatcher;
    private readonly ScanScheduler _scanScheduler;
    private readonly BrowserHintServer _hintServer;
    private readonly WindowEventWatcher _windowEventWatcher;
    private readonly DisplayEventWatcher _displayEventWatcher;
    private readonly PowerEventWatcher _powerEventWatcher;
    private readonly AudioEventWatcher _audioEventWatcher;
    private RoutingEngine _engine;
    private RouterSettings _settings;
    private bool _scanRunning;
    private string _lastStatus = "Starting";

    public RouterTrayContext()
    {
        var settingsLoad = SettingsStore.Load();
        _settings = settingsLoad.Settings;
        _engine = new RoutingEngine(_settings);
        if (TrayLifecycleDecisions.ShouldApplyAutostart(settingsLoad))
        {
            ApplyAutostartSetting();
        }
        else
        {
            _lastStatus = settingsLoad.ErrorMessage ?? "Configuration is invalid";
        }

        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _enabledMenuItem = new ToolStripMenuItem("Enabled")
        {
            CheckOnClick = false
        };
        _enabledMenuItem.Click += (_, _) => ToggleEnabled();
        _autostartMenuItem = new ToolStripMenuItem("Autostart")
        {
            CheckOnClick = false
        };
        _autostartMenuItem.Click += (_, _) => ToggleAutostart();
        _trayResources = new TrayResourceLifecycle<ContextMenuStrip, Icon>(
            _settings.Enabled,
            BuildMenu,
            TrayIconFactory.Create,
            menu => menu.Dispose(),
            icon => icon.Dispose());
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayResources.Icon,
            Text = BuildTooltip(),
            Visible = true,
            ContextMenuStrip = _trayResources.Menu
        };
        RefreshTray();

        _scanScheduler = new ScanScheduler(
            _dispatcher,
            reason => Scan(reason),
            Math.Max(500, _settings.PollMilliseconds));
        _hintServer = new BrowserHintServer(reason => _scanScheduler.RequestBurst(reason));
        _windowEventWatcher = new WindowEventWatcher(reason => _scanScheduler.RequestBurst(reason));
        _displayEventWatcher = new DisplayEventWatcher(reason => _scanScheduler.RequestBurst(reason));
        _powerEventWatcher = new PowerEventWatcher(reason =>
        {
            _engine.HoldManagedRoutes(TimeSpan.FromSeconds(20), reason);
            _scanScheduler.RequestBurst(reason);
        });
        _audioEventWatcher = new AudioEventWatcher(
            reason => _scanScheduler.RequestBurst(reason),
            cleanup =>
            {
                _dispatcher.BeginInvoke(cleanup);
            });

        _hintServer.Start();
        _windowEventWatcher.Start();
        _displayEventWatcher.Start();
        _powerEventWatcher.Start();
        _audioEventWatcher.Start();
        _scanScheduler.Start();

        if (settingsLoad.Status == SettingsLoadStatus.Invalid)
        {
            MessageBox.Show(
                settingsLoad.ErrorMessage,
                "Could not load config",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var scanItem = new ToolStripMenuItem("Scan now");
        scanItem.Click += (_, _) => Scan("manual", force: true);

        var reloadItem = new ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, _) => ReloadConfig();

        var configureRoutesItem = new ToolStripMenuItem("Open config");
        configureRoutesItem.Click += (_, _) => ConfigureRoutes();

        var showInfoItem = new ToolStripMenuItem("Show setup info");
        showInfoItem.Click += (_, _) => MessageBox.Show(CommandLineDiagnostics.BuildSetupInfo(), "Monitor Audio Router", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var aboutItem = new ToolStripMenuItem("About");
        aboutItem.Click += (_, _) => ShowAbout();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(_autostartMenuItem);
        menu.Items.Add(scanItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(configureRoutesItem);
        menu.Items.Add(showInfoItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        SettingsStore.Save(_settings);
        if (!_settings.Enabled)
        {
            var result = _engine.ClearManagedRoutes();
            _lastStatus = result.ToString();
        }
        else
        {
            _lastStatus = "Enabled";
            _scanScheduler.RequestBurst("router enabled");
        }

        RefreshTray();
    }

    private void ToggleAutostart()
    {
        var desired = !_settings.AutostartEnabled;
        try
        {
            StartupRegistration.SetEnabled(desired);
            _settings.AutostartEnabled = desired;
            SettingsStore.Save(_settings);
            _lastStatus = desired ? "Autostart enabled" : "Autostart disabled";
        }
        catch (Exception exception)
        {
            _lastStatus = $"Autostart update failed: {exception.Message}";
            Log.Write($"Autostart update failed: {exception}");
            MessageBox.Show(exception.Message, "Could not update autostart", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshTray();
    }

    private void ApplyAutostartSetting()
    {
        try
        {
            StartupRegistration.SetEnabled(_settings.AutostartEnabled);
        }
        catch (Exception exception)
        {
            _lastStatus = $"Autostart update failed: {exception.Message}";
            Log.Write($"Autostart update failed: {exception}");
        }
    }

    private bool ReloadConfig()
    {
        var settingsLoad = SettingsStore.Load();
        var oldEngine = _engine;
        var wasEnabled = _settings.Enabled;
        var reloadResult = TrayLifecycleDecisions.ApplyEngineReload(
            wasEnabled,
            settingsLoad.Settings.Enabled,
            clearManagedRoutes: oldEngine.ClearManagedRoutes,
            disposeEngine: oldEngine.Dispose,
            createEngine: () => _engine = new RoutingEngine(settingsLoad.Settings));
        if (!reloadResult.Applied)
        {
            _lastStatus = reloadResult.ErrorMessage ?? "Config reload failed";
            Log.Write($"Config reload rejected: {_lastStatus}");
            RefreshTray();
            MessageBox.Show(
                _lastStatus,
                "Could not reload config",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        _settings = settingsLoad.Settings;
        if (TrayLifecycleDecisions.ShouldApplyAutostart(settingsLoad))
        {
            ApplyAutostartSetting();
        }

        _scanScheduler.SetPassiveInterval(Math.Max(500, _settings.PollMilliseconds));
        _audioEventWatcher.RefreshSubscriptions("config reload");
        _lastStatus = settingsLoad.Status == SettingsLoadStatus.Invalid
            ? settingsLoad.ErrorMessage ?? "Configuration is invalid"
            : "Config reloaded";
        RefreshTray();
        _scanScheduler.RequestBurst("config reload");

        if (settingsLoad.Status == SettingsLoadStatus.Invalid)
        {
            MessageBox.Show(
                settingsLoad.ErrorMessage,
                "Could not load config",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return settingsLoad.Status != SettingsLoadStatus.Invalid;
    }

    private void ConfigureRoutes()
    {
        try
        {
            var settingsLoad = SettingsStore.Load();
            if (settingsLoad.Status == SettingsLoadStatus.Invalid)
            {
                throw new InvalidOperationException(settingsLoad.ErrorMessage);
            }

            using var form = new RouteConfigForm(settingsLoad.Settings);
            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            SettingsStore.Save(form.Settings);
            if (!ReloadConfig())
            {
                return;
            }

            _lastStatus = "Routes saved";
            RefreshTray();
            _scanScheduler.RequestBurst("routes saved");
        }
        catch (Exception exception)
        {
            Log.Write($"Route configuration failed: {exception}");
            MessageBox.Show(exception.Message, "Could not configure routes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void ShowAbout()
    {
        try
        {
            using var form = new AboutForm();
            form.ShowDialog();
        }
        catch (Exception exception)
        {
            Log.Write($"About dialog failed: {exception}");
            MessageBox.Show(exception.Message, "Could not open About", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Scan(string reason, bool force = false)
    {
        if (_scanRunning || (!_settings.Enabled && !force))
        {
            return;
        }

        _scanRunning = true;
        try
        {
            var result = _engine.Scan();
            _lastStatus = result.ToString();
            RefreshTray();
        }
        finally
        {
            _scanRunning = false;
        }
    }

    private void RefreshTray()
    {
        _trayResources.Refresh(
            _settings.Enabled,
            _ =>
            {
                _enabledMenuItem.Checked = _settings.Enabled;
                _autostartMenuItem.Checked = _settings.AutostartEnabled;
            },
            icon => _notifyIcon.Icon = icon);
        _notifyIcon.Text = BuildTooltip();
    }

    private string BuildTooltip()
    {
        var status = _settings.Enabled ? "enabled" : "disabled";
        var text = $"Monitor Audio Router ({status})\n{_lastStatus}";
        return text.Length > 120 ? text[..120] : text;
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Could not open file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void ExitThreadCore()
    {
        _scanScheduler.Dispose();
        _windowEventWatcher.Dispose();
        _displayEventWatcher.Dispose();
        _powerEventWatcher.Dispose();
        _audioEventWatcher.Dispose();
        _hintServer.Dispose();
        RestoreManagedRoutesForExit();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Icon = null;
        _notifyIcon.Dispose();
        _trayResources.Dispose();
        _dispatcher.Dispose();
        _engine.Dispose();
        base.ExitThreadCore();
    }

    private void RestoreManagedRoutesForExit()
    {
        try
        {
            var result = _engine.ClearManagedRoutes();
            Log.Write($"Exit cleanup: {result}");
        }
        catch (Exception exception)
        {
            Log.Write($"Exit cleanup failed: {exception}");
        }
    }
}

internal sealed class TrayResourceLifecycle<TMenu, TIcon> : IDisposable
    where TMenu : class
    where TIcon : class
{
    private readonly Func<bool, TIcon> _createIcon;
    private readonly Action<TMenu> _disposeMenu;
    private readonly Action<TIcon> _disposeIcon;
    private bool _displayedEnabled;
    private bool _disposed;

    public TMenu Menu { get; }
    public TIcon Icon { get; private set; }

    public TrayResourceLifecycle(
        bool initialEnabled,
        Func<TMenu> createMenu,
        Func<bool, TIcon> createIcon,
        Action<TMenu> disposeMenu,
        Action<TIcon> disposeIcon)
    {
        _displayedEnabled = initialEnabled;
        _createIcon = createIcon;
        _disposeMenu = disposeMenu;
        _disposeIcon = disposeIcon;
        Menu = createMenu();
        Icon = createIcon(initialEnabled);
    }

    public void Refresh(bool enabled, Action<TMenu> updateMenu, Action<TIcon> attachIcon)
    {
        updateMenu(Menu);
        if (enabled == _displayedEnabled)
        {
            return;
        }

        var replacement = _createIcon(enabled);
        try
        {
            attachIcon(replacement);
        }
        catch
        {
            _disposeIcon(replacement);
            throw;
        }

        var superseded = Icon;
        Icon = replacement;
        _displayedEnabled = enabled;
        _disposeIcon(superseded);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeIcon(Icon);
        _disposeMenu(Menu);
    }
}

internal sealed record EngineReloadResult(bool Applied, string? ErrorMessage)
{
    public static EngineReloadResult Success { get; } = new(true, null);

    public static EngineReloadResult Failure(string errorMessage)
    {
        return new EngineReloadResult(false, errorMessage);
    }
}

internal static class TrayLifecycleDecisions
{
    public static bool ShouldApplyAutostart(SettingsLoadResult settingsLoad)
    {
        return settingsLoad.Status != SettingsLoadStatus.Invalid;
    }

    public static EngineReloadResult ApplyEngineReload(
        bool wasEnabled,
        bool isEnabled,
        Func<ScanResult> clearManagedRoutes,
        Action disposeEngine,
        Action createEngine)
    {
        if (wasEnabled && !isEnabled)
        {
            ScanResult cleanupResult;
            try
            {
                cleanupResult = clearManagedRoutes();
            }
            catch (Exception exception)
            {
                return EngineReloadResult.Failure(
                    $"Could not disable routing because managed-route cleanup failed: {exception.Message}");
            }

            if (!cleanupResult.Success)
            {
                return EngineReloadResult.Failure(
                    $"Could not disable routing because managed-route cleanup was unsuccessful: {cleanupResult}");
            }
        }

        disposeEngine();
        createEngine();
        return EngineReloadResult.Success;
    }
}

internal sealed class RouteConfigForm : Form
{
    private readonly RouterSettings _settings;
    private readonly List<MonitorInfo> _monitors;
    private readonly List<AudioEndpoint> _endpoints;
    private readonly List<RouteConfigRow> _rows = new();

    public RouterSettings Settings => _settings;

    public RouteConfigForm(RouterSettings settings)
    {
        _settings = settings;
        _monitors = WindowInspector.GetMonitors()
            .OrderByDescending(monitor => monitor.Primary)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var devices = new AudioDeviceManager();
        _endpoints = devices.GetRenderEndpoints()
            .OrderByDescending(endpoint => endpoint.IsDefault)
            .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Monitor Audio Routes";
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = true;
        MinimumSize = new Size(560, 420);
        Size = new Size(760, 640);
        try
        {
            var iconPath = Path.Combine(Paths.AppRoot, "MonitorAudioRouter.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch
        {
            // The config form is still usable without a window icon.
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(720, 0),
            Text = "Choose an audio device for each monitor. Leave a monitor set to Default to use the current Windows default playback device on that monitor."
        };
        root.Controls.Add(intro, 0, 0);

        var listPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(0, 10, 0, 10),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        listPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (_monitors.Count == 0)
        {
            listPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "No monitors were reported by Windows."
            });
        }
        else if (_endpoints.Count == 0)
        {
            listPanel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "No active render audio devices were reported by Windows."
            });
        }
        else
        {
            foreach (var monitor in _monitors)
            {
                var row = CreateMonitorRow(monitor);
                _rows.Add(row);
                listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                listPanel.Controls.Add(row.Container, 0, listPanel.RowCount++);
            }
        }

        root.Controls.Add(listPanel, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var viewJsonLink = new LinkLabel
        {
            Text = "View config JSON",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 0, 0)
        };
        viewJsonLink.LinkClicked += (_, _) => OpenConfigJson();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };

        var saveButton = new Button
        {
            Text = "Save",
            AutoSize = true,
            DialogResult = DialogResult.None
        };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };

        var autoDetectButton = new Button
        {
            Text = "Auto-detect",
            AutoSize = true
        };
        autoDetectButton.Click += (_, _) => AutoDetectRoutes();

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(autoDetectButton);
        footer.Controls.Add(viewJsonLink, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(footer, 0, 2);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    private RouteConfigRow CreateMonitorRow(MonitorInfo monitor)
    {
        var group = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(10),
            Text = BuildMonitorTitle(monitor)
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"Monitor ID: {DisplayValue(monitor.DeviceId)}"
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"Windows display: {monitor.DeviceName}; bounds {monitor.BoundsKey}"
        }, 0, 1);

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
            IntegralHeight = false,
            MaxDropDownItems = 12
        };
        foreach (var choice in BuildAudioChoices())
        {
            combo.Items.Add(choice);
        }

        SelectConfiguredChoice(combo, monitor);
        layout.Controls.Add(combo, 0, 2);

        var endpointId = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 3, 0, 0)
        };
        combo.SelectedIndexChanged += (_, _) => endpointId.Text = BuildEndpointDetail(combo.SelectedItem as AudioDeviceChoice);
        endpointId.Text = BuildEndpointDetail(combo.SelectedItem as AudioDeviceChoice);
        layout.Controls.Add(endpointId, 0, 3);

        group.Controls.Add(layout);
        return new RouteConfigRow(monitor, group, combo);
    }

    private IEnumerable<AudioDeviceChoice> BuildAudioChoices()
    {
        var defaultEndpoint = _endpoints.FirstOrDefault(endpoint => endpoint.IsDefault);
        var defaultLabel = defaultEndpoint is null
            ? "Default"
            : $"Default (current system default: {defaultEndpoint.Name})";
        yield return AudioDeviceChoice.SystemDefault(defaultLabel);

        foreach (var endpoint in _endpoints)
        {
            yield return AudioDeviceChoice.ForEndpoint(endpoint);
        }
    }

    private void SelectConfiguredChoice(ComboBox combo, MonitorInfo monitor)
    {
        var route = _settings.MonitorRoutes.FirstOrDefault(route => route.Matches(monitor));
        if (route is not null && !route.UsesSystemDefault)
        {
            var endpoint = route.FindEndpoint(_endpoints);
            if (endpoint is not null)
            {
                SelectEndpoint(combo, endpoint);
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static void SelectEndpoint(ComboBox combo, AudioEndpoint endpoint)
    {
        for (var itemIndex = 0; itemIndex < combo.Items.Count; itemIndex++)
        {
            if (combo.Items[itemIndex] is AudioDeviceChoice choice &&
                choice.Endpoint is not null &&
                string.Equals(choice.Endpoint.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = itemIndex;
                return;
            }
        }
    }

    private void AutoDetectRoutes()
    {
        var changed = 0;
        foreach (var row in _rows)
        {
            var endpoint = MonitorAudioAutoDetector.FindBestEndpoint(row.Monitor, _endpoints);
            if (endpoint is null)
            {
                continue;
            }

            SelectEndpoint(row.ComboBox, endpoint);
            changed++;
        }

        MessageBox.Show(
            changed == 0
                ? "No confident monitor/audio matches were found."
                : $"Auto-detected {changed} monitor/audio route{(changed == 1 ? "" : "s")}. Review the selections before saving.",
            "Auto-detect routes",
            MessageBoxButtons.OK,
            changed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.None);
    }

    private static void OpenConfigJson()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Paths.ConfigFile) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Could not open config JSON", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveAndClose()
    {
        var currentMonitorRoutes = new HashSet<MonitorRoute>();
        foreach (var route in _settings.MonitorRoutes)
        {
            if (_monitors.Any(route.Matches))
            {
                currentMonitorRoutes.Add(route);
            }
        }

        var newRoutes = _settings.MonitorRoutes
            .Where(route => !currentMonitorRoutes.Contains(route))
            .ToList();

        foreach (var row in _rows)
        {
            var choice = row.ComboBox.SelectedItem as AudioDeviceChoice;
            newRoutes.Add(BuildRoute(row.Monitor, choice?.Endpoint));
        }

        _settings.MonitorRoutes = newRoutes;
        DialogResult = DialogResult.OK;
        Close();
    }

    private MonitorRoute BuildRoute(MonitorInfo monitor, AudioEndpoint? endpoint)
    {
        var route = new MonitorRoute();
        var monitorId = BuildMonitorRouteId(monitor);
        if (!string.IsNullOrWhiteSpace(monitorId))
        {
            route.MonitorDeviceIdContains = monitorId;
        }
        else if (!string.IsNullOrWhiteSpace(monitor.FriendlyName))
        {
            route.MonitorFriendlyNameContains = monitor.FriendlyName;
        }
        else if (!string.IsNullOrWhiteSpace(monitor.DeviceName))
        {
            route.MonitorDeviceNameContains = monitor.DeviceName;
        }
        else
        {
            route.MonitorBounds = monitor.BoundsKey;
        }

        if (endpoint is not null)
        {
            route.AudioDeviceIdContains = endpoint.Id;
            route.AudioDeviceNameContains = endpoint.Name;
        }

        return route;
    }

    private string? BuildMonitorRouteId(MonitorInfo monitor)
    {
        var stableId = GetStableMonitorDeviceId(monitor.DeviceId);
        if (string.IsNullOrWhiteSpace(stableId))
        {
            return null;
        }

        var duplicateCount = _monitors.Count(monitor =>
            string.Equals(GetStableMonitorDeviceId(monitor.DeviceId), stableId, StringComparison.OrdinalIgnoreCase));
        return duplicateCount <= 1 ? stableId : monitor.DeviceId.Trim();
    }

    private static string? GetStableMonitorDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var trimmed = deviceId.Trim();
        var parts = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("MONITOR", StringComparison.OrdinalIgnoreCase))
        {
            return parts[0] + "\\" + parts[1];
        }

        return trimmed;
    }

    private static string BuildMonitorTitle(MonitorInfo monitor)
    {
        var name = string.IsNullOrWhiteSpace(monitor.FriendlyName) ? monitor.DeviceName : monitor.FriendlyName;
        return monitor.Primary ? $"{name} (primary)" : name;
    }

    private static string DisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unreported>" : value;
    }

    private static string BuildEndpointDetail(AudioDeviceChoice? choice)
    {
        if (choice?.Endpoint is null)
        {
            return "No per-app audio override will be set for this monitor.";
        }

        return $"Audio device ID: {choice.Endpoint.Id}";
    }

    private sealed record RouteConfigRow(MonitorInfo Monitor, Control Container, ComboBox ComboBox);

    private sealed class AudioDeviceChoice
    {
        private readonly string _label;

        private AudioDeviceChoice(AudioEndpoint? endpoint, string label)
        {
            Endpoint = endpoint;
            _label = label;
        }

        public AudioEndpoint? Endpoint { get; }

        public static AudioDeviceChoice SystemDefault(string label)
        {
            return new AudioDeviceChoice(null, label);
        }

        public static AudioDeviceChoice ForEndpoint(AudioEndpoint endpoint)
        {
            var label = endpoint.IsDefault ? $"{endpoint.Name} (current system default)" : endpoint.Name;
            return new AudioDeviceChoice(endpoint, label);
        }

        public override string ToString()
        {
            return _label;
        }
    }
}

internal static class AppUpdater
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/TechlyAccurate/MonitorAudioRouter/releases/latest";
    private const string SetupAssetName = "MonitorAudioRouterSetup.exe";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";

    public static string GetInstalledVersionText()
    {
        var version = GetCurrentVersion();
        return version is null ? "unknown" : FormatVersion(version);
    }

    public static async Task CheckAndInstallLatestAsync(IWin32Window owner)
    {
        try
        {
            using var httpClient = CreateHttpClient();
            var release = await GetLatestReleaseAsync(httpClient);
            var currentVersion = GetCurrentVersion();
            var latestVersion = ParseVersion(release.TagName);

            if (latestVersion is not null &&
                currentVersion is not null &&
                CompareVersions(latestVersion, currentVersion) <= 0)
            {
                var statusLine = CompareVersions(latestVersion, currentVersion) < 0
                    ? "This installed build is newer than the latest published release."
                    : "Monitor Audio Router is up to date.";
                MessageBox.Show(
                    owner,
                    $"{statusLine}\n\nInstalled version: {FormatVersion(currentVersion)}\nLatest release: {release.TagName}",
                    "App update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var prompt = currentVersion is null
                ? $"Latest release {release.TagName} is available. Download, verify, and run the installer now?"
                : $"Latest release {release.TagName} is available.\n\nInstalled version: {FormatVersion(currentVersion)}\n\nDownload, verify, and run the installer now?";
            if (MessageBox.Show(owner, prompt, "App update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            var updateDir = Path.Combine(Paths.Root, "updates", SanitizePathPart(release.TagName));
            Directory.CreateDirectory(updateDir);
            var setupPath = Path.Combine(updateDir, SetupAssetName);
            var checksumsPath = Path.Combine(updateDir, ChecksumsAssetName);

            await DownloadFileAsync(httpClient, release.ChecksumsDownloadUrl, checksumsPath);
            await DownloadFileAsync(httpClient, release.SetupDownloadUrl, setupPath);

            var expectedHash = ReadExpectedHash(checksumsPath, SetupAssetName);
            if (expectedHash is null)
            {
                throw new InvalidOperationException($"The release checksum file does not include {SetupAssetName}.");
            }

            var actualHash = ComputeSha256(setupPath);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The downloaded installer did not match the release checksum.");
            }

            Log.Write($"Verified update installer {release.TagName}: {SetupAssetName} sha256 {actualHash}.");
            MessageBox.Show(
                owner,
                "The installer was downloaded and verified. Windows will ask for permission to install the update.",
                "App update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            var settingsLoad = SettingsStore.Load();
            if (settingsLoad.Status == SettingsLoadStatus.Invalid)
            {
                throw new InvalidOperationException(settingsLoad.ErrorMessage);
            }

            var installerArgs = settingsLoad.Settings.AutostartEnabled
                ? "/nobrowsersetup /nooptions /noupdatetolatest /autostart"
                : "/nobrowsersetup /nooptions /noupdatetolatest /noautostart";
            Process.Start(new ProcessStartInfo(setupPath, installerArgs)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = updateDir
            });
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Log.Write("App update canceled at the Windows permission prompt.");
            MessageBox.Show(owner, "The update was canceled.", "App update", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            Log.Write($"App update failed: {exception}");
            MessageBox.Show(owner, exception.Message, "Could not update app", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MonitorAudioRouter", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return httpClient;
    }

    private static async Task<ReleaseInfo> GetLatestReleaseAsync(HttpClient httpClient)
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

    private static string? FindAssetDownloadUrl(JsonElement releaseRoot, string assetName)
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

    private static async Task DownloadFileAsync(HttpClient httpClient, string url, string destinationPath)
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

    private static string? ReadExpectedHash(string checksumsPath, string assetName)
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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Version? GetCurrentVersion()
    {
        try
        {
            var path = Environment.ProcessPath ?? Application.ExecutablePath;
            return ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion);
        }
        catch
        {
            return typeof(Program).Assembly.GetName().Version;
        }
    }

    private static Version? ParseVersion(string? value)
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

    private static int CompareVersions(Version left, Version right)
    {
        var leftParts = new[] { left.Major, left.Minor, Math.Max(0, left.Build), Math.Max(0, left.Revision) };
        var rightParts = new[] { right.Major, right.Minor, Math.Max(0, right.Build), Math.Max(0, right.Revision) };
        for (var partIndex = 0; partIndex < leftParts.Length; partIndex++)
        {
            var comparison = leftParts[partIndex].CompareTo(rightParts[partIndex]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static string FormatVersion(Version version)
    {
        return version.Build >= 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
    }

    private static string SanitizePathPart(string value)
    {
        var invalidPathCharacters = Path.GetInvalidFileNameChars();
        var pathPartBuilder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            pathPartBuilder.Append(invalidPathCharacters.Contains(character) ? '_' : character);
        }

        return pathPartBuilder.Length == 0 ? "latest" : pathPartBuilder.ToString();
    }

    private sealed record ReleaseInfo(string TagName, string SetupDownloadUrl, string ChecksumsDownloadUrl);
}

internal static class MonitorAudioAutoDetector
{
    private const int MinimumConfidence = 45;
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIO",
        "DEFAULT",
        "DEFINITION",
        "DEVICE",
        "DIGITAL",
        "DISPLAY",
        "DISPLAYPORT",
        "GENERIC",
        "HEADPHONE",
        "HEADPHONES",
        "HDMI",
        "HIGH",
        "INPUT",
        "INTEL",
        "MICROPHONE",
        "MONITOR",
        "NVIDIA",
        "OUTPUT",
        "PNP",
        "REALTEK",
        "SPEAKER",
        "SPEAKERS",
        "USB"
    };

    public static AudioEndpoint? FindBestEndpoint(MonitorInfo monitor, List<AudioEndpoint> endpoints)
    {
        return endpoints
            .Select(endpoint => new { Endpoint = endpoint, Score = Score(monitor, endpoint) })
            .Where(match => match.Score >= MinimumConfidence)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Endpoint.IsDefault)
            .ThenBy(match => match.Endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Endpoint)
            .FirstOrDefault();
    }

    private static int Score(MonitorInfo monitor, AudioEndpoint endpoint)
    {
        var monitorText = $"{monitor.DeviceName} {monitor.FriendlyName} {monitor.DeviceId}";
        var endpointText = $"{endpoint.Name} {endpoint.Id}";
        var monitorTokens = Tokenize(monitorText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var endpointTokens = Tokenize(endpointText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitorCompact = Compact(monitorText);
        var endpointCompact = Compact(endpointText);
        var score = 0;

        foreach (var token in monitorTokens)
        {
            if (endpointTokens.Contains(token))
            {
                score += IsModelToken(token) ? 70 : 35;
            }
            else if (endpointCompact.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += IsModelToken(token) ? 55 : 20;
            }
        }

        foreach (var token in endpointTokens)
        {
            if (monitorTokens.Contains(token))
            {
                score += IsModelToken(token) ? 35 : 15;
            }
            else if (monitorCompact.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += IsModelToken(token) ? 25 : 10;
            }
        }

        var friendlyCompact = Compact(monitor.FriendlyName);
        if (friendlyCompact.Length >= 6 && endpointCompact.Contains(friendlyCompact, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var currentToken = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                currentToken.Append(char.ToUpperInvariant(character));
                continue;
            }

            foreach (var value in FlushToken(currentToken))
            {
                yield return value;
            }
        }

        foreach (var value in FlushToken(currentToken))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> FlushToken(StringBuilder tokenBuilder)
    {
        if (tokenBuilder.Length == 0)
        {
            yield break;
        }

        var tokenValue = tokenBuilder.ToString();
        tokenBuilder.Clear();
        foreach (var candidate in ExpandToken(tokenValue))
        {
            if (candidate.Length >= 3 && !NoiseTokens.Contains(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandToken(string token)
    {
        yield return token;

        var letters = new StringBuilder();
        var digits = new StringBuilder();
        foreach (var character in token)
        {
            if (char.IsLetter(character))
            {
                letters.Append(character);
            }
            else if (char.IsDigit(character))
            {
                digits.Append(character);
            }
        }

        if (letters.Length >= 3 && letters.Length != token.Length)
        {
            yield return letters.ToString();
        }

        if (digits.Length >= 4 && digits.Length != token.Length)
        {
            yield return digits.ToString();
        }
    }

    private static bool IsModelToken(string token)
    {
        return token.Length >= 4 && token.Any(char.IsLetter) && token.Any(char.IsDigit);
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var compactValueBuilder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                compactValueBuilder.Append(char.ToUpperInvariant(character));
            }
        }

        return compactValueBuilder.ToString();
    }
}

internal sealed class ScanScheduler : IDisposable
{
    private static readonly TimeSpan ImmediateDebounce = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MinimumScanGap = TimeSpan.FromMilliseconds(90);
    private static readonly int[] BurstDelaysMs = { 120, 250, 500, 1000, 1500 };

    private readonly Control _dispatcher;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Action<string> _scan;
    private int _passiveMilliseconds;
    private DateTimeOffset _lastScanUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDueUtc = DateTimeOffset.MaxValue;
    private string _nextReason = "startup";
    private int _burstIndex = int.MaxValue;
    private bool _disposed;

    public ScanScheduler(Control dispatcher, Action<string> scan, int passiveMilliseconds)
    {
        _dispatcher = dispatcher;
        _scan = scan;
        _passiveMilliseconds = Math.Max(500, passiveMilliseconds);
        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => OnTick();
    }

    public void Start()
    {
        Post(() => RequestBurstOnUiThread("startup"));
    }

    public void SetPassiveInterval(int passiveMilliseconds)
    {
        Post(() =>
        {
            _passiveMilliseconds = Math.Max(500, passiveMilliseconds);
            ScheduleAt(DateTimeOffset.UtcNow.AddMilliseconds(_passiveMilliseconds), "passive");
        });
    }

    public void RequestImmediate(string reason)
    {
        Post(() => RequestImmediateOnUiThread(reason));
    }

    public void RequestBurst(string reason)
    {
        Post(() => RequestBurstOnUiThread(reason));
    }

    private void RequestImmediateOnUiThread(string reason)
    {
        ScheduleAt(DateTimeOffset.UtcNow + ImmediateDebounce, reason);
    }

    private void RequestBurstOnUiThread(string reason)
    {
        _burstIndex = 0;
        RequestImmediateOnUiThread(reason);
    }

    private void OnTick()
    {
        _timer.Stop();
        var now = DateTimeOffset.UtcNow;
        if (now < _nextDueUtc)
        {
            ArmTimer();
            return;
        }

        var reason = _nextReason;
        _nextDueUtc = DateTimeOffset.MaxValue;
        _nextReason = "passive";
        _lastScanUtc = now;
        try
        {
            _scan(reason);
        }
        catch (Exception exception)
        {
            Log.WriteThrottled(
                "scan-scheduler-failed:" + exception.Message,
                $"Scheduled scan failed: {exception.Message}",
                TimeSpan.FromMinutes(1));
        }

        now = DateTimeOffset.UtcNow;
        if (_burstIndex < BurstDelaysMs.Length)
        {
            ScheduleAt(now.AddMilliseconds(BurstDelaysMs[_burstIndex++]), "burst");
            return;
        }

        ScheduleAt(now.AddMilliseconds(_passiveMilliseconds), "passive");
    }

    private void ScheduleAt(DateTimeOffset dueUtc, string reason)
    {
        if (_disposed)
        {
            return;
        }

        var earliest = _lastScanUtc + MinimumScanGap;
        if (dueUtc < earliest)
        {
            dueUtc = earliest;
        }

        if (_timer.Enabled && dueUtc >= _nextDueUtc)
        {
            return;
        }

        _nextDueUtc = dueUtc;
        _nextReason = reason;
        ArmTimer();
    }

    private void ArmTimer()
    {
        if (_disposed)
        {
            return;
        }

        var delay = _nextDueUtc - DateTimeOffset.UtcNow;
        var milliseconds = delay.TotalMilliseconds <= 1 ? 1 : Math.Min(int.MaxValue, (int)Math.Ceiling(delay.TotalMilliseconds));
        _timer.Interval = Math.Max(1, milliseconds);
        _timer.Stop();
        _timer.Start();
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.IsDisposed)
        {
            return;
        }

        try
        {
            if (_dispatcher.InvokeRequired)
            {
                _dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch
        {
            // Shutdown can race with late native callbacks.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}

internal static class BrowserBridgeProtocol
{
    public const int MaximumBrowserMessageBytes = 1024 * 1024;
    public const int MaximumTokenCharacters = 512;
    public const int MaximumSerializedCharacterExpansion = 6;
    public const int EnvelopeFixedCharacters = 54;

    // UnsafeRelaxedJsonEscaping can emit one six-character \uXXXX escape for
    // each input UTF-16 unit. A valid UTF-8 frame has no more UTF-16 units than
    // bytes. The token is bounded in UTF-16 units, and the fixed count is the
    // exact serialized envelope with empty token and payload values.
    public const int MaximumPipeMessageCharacters =
        (MaximumSerializedCharacterExpansion * MaximumBrowserMessageBytes) +
        (MaximumSerializedCharacterExpansion * MaximumTokenCharacters) +
        EnvelopeFixedCharacters;

    public static bool IsEnvelopeLengthAllowed(int characterCount)
    {
        return characterCount >= 0 && characterCount <= MaximumPipeMessageCharacters;
    }
}

internal sealed class BrowserHintServer : IDisposable
{
    public const string PipeName = "MonitorAudioRouterHints";
    private static readonly TimeSpan PipeReadTimeout = TimeSpan.FromSeconds(5);
    private readonly Action<string> _requestBurst;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public BrowserHintServer(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
    }

    public void Start()
    {
        _task = Task.Run(() => RunAsync(_cts.Token, _requestBurst));
        Log.Write($"Browser hint server listening on named pipe {PipeName}.");
    }

    private static async Task RunAsync(CancellationToken token, Action<string> requestBurst)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await ReadBoundedLineAsync(
                    reader,
                    BrowserBridgeProtocol.MaximumPipeMessageCharacters,
                    PipeReadTimeout,
                    token);
                if (line is null)
                {
                    continue;
                }

                var payload = BrowserBridgeSecurity.TryUnwrap(line);
                if (payload is null)
                {
                    Log.WriteThrottled(
                        "browser-hint-invalid-token",
                        "Rejected browser hint: invalid native-host bridge token.",
                        TimeSpan.FromMinutes(5));
                    continue;
                }

                if (BrowserHintStore.ApplyJson(payload))
                {
                    requestBurst("browser hint");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.Write($"Browser hint server error: {exception.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        int maximumCharacters,
        TimeSpan timeout,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        var result = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[1024];
        var pendingTerminalCarriageReturn = false;
        try
        {
            while (true)
            {
                var remainingCharacters = maximumCharacters - result.Length;
                var charactersToRead = Math.Min(buffer.Length, remainingCharacters + 1);
                var charactersRead = await reader.ReadAsync(
                    buffer.AsMemory(0, charactersToRead),
                    timeoutSource.Token);
                if (charactersRead == 0)
                {
                    if (result.Length == 0)
                    {
                        return null;
                    }

                    throw new EndOfStreamException(
                        "Browser hint pipe message ended before the line terminator.");
                }

                for (var index = 0; index < charactersRead; index++)
                {
                    var character = buffer[index];
                    if (pendingTerminalCarriageReturn)
                    {
                        if (character == '\n')
                        {
                            return result.ToString();
                        }

                        throw new InvalidDataException(
                            $"Browser hint pipe message exceeded the maximum of {maximumCharacters} characters.");
                    }

                    if (character == '\n')
                    {
                        if (result.Length > 0 && result[^1] == '\r')
                        {
                            result.Length--;
                        }

                        return result.ToString();
                    }

                    if (result.Length == maximumCharacters)
                    {
                        if (character == '\r')
                        {
                            pendingTerminalCarriageReturn = true;
                            continue;
                        }

                        throw new InvalidDataException(
                            $"Browser hint pipe message exceeded the maximum of {maximumCharacters} characters.");
                    }

                    result.Append(character);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Browser hint pipe message did not complete before the read timeout.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _task?.Wait(1000);
        }
        catch
        {
            // Shutdown should not block app exit.
        }

        _cts.Dispose();
    }
}

internal sealed class WindowEventWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjIdWindow = 0;
    private static readonly TimeSpan LocationThrottle = TimeSpan.FromMilliseconds(150);

    private readonly Action<string> _requestBurst;
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly List<IntPtr> _hooks = new();
    private DateTimeOffset _lastLocationEventUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public WindowEventWatcher(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        AddHook(EventSystemForeground, EventSystemForeground);
        AddHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd);
        AddHook(EventObjectShow, EventObjectHide);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
        Log.Write($"Window event watcher started with {_hooks.Count} hooks.");
    }

    private void AddHook(uint eventMin, uint eventMax)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || idObject != ObjIdWindow || idChild != 0)
        {
            return;
        }

        if (eventType == EventObjectLocationChange)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastLocationEventUtc < LocationThrottle)
            {
                return;
            }

            _lastLocationEventUtc = now;
        }

        _requestBurst(EventReason(eventType));
    }

    private static string EventReason(uint eventType)
    {
        return eventType switch
        {
            EventSystemForeground => "window foreground",
            EventSystemMoveSizeStart => "window move start",
            EventSystemMoveSizeEnd => "window move end",
            EventObjectShow => "window shown",
            EventObjectHide => "window hidden",
            EventObjectLocationChange => "window location",
            _ => "window event"
        };
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }
}

internal sealed class DisplayEventWatcher : IDisposable
{
    private readonly Action<string> _requestBurst;

    public DisplayEventWatcher(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
    }

    public void Start()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _requestBurst("display settings changed");
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}

internal sealed class PowerEventWatcher : IDisposable
{
    private readonly Action<string> _requestBurst;

    public PowerEventWatcher(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
    }

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _requestBurst("power resume");
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}

// Browser extensions can see audible tabs and browser window bounds, but they
// cannot directly set Windows audio routing. This store keeps those browser
// snapshots as short-lived hints that the routing engine later reconciles
// against native Windows windows and active Windows audio-session PIDs.
internal static class BrowserHintStore
{
    private const int MaxHintWindows = 32;
    private const int MaxProcessIdsPerWindow = 32;
    private const int MaxTitlesPerWindow = 16;
    private const int MaxTitleChars = 256;
    private const int MaxSourceInstanceIdChars = 128;
    private const int MaxOrderedSourceStates = 64;
    private static readonly TimeSpan HintStaleAfter = TimeSpan.FromSeconds(12);
    private static readonly object LockObject = new();
    private static readonly Dictionary<BrowserHintSourceKey, BrowserHintSourceState> SourceStates = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string> LastLoggedHintSignatures = new(StringComparer.OrdinalIgnoreCase);

    public static bool ApplyJson(string json)
    {
        return ApplyJson(json, DateTimeOffset.UtcNow);
    }

    internal static bool ApplyJson(string json, DateTimeOffset now)
    {
        try
        {
            var update = JsonSerializer.Deserialize<BrowserHintUpdate>(json, JsonOptions);
            if (update?.Type is null || !update.Type.Equals("audibleWindows", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var family = BrowserToFamily(update.Browser);
            if (family is null)
            {
                return false;
            }

            var hasSourceInstanceId = !string.IsNullOrWhiteSpace(update.SourceInstanceId);
            var hasSequence = update.Sequence.HasValue;
            if (hasSourceInstanceId != hasSequence)
            {
                return false;
            }

            var sourceInstanceId = hasSourceInstanceId ? update.SourceInstanceId!.Trim() : null;
            if (sourceInstanceId is not null &&
                (sourceInstanceId.Length > MaxSourceInstanceIdChars || update.Sequence <= 0))
            {
                return false;
            }

            // Extension payloads are local, but still treated as a boundary:
            // clamp counts and title lengths before saving anything in memory.
            var windows = (update.Windows ?? new List<BrowserHintWindowUpdate>())
                .Where(windowUpdate => windowUpdate.Width > 0 && windowUpdate.Height > 0)
                .Take(MaxHintWindows)
                .Select(windowUpdate => new BrowserHintWindow(
                    windowUpdate.WindowId,
                    new Rectangle(windowUpdate.Left, windowUpdate.Top, windowUpdate.Width, windowUpdate.Height),
                    (windowUpdate.ProcessIds ?? new List<int>())
                        .Where(processId => processId > 0)
                        .Distinct()
                        .Take(MaxProcessIdsPerWindow)
                        .ToList(),
                    (windowUpdate.Titles ?? new List<string>())
                        .Select(NormalizeHintTitle)
                        .Where(title => title.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(MaxTitlesPerWindow)
                        .ToList(),
                    (windowUpdate.WindowTitles ?? new List<string>())
                        .Select(NormalizeHintTitle)
                        .Where(title => title.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(MaxTitlesPerWindow)
                        .ToList()))
                .ToList();

            BrowserHintSet hintSet;
            lock (LockObject)
            {
                PruneStaleSourcesLocked(now);
                var sourceKey = new BrowserHintSourceKey(family.Value, sourceInstanceId);
                SourceStates.TryGetValue(sourceKey, out var previous);
                if (sourceInstanceId is not null &&
                    previous is not null &&
                    update.Sequence!.Value <= previous.Sequence!.Value)
                {
                    return false;
                }

                var preferredWindowId = DeterminePreferredWindowId(previous, windows);
                SourceStates[sourceKey] = new BrowserHintSourceState(
                    family.Value,
                    sourceInstanceId,
                    update.Sequence,
                    now,
                    windows,
                    preferredWindowId);
                if (sourceInstanceId is not null)
                {
                    EnforceOrderedSourceCapLocked(sourceKey);
                }

                hintSet = BuildHintSetLocked(family.Value);
            }

            return LogHintIfChanged(hintSet);
        }
        catch (Exception exception)
        {
            Log.WriteThrottled(
                "browser-hint-parse-error:" + exception.Message,
                $"Browser hint parse error: {ShortError(exception.Message)}",
                TimeSpan.FromMinutes(1));
            return false;
        }
    }

    public static Dictionary<string, BrowserHintSet> GetSnapshot()
    {
        return GetSnapshot(DateTimeOffset.UtcNow);
    }

    internal static Dictionary<string, BrowserHintSet> GetSnapshot(DateTimeOffset now)
    {
        lock (LockObject)
        {
            PruneStaleSourcesLocked(now);

            return SourceStates.Values
                .Select(state => state.Family)
                .Distinct()
                .ToDictionary(
                    BrowserFamilyProcessName,
                    BuildHintSetLocked,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool WindowMatchesHints(Dictionary<string, BrowserHintSet> hints, WindowInfo window)
    {
        var family = BrowserFamilyForProcessName(window.ProcessName);
        if (family is null)
        {
            return true;
        }

        if (!hints.TryGetValue(BrowserFamilyProcessName(family.Value), out var hintSet))
        {
            return false;
        }

        if (hintSet.Windows.Count == 0)
        {
            return false;
        }

        return hintSet.Windows.Any(hintWindow => WindowMatchesHint(hintWindow, window));
    }

    public static bool WindowMatchesHint(BrowserHintWindow hintWindow, WindowInfo window)
    {
        var titles = GetMatchTitles(hintWindow);
        if (titles.Count > 0)
        {
            return HintTitlesMatchWindow(window.Title, titles);
        }

        var hintMonitor = WindowInspector.PickMonitor(hintWindow.Bounds, WindowInspector.GetMonitors());
        return hintMonitor.BoundsKey.Equals(window.Monitor.BoundsKey, StringComparison.OrdinalIgnoreCase);
    }

    private static BrowserFamily? BrowserToFamily(string? browser)
    {
        return browser?.ToLowerInvariant() switch
        {
            "chrome" => BrowserFamily.Chromium,
            "edge" => BrowserFamily.Edge,
            "firefox" => BrowserFamily.Firefox,
            _ => null
        };
    }

    public static bool IsBrowserProcessName(string processName)
    {
        return BrowserFamilyForProcessName(processName) is not null;
    }

    internal static bool ProcessBelongsToFamily(BrowserFamily family, string processName)
    {
        return BrowserFamilyForProcessName(processName) == family;
    }

    internal static bool IsAdvisoryProcessMatch(
        string expectedProcessName,
        string actualProcessName,
        bool ownsRelevantAudioSession)
    {
        var family = BrowserFamilyForProcessName(expectedProcessName);
        return family is not null &&
               ownsRelevantAudioSession &&
               ProcessBelongsToFamily(family.Value, actualProcessName);
    }

    private static BrowserFamily? BrowserFamilyForProcessName(string processName)
    {
        if (processName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("chromium.exe", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase))
        {
            return BrowserFamily.Chromium;
        }

        if (processName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
        {
            return BrowserFamily.Edge;
        }

        return processName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
            ? BrowserFamily.Firefox
            : null;
    }

    private static string BrowserFamilyProcessName(BrowserFamily family)
    {
        return family switch
        {
            BrowserFamily.Chromium => "chrome.exe",
            BrowserFamily.Edge => "msedge.exe",
            BrowserFamily.Firefox => "firefox.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private static void PruneStaleSourcesLocked(DateTimeOffset now)
    {
        var cutoff = now - HintStaleAfter;
        foreach (var staleKey in SourceStates
                     .Where(entry => entry.Value.UpdatedUtc < cutoff)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            SourceStates.Remove(staleKey);
        }
    }

    private static void EnforceOrderedSourceCapLocked(BrowserHintSourceKey activeKey)
    {
        while (SourceStates.Count(entry => entry.Key.SourceInstanceId is not null) > MaxOrderedSourceStates)
        {
            var oldestKey = SourceStates
                .Where(entry => entry.Key.SourceInstanceId is not null && entry.Key != activeKey)
                .OrderBy(entry => entry.Value.UpdatedUtc)
                .ThenBy(entry => entry.Key.Family)
                .ThenBy(entry => entry.Key.SourceInstanceId, StringComparer.Ordinal)
                .Select(entry => (BrowserHintSourceKey?)entry.Key)
                .FirstOrDefault();
            if (oldestKey is null)
            {
                break;
            }

            SourceStates.Remove(oldestKey.Value);
        }
    }

    private static BrowserHintSet BuildHintSetLocked(BrowserFamily family)
    {
        var sources = SourceStates.Values
            .Where(state => state.Family == family)
            .OrderBy(state => state.SourceInstanceId is null ? 0 : 1)
            .ThenBy(state => state.SourceInstanceId, StringComparer.Ordinal)
            .Select(state => new BrowserHintSourceSnapshot(
                state.SourceInstanceId,
                state.UpdatedUtc,
                state.Windows,
                state.PreferredWindowId))
            .ToList();
        var windows = sources.SelectMany(source => source.Windows).ToList();
        var updatedUtc = sources.Max(source => source.UpdatedUtc);
        var preferredWindowId = sources.Count == 1 ? sources[0].PreferredWindowId : null;
        return new BrowserHintSet(
            BrowserFamilyProcessName(family),
            family,
            updatedUtc,
            windows,
            preferredWindowId,
            sources);
    }

    private static int? DeterminePreferredWindowId(
        BrowserHintSourceState? previous,
        List<BrowserHintWindow> windows)
    {
        // When a playing tab is moved to another browser window, Firefox and
        // Chromium can briefly report both the old and new windows as audible.
        // Prefer the newly added window during that overlap so audio moves fast.
        var currentIds = windows
            .Where(window => window.WindowId > 0)
            .Select(window => window.WindowId)
            .ToHashSet();
        if (currentIds.Count == 1)
        {
            return currentIds.Single();
        }

        if (currentIds.Count == 0 || previous is null)
        {
            return null;
        }

        var previousIds = previous.Windows
            .Where(window => window.WindowId > 0)
            .Select(window => window.WindowId)
            .ToHashSet();
        var addedIds = currentIds.Where(windowId => !previousIds.Contains(windowId)).ToList();
        if (addedIds.Count == 1)
        {
            return addedIds[0];
        }

        return previous.PreferredWindowId is int preferred &&
               currentIds.Contains(preferred)
            ? preferred
            : null;
    }

    private static bool LogHintIfChanged(BrowserHintSet hintSet)
    {
        var monitors = WindowInspector.GetMonitors();
        var signatureParts = hintSet.Sources
            .SelectMany((source, sourceIndex) => source.Windows.Select(hintWindow =>
            {
                var monitor = WindowInspector.PickMonitor(hintWindow.Bounds, monitors);
                return $"source={sourceIndex};preferred={source.PreferredWindowId?.ToString() ?? "none"};id={hintWindow.WindowId};{ShortBounds(hintWindow.Bounds)}>{monitor.BoundsKey};processIds={CompactProcessIdList(hintWindow.ProcessIds)};tabs={hintWindow.Titles.Count};active={hintWindow.WindowTitles.Count};{CreateTitleDiagnostic(hintWindow.Titles.Concat(hintWindow.WindowTitles))}";
            }));
        var signature = $"{hintSet.ProcessName}|sources={hintSet.Sources.Count}|{string.Join("|", signatureParts)}";
        lock (LockObject)
        {
            if (LastLoggedHintSignatures.TryGetValue(hintSet.ProcessName, out var previousSignature) &&
                string.Equals(previousSignature, signature, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            LastLoggedHintSignatures[hintSet.ProcessName] = signature;
        }

        var summary = string.Join("; ", hintSet.Windows.Select((hintWindow, index) =>
        {
            var monitor = WindowInspector.PickMonitor(hintWindow.Bounds, monitors);
            var titleDiagnostic = CreateTitleDiagnostic(hintWindow.Titles.Concat(hintWindow.WindowTitles));
            return $"w{index + 1}@{ShortMonitor(monitor)} bounds={ShortBounds(hintWindow.Bounds)} processIds={CompactProcessIdList(hintWindow.ProcessIds)} tabs={hintWindow.Titles.Count} {titleDiagnostic}";
        }));
        Log.Write(
            $"Browser hint: {hintSet.ProcessName} sources={hintSet.Sources.Count} windows={hintSet.Windows.Count}" +
            $"{(summary.Length == 0 ? "" : " " + summary)}");
        return true;
    }

    private static bool HintTitlesMatchWindow(string windowTitle, List<string> hintTitles)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        var normalizedWindowTitle = NormalizeTitle(windowTitle);
        return hintTitles.Any(title =>
        {
            var normalizedHintTitle = NormalizeTitle(title);
            return normalizedHintTitle.Length > 0 &&
                   (normalizedWindowTitle.Contains(normalizedHintTitle, StringComparison.OrdinalIgnoreCase) ||
                    normalizedHintTitle.Contains(normalizedWindowTitle, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static List<string> GetMatchTitles(BrowserHintWindow hintWindow)
    {
        return hintWindow.Titles
            .Concat(hintWindow.WindowTitles)
            .Select(title => title.Trim())
            .Where(title => title.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeTitle(string title)
    {
        title = title.Trim();
        foreach (var suffix in new[]
                 {
                     " — Firefox Developer Edition",
                     " - Firefox Developer Edition",
                     " — Mozilla Firefox",
                     " - Mozilla Firefox",
                     " — Firefox",
                     " - Firefox",
                     " - Google Chrome",
                     " - Chromium",
                     " - Microsoft Edge",
                     " - Brave",
                     " - Vivaldi"
                 })
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length].Trim();
                break;
            }
        }

        return title.Trim('\u200B', '\u200C', '\u200D', ' ');
    }

    private static string NormalizeHintTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        title = title.Trim();
        return title.Length <= MaxTitleChars ? title : title[..MaxTitleChars];
    }

    private static string ShortError(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 160 ? value : value[..160] + "...";
    }

    private static string ShortMonitor(MonitorInfo monitor)
    {
        var display = monitor.DeviceName.StartsWith("\\\\.\\", StringComparison.Ordinal)
            ? monitor.DeviceName[4..]
            : monitor.DeviceName;
        var idParts = monitor.DeviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var panelId = idParts.Length > 1 ? idParts[1] : "";
        var primary = monitor.Primary ? "*" : "";
        return string.IsNullOrWhiteSpace(panelId)
            ? $"{display}{primary}@{ShortBounds(monitor.Bounds)}"
            : $"{display}{primary}:{panelId}@{ShortBounds(monitor.Bounds)}";
    }

    private static string ShortBounds(Rectangle bounds)
    {
        return $"{bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}";
    }

    private static string CompactProcessIdList(List<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return "none";
        }

        var visibleProcessIds = processIds.Take(3).Select(processId => processId.ToString());
        return processIds.Count <= 3
            ? string.Join(",", visibleProcessIds)
            : string.Join(",", visibleProcessIds) + $"+{processIds.Count - 3}";
    }

    internal static string CreateTitleDiagnostic(IEnumerable<string> titles)
    {
        var normalizedTitles = titles
            .Select(NormalizeTitle)
            .Where(title => title.Length > 0)
            .GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(title => title, StringComparer.Ordinal).First())
            .OrderBy(title => title, StringComparer.Ordinal)
            .ToList();
        if (normalizedTitles.Count == 0)
        {
            return "titleCount=0 titleHash=none";
        }

        var joined = string.Join("\u001F", normalizedTitles);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..10].ToLowerInvariant();
        return $"titleCount={normalizedTitles.Count} titleHash={signature}";
    }

}

internal sealed class BrowserHintUpdate
{
    public string? Type { get; set; }
    public string? Browser { get; set; }
    public string? SourceInstanceId { get; set; }
    public long? Sequence { get; set; }
    public List<BrowserHintWindowUpdate>? Windows { get; set; }
}

internal sealed class BrowserHintWindowUpdate
{
    public int WindowId { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<int>? ProcessIds { get; set; }
    public List<string>? Titles { get; set; }
    public List<string>? WindowTitles { get; set; }
}

internal enum BrowserFamily
{
    Chromium,
    Edge,
    Firefox
}

internal readonly record struct BrowserHintSourceKey(
    BrowserFamily Family,
    string? SourceInstanceId);

internal sealed record BrowserHintSourceState(
    BrowserFamily Family,
    string? SourceInstanceId,
    long? Sequence,
    DateTimeOffset UpdatedUtc,
    List<BrowserHintWindow> Windows,
    int? PreferredWindowId);

internal sealed record BrowserHintSourceSnapshot(
    string? SourceInstanceId,
    DateTimeOffset UpdatedUtc,
    List<BrowserHintWindow> Windows,
    int? PreferredWindowId);

internal sealed record BrowserHintSet(
    string ProcessName,
    BrowserFamily Family,
    DateTimeOffset UpdatedUtc,
    List<BrowserHintWindow> Windows,
    int? PreferredWindowId,
    List<BrowserHintSourceSnapshot> Sources);

internal sealed record BrowserHintWindow(
    int WindowId,
    Rectangle Bounds,
    List<int> ProcessIds,
    List<string> Titles,
    List<string> WindowTitles);

internal static class BrowserBridgeSecurity
{
    private const string EnvelopeType = "browserHintEnvelope";
    private const int TokenByteCount = 32;
    private const string TokenMutexName = @"Local\MonitorAudioRouterBrowserBridgeToken";
    private static readonly object LockObject = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static string? _token;

    public static string CreateEnvelope(string payloadJson)
    {
        return SerializeEnvelope(payloadJson, GetToken());
    }

    internal static string SerializeEnvelope(string payloadJson, string token)
    {
        return JsonSerializer.Serialize(new BrowserBridgeEnvelope
        {
            Type = EnvelopeType,
            Token = token,
            Payload = payloadJson
        }, JsonOptions);
    }

    public static string? TryUnwrap(string envelopeJson)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<BrowserBridgeEnvelope>(envelopeJson, JsonOptions);
            if (envelope?.Type is null ||
                !envelope.Type.Equals(EnvelopeType, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(envelope.Token) ||
                string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return null;
            }

            return TokenEquals(envelope.Token, GetToken()) ? envelope.Payload : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetToken()
    {
        lock (LockObject)
        {
            if (!string.IsNullOrWhiteSpace(_token))
            {
                return _token;
            }

            using var mutex = new Mutex(false, TokenMutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new TimeoutException("Timed out waiting for browser bridge token lock.");
                }

                Directory.CreateDirectory(Paths.Root);
                if (File.Exists(Paths.BrowserBridgeTokenFile))
                {
                    var existing = File.ReadAllText(Paths.BrowserBridgeTokenFile).Trim();
                    if (existing.Length is >= 32 and <= BrowserBridgeProtocol.MaximumTokenCharacters)
                    {
                        _token = existing;
                        return _token;
                    }
                }

                _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenByteCount));
                AtomicFile.WriteAllText(Paths.BrowserBridgeTokenFile, _token);
                return _token;
            }
            finally
            {
                if (acquired)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Best effort only.
                    }
                }
            }
        }
    }

    private static bool TokenEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal sealed class BrowserBridgeEnvelope
{
    public string? Type { get; set; }
    public string? Token { get; set; }
    public string? Payload { get; set; }
}

// Native messaging hosts are launched by the browser, not by the tray app. The
// host keeps the browser contract tiny: read one browser message, wrap it with
// a local token, forward it to the tray pipe, and answer whether forwarding
// worked. The tray app remains the only process that performs routing.
internal static class NativeMessagingHost
{
    public static void Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();

        while (true)
        {
            var message = ReadMessage(input);
            if (message is null)
            {
                break;
            }

            var forwarded = ForwardToTray(message);
            WriteMessage(output, JsonSerializer.Serialize(new { ok = forwarded }));
        }
    }

    private static string? ReadMessage(Stream input)
    {
        var messageLengthBytes = ReadExact(input, 4);
        if (messageLengthBytes is null)
        {
            return null;
        }

        var messageLength = BitConverter.ToInt32(messageLengthBytes, 0);
        if (messageLength <= 0 || messageLength > BrowserBridgeProtocol.MaximumBrowserMessageBytes)
        {
            return null;
        }

        var payloadBytes = ReadExact(input, messageLength);
        return payloadBytes is null ? null : Encoding.UTF8.GetString(payloadBytes);
    }

    private static byte[]? ReadExact(Stream stream, int requestedByteCount)
    {
        var resultBuffer = new byte[requestedByteCount];
        var bytesRead = 0;
        while (bytesRead < requestedByteCount)
        {
            var bytesReadThisCall = stream.Read(resultBuffer, bytesRead, requestedByteCount - bytesRead);
            if (bytesReadThisCall == 0)
            {
                return null;
            }

            bytesRead += bytesReadThisCall;
        }

        return resultBuffer;
    }

    private static bool ForwardToTray(string json)
    {
        try
        {
            var envelope = BrowserBridgeSecurity.CreateEnvelope(json);
            if (!BrowserBridgeProtocol.IsEnvelopeLengthAllowed(envelope.Length))
            {
                return false;
            }

            using var pipe = new NamedPipeClientStream(".", BrowserHintServer.PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            writer.Write(envelope);
            writer.Write('\n');
            return true;
        }
        catch (Exception exception)
        {
            Log.WriteThrottled(
                "native-host-forward-failed:" + exception.Message,
                $"Native host forward failed: {exception.Message}",
                TimeSpan.FromMinutes(5));
            return false;
        }
    }

    private static void WriteMessage(Stream output, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(payload.Length);
        output.Write(lengthBytes, 0, lengthBytes.Length);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }
}

// RoutingEngine owns the app's safety rules. A Windows PID is the address that
// Volume Mixer exposes for a per-app output device, not a durable browser-tab
// identity. The engine owns only routes it set and verified by readback.
internal sealed class RoutingEngine : IDisposable
{
    private static readonly TimeSpan PowerResumeRecoveryWindow = TimeSpan.FromHours(12);
    private readonly RouterSettings _settings;
    private readonly IAudioRoutingPolicy _policy;
    private readonly IProcessIdentityProvider _processIdentityProvider;
    private readonly RouterState _state;
    private readonly Action<RouterState> _saveState;
    private readonly Dictionary<int, string> _lastAmbiguousTarget = new();
    private readonly Dictionary<int, DateTimeOffset> _lastActiveSessionReassertUtc = new();
    private readonly Dictionary<string, IntPtr> _browserWindowHandles = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _holdManagedRoutesUntilUtc = DateTimeOffset.MinValue;
    private string? _lastDebugSignature;

    public RoutingEngine(RouterSettings settings)
        : this(
            settings,
            new AppAudioPolicy(),
            StateStore.Load(),
            new WindowsProcessIdentityProvider(),
            StateStore.Save)
    {
    }

    internal RoutingEngine(
        RouterSettings settings,
        IAudioRoutingPolicy policy,
        RouterState state,
        IProcessIdentityProvider processIdentityProvider)
        : this(settings, policy, state, processIdentityProvider, _ => { })
    {
    }

    internal RoutingEngine(
        RouterSettings settings,
        IAudioRoutingPolicy policy,
        RouterState state,
        IProcessIdentityProvider processIdentityProvider,
        Action<RouterState> saveState)
    {
        _settings = settings;
        _policy = policy;
        _state = state;
        _processIdentityProvider = processIdentityProvider;
        _saveState = saveState;
    }

    public void HoldManagedRoutes(TimeSpan duration, string reason)
    {
        var until = DateTimeOffset.UtcNow.Add(duration);
        if (until > _holdManagedRoutesUntilUtc)
        {
            _holdManagedRoutesUntilUtc = until;
        }

        RememberPowerResumeManagedRoutes();
        Log.WriteThrottled(
            $"managed-route-hold-{reason}",
            $"Holding managed route ownership for {duration.TotalSeconds:0}s after {reason}.",
            TimeSpan.FromMinutes(1));
    }

    private void RememberPowerResumeManagedRoutes()
    {
        var now = DateTimeOffset.UtcNow;
        _state.LastPowerResumeUtc = now;
        PrunePowerResumeManagedRoutes(now);

        foreach (var route in _state.Managed.Values)
        {
            _state.PowerResumeManaged[route.ProcessId.ToString()] = route;
        }

        _saveState(_state);
    }

    public ScanResult Scan()
    {
        if (!_settings.Enabled)
        {
            return new ScanResult(true, "Disabled", 0, 0, 0);
        }

        try
        {
            if (!_policy.IsAvailable)
            {
                return new ScanResult(false, "No app audio policy backend available", 0, 0, 0);
            }

            using var devices = new AudioDeviceManager();
            var endpoints = devices.GetRenderEndpoints().ToList();
            var defaultEndpoint = devices.GetDefaultRenderEndpoint();
            if (defaultEndpoint is null)
            {
                return new ScanResult(false, "No default render endpoint", 0, 0, 0);
            }

            var windows = WindowInspector.GetVisibleWindows()
                .Where(IsAllowedWindow)
                .ToList();
            var audioSessions = devices.GetAudioSessions().ToList();
            var audioSessionProcessIds = audioSessions
                .Select(session => session.ProcessId)
                .ToHashSet();
            var activeAudioSessions = audioSessions
                .Where(session => session.State == AudioSessionState.Active)
                .ToList();
            var routeTargetBuild = BuildProcessRouteTargets(windows, audioSessionProcessIds, endpoints);
            var processRouteTargets = routeTargetBuild.Targets;
            var changed = 0;
            var skippedManual = 0;
            var failed = 0;

            // First clear routes the app used to own but that no current
            // window/audio target still needs. Held browser routes are spared
            // while the browser is paused, ambiguous, or waking after sleep.
            PrunePowerResumeManagedRoutes(DateTimeOffset.UtcNow);
            var untargeted = ClearUntargetedManagedRoutes(processRouteTargets, routeTargetBuild.HeldProcessIds);
            changed += untargeted.Changed;
            failed += untargeted.Failed;

            // Then apply the desired route for every PID that currently has a
            // window/audio target. Manual Windows Volume Mixer assignments take
            // priority over this automatic routing.
            foreach (var target in processRouteTargets.Values)
            {
                if (target.Endpoint is null)
                {
                    failed++;
                    Log.Write($"No endpoint matched route for PID {target.ProcessId} ({target.ProcessName}) on {target.Monitor.DeviceName}.");
                    continue;
                }

                var targetProcess = GetProcessInfo(target.ProcessId);
                if (targetProcess is null ||
                    targetProcess.Value.ExecutablePath is null ||
                    targetProcess.Value.StartUtc is null ||
                    !target.ProcessName.Equals(targetProcess.Value.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                    (target.ProcessStartUtc is not null && target.ProcessStartUtc != targetProcess.Value.StartUtc))
                {
                    failed++;
                    Log.WriteThrottled(
                        $"route-process-identity-unavailable-{target.ProcessId}",
                        $"Skipped PID {target.ProcessId} ({target.ProcessName}) because its executable path could not be verified.",
                        TimeSpan.FromMinutes(5));
                    continue;
                }

                var targetExecutablePath = targetProcess.Value.ExecutablePath;
                var targetIdentity = new ProcessIdentitySnapshot(
                    target.ProcessId,
                    targetProcess.Value.ProcessName,
                    targetProcess.Value.StartUtc.Value,
                    targetExecutablePath);
                var existingState = _state.Get(target.ProcessId);
                if (existingState is not null && !MatchesProcessIdentity(existingState, targetProcess.Value))
                {
                    failed++;
                    Log.WriteThrottled(
                        $"managed-route-process-identity-mismatch-{target.ProcessId}",
                        $"Kept managed ownership for PID {target.ProcessId} because its saved process identity no longer matches the running process.",
                        TimeSpan.FromMinutes(5));
                    continue;
                }

                var currentEndpoint = _policy.GetPersistedEndpoint(target.ProcessId);
                if (currentEndpoint.Status == PersistedEndpointStatus.Unavailable)
                {
                    failed++;
                    Log.WriteThrottled(
                        $"route-endpoint-unavailable-{target.ProcessId}",
                        $"Skipped PID {target.ProcessId} ({target.ProcessName}) because Windows could not read its persisted endpoint.",
                        TimeSpan.FromMinutes(5));
                    continue;
                }

                if (existingState is not null &&
                    existingState.ExecutablePath is null &&
                    !TryMigrateLegacyManagedRoute(existingState, target, targetIdentity, currentEndpoint))
                {
                    failed++;
                    Log.WriteThrottled(
                        $"managed-route-legacy-identity-unverified-{target.ProcessId}",
                        $"Kept legacy managed ownership for PID {target.ProcessId} because its executable identity could not be upgraded safely.",
                        TimeSpan.FromMinutes(5));
                    continue;
                }

                var activeSessionOnWrongEndpoint = ActiveSessionIsOnlyOnDifferentEndpoint(target, activeAudioSessions);
                if (!activeSessionOnWrongEndpoint)
                {
                    _lastActiveSessionReassertUtc.Remove(target.ProcessId);
                }

                if (existingState is null &&
                    TryGetPowerResumeManagedRoute(target, targetExecutablePath, currentEndpoint, endpoints) is ManagedRoute recoveredRoute)
                {
                    existingState = recoveredRoute;
                    _state.Managed[target.ProcessId.ToString()] = recoveredRoute;
                }

                if (existingState is null &&
                    TryRebindDormantManagedRoute(target, targetIdentity, currentEndpoint) is ManagedRoute reboundRoute)
                {
                    existingState = reboundRoute;
                }

                var hasManualOverride = currentEndpoint.HasExplicitEndpoint &&
                                        (existingState is null ||
                                         !EndpointIdsEqual(existingState.EndpointId, currentEndpoint.EndpointId));

                if (hasManualOverride)
                {
                    // If Windows reports an explicit endpoint that does not
                    // match the route we own, treat it as user-owned and leave
                    // it alone until the user sets that app back to Default.
                    skippedManual++;
                    var currentEndpointName = endpoints.FirstOrDefault(endpoint =>
                        EndpointIdsEqual(endpoint.Id, currentEndpoint.EndpointId))?.Name
                        ?? currentEndpoint.EndpointId
                        ?? "<unknown>";
                    Log.WriteThrottled(
                        $"manual-audio-override-{target.ProcessId}-{currentEndpoint.EndpointId}",
                        $"Skipped PID {target.ProcessId} ({target.ProcessName}) because Windows Volume Mixer assigns it to {currentEndpointName}. Set its output device to Default to restore automatic routing.",
                        TimeSpan.FromMinutes(5));
                    ForgetManagedRoute(target.ProcessId);
                    continue;
                }

                var desiredIsSystemDefault = EndpointIdsEqual(target.Endpoint.Id, defaultEndpoint.Id);
                if (desiredIsSystemDefault)
                {
                    // "Default" means no per-app override. Clearing the stored
                    // endpoint lets Windows follow the current system default.
                    if (existingState is not null ||
                        currentEndpoint.HasExplicitEndpoint ||
                        activeSessionOnWrongEndpoint)
                    {
                        if (activeSessionOnWrongEndpoint &&
                            !ShouldReassertActiveSessionRoute(target.ProcessId))
                        {
                            continue;
                        }

                        if (existingState is null)
                        {
                            if (ClearUnownedTargetWithReadback(target, targetIdentity))
                            {
                                changed++;
                                if (activeSessionOnWrongEndpoint)
                                {
                                    LogActiveSessionReassertion(target, activeAudioSessions);
                                }
                            }
                            else
                            {
                                failed++;
                            }
                        }
                        else
                        {
                            switch (ClearOwnedRouteWithReadback(existingState, "target monitor uses Default"))
                            {
                                case ManagedRouteClearOutcome.ClearedToDefault:
                                    changed++;
                                    if (activeSessionOnWrongEndpoint)
                                    {
                                        LogActiveSessionReassertion(target, activeAudioSessions);
                                    }

                                    break;
                                case ManagedRouteClearOutcome.StillOwned:
                                case ManagedRouteClearOutcome.ReadbackUnavailable:
                                case ManagedRouteClearOutcome.IdentityUnverified:
                                    failed++;
                                    break;
                            }
                        }
                    }

                    continue;
                }

                if (existingState is not null &&
                    EndpointIdsEqual(existingState.EndpointId, target.Endpoint.Id) &&
                    currentEndpoint.HasExplicitEndpoint &&
                    EndpointIdsEqual(currentEndpoint.EndpointId, target.Endpoint.Id) &&
                    !activeSessionOnWrongEndpoint)
                {
                    continue;
                }

                if (activeSessionOnWrongEndpoint &&
                    !ShouldReassertActiveSessionRoute(target.ProcessId))
                {
                    continue;
                }

                if (SetOwnedRouteWithReadback(target, targetIdentity, endpoints))
                {
                    changed++;
                    if (activeSessionOnWrongEndpoint)
                    {
                        LogActiveSessionReassertion(target, activeAudioSessions);
                    }
                }
                else
                {
                    failed++;
                }
            }

            _saveState(_state);
            return new ScanResult(failed == 0, $"Windows: {windows.Count}, targets: {processRouteTargets.Count}", processRouteTargets.Count, changed, skippedManual);
        }
        catch (Exception exception)
        {
            Log.Write(exception.ToString());
            return new ScanResult(false, exception.Message, 0, 0, 0);
        }
    }

    public ScanResult ClearManagedRoutes()
    {
        var changed = 0;
        var failed = 0;

        foreach (var route in _state.Managed.Values.ToList())
        {
            var processRead = _processIdentityProvider.Read(route.ProcessId);
            if (processRead.Status != ProcessIdentityReadStatus.Available || processRead.Identity is null)
            {
                failed++;
                continue;
            }

            var currentEndpoint = _policy.GetPersistedEndpoint(route.ProcessId);
            if (currentEndpoint.Status == PersistedEndpointStatus.Unavailable)
            {
                failed++;
                continue;
            }

            if (route.ExecutablePath is null)
            {
                if (!TryMigrateLegacyManagedRoute(route, processRead.Identity, currentEndpoint))
                {
                    failed++;
                    continue;
                }
            }
            else if (RouteOwnershipDecisions.EvaluateOwnedProcessIdentity(route, processRead) !=
                     OwnedProcessIdentityStatus.Match)
            {
                failed++;
                continue;
            }

            if (currentEndpoint.IsDefault)
            {
                ForgetManagedRoute(route.ProcessId);
                continue;
            }

            if (!EndpointIdsEqual(currentEndpoint.EndpointId, route.EndpointId))
            {
                ForgetManagedRoute(route.ProcessId);
                continue;
            }

            switch (ClearOwnedRouteWithReadback(route, "clearing managed routes"))
            {
                case ManagedRouteClearOutcome.ClearedToDefault:
                    changed++;
                    break;
                case ManagedRouteClearOutcome.StillOwned:
                case ManagedRouteClearOutcome.ReadbackUnavailable:
                case ManagedRouteClearOutcome.IdentityUnverified:
                    failed++;
                    break;
            }
        }

        _saveState(_state);
        return new ScanResult(failed == 0, $"Cleared managed routes: {changed}", 0, changed, 0);
    }

    private (int Changed, int Failed) ClearUntargetedManagedRoutes(
        Dictionary<int, ProcessRouteTarget> targets,
        HashSet<int> heldProcessIds)
    {
        // A managed route becomes untargeted when no visible window/audio hint
        // currently maps that PID to a monitor. For normal apps this usually
        // means clear it. Browsers get extra protection because pause/resume,
        // tab moves, and power transitions can briefly hide the correct target.
        var changed = 0;
        var failed = 0;

        var temporarilyHoldingManagedRoutes = DateTimeOffset.UtcNow < _holdManagedRoutesUntilUtc;
        foreach (var route in _state.Managed.Values.ToList())
        {
            if (targets.ContainsKey(route.ProcessId))
            {
                continue;
            }

            var processRead = _processIdentityProvider.Read(route.ProcessId);
            PersistedEndpoint? observedEndpoint = null;
            var processIdentityStatus = RouteOwnershipDecisions.EvaluateOwnedProcessIdentity(route, processRead);
            if (route.ExecutablePath is null)
            {
                if (processRead.Status != ProcessIdentityReadStatus.Available || processRead.Identity is null)
                {
                    continue;
                }

                observedEndpoint = _policy.GetPersistedEndpoint(route.ProcessId);
                if (observedEndpoint.Status == PersistedEndpointStatus.Unavailable ||
                    !TryMigrateLegacyManagedRoute(route, processRead.Identity, observedEndpoint))
                {
                    continue;
                }

                processIdentityStatus = OwnedProcessIdentityStatus.Match;
            }

            var processIdentityMatches = processIdentityStatus == OwnedProcessIdentityStatus.Match;

            if (temporarilyHoldingManagedRoutes && processIdentityMatches)
            {
                continue;
            }

            if (heldProcessIds.Contains(route.ProcessId))
            {
                var heldEndpoint = observedEndpoint ?? _policy.GetPersistedEndpoint(route.ProcessId);
                if (heldEndpoint.Status == PersistedEndpointStatus.Unavailable || !processIdentityMatches)
                {
                    continue;
                }

                if (heldEndpoint.IsDefault)
                {
                    KeepManagedRouteWhileEndpointIsUnavailable(route, "browser window is paused or ambiguous");
                    continue;
                }

                if (EndpointIdsEqual(heldEndpoint.EndpointId, route.EndpointId))
                {
                    continue;
                }

                // A user or Windows changed the endpoint while the route was held.
                // Forget our ownership without changing their current selection.
                ForgetManagedRoute(route.ProcessId);
                continue;
            }

            if (!processIdentityMatches)
            {
                continue;
            }

            var currentEndpoint = observedEndpoint ?? _policy.GetPersistedEndpoint(route.ProcessId);
            if (currentEndpoint.Status == PersistedEndpointStatus.Unavailable)
            {
                continue;
            }

            if (currentEndpoint.HasExplicitEndpoint &&
                EndpointIdsEqual(currentEndpoint.EndpointId, route.EndpointId))
            {
                switch (ClearOwnedRouteWithReadback(route, "route is no longer targeted"))
                {
                    case ManagedRouteClearOutcome.ClearedToDefault:
                        changed++;
                        break;
                    case ManagedRouteClearOutcome.StillOwned:
                    case ManagedRouteClearOutcome.ReadbackUnavailable:
                    case ManagedRouteClearOutcome.IdentityUnverified:
                        failed++;
                        break;
                }

                continue;
            }

            ForgetManagedRoute(route.ProcessId);
        }

        return (changed, failed);
    }

    internal bool SetOwnedRouteWithReadback(
        ProcessRouteTarget target,
        ProcessIdentitySnapshot expectedIdentity,
        List<AudioEndpoint> endpoints)
    {
        // Only write state.json after Windows reports the same explicit
        // endpoint we just requested. This prevents the app from "owning" a
        // route that Windows rejected or that was immediately changed elsewhere.
        if (target.Endpoint is null)
        {
            return false;
        }

        if (!RouteOwnershipDecisions.TargetIdentityMatches(
                target,
                expectedIdentity,
                _processIdentityProvider.Read(target.ProcessId)))
        {
            Log.WriteThrottled(
                $"managed-route-set-identity-changed-{target.ProcessId}",
                $"Skipped setting PID {target.ProcessId} ({target.ProcessName}) because its executable identity changed before the policy write.",
                TimeSpan.FromMinutes(5));
            return false;
        }

        var writeSucceeded = _policy.SetPersistedEndpoint(target.ProcessId, target.Endpoint.Id);
        var afterSet = _policy.GetPersistedEndpoint(target.ProcessId);
        if (RouteOwnershipDecisions.ShouldClaimAfterSet(writeSucceeded, target.Endpoint.Id, afterSet))
        {
            _state.Managed[target.ProcessId.ToString()] = ManagedRoute.FromTarget(target, expectedIdentity);
            _state.PowerResumeManaged.Remove(target.ProcessId.ToString());
            return true;
        }

        var reportedEndpointName = DescribePersistedEndpoint(afterSet, endpoints);
        Log.WriteThrottled(
            $"managed-route-set-readback-mismatch-{target.ProcessId}-{target.Endpoint.Id}-{afterSet.EndpointId}",
            $"Did not claim managed ownership for PID {target.ProcessId} ({target.ProcessName}) because Windows reported {reportedEndpointName} after assigning {target.Endpoint.Name}.",
            TimeSpan.FromMinutes(5));
        return false;
    }

    internal bool ClearUnownedTargetWithReadback(
        ProcessRouteTarget target,
        ProcessIdentitySnapshot expectedIdentity)
    {
        if (!RouteOwnershipDecisions.TargetIdentityMatches(
                target,
                expectedIdentity,
                _processIdentityProvider.Read(target.ProcessId)))
        {
            Log.WriteThrottled(
                $"direct-route-clear-identity-changed-{target.ProcessId}",
                $"Skipped clearing PID {target.ProcessId} ({target.ProcessName}) because its executable identity changed before the policy write.",
                TimeSpan.FromMinutes(5));
            return false;
        }

        _policy.ClearPersistedEndpoint(target.ProcessId);
        return _policy.GetPersistedEndpoint(target.ProcessId).IsDefault;
    }

    internal bool TryMigrateLegacyManagedRoute(
        ManagedRoute route,
        ProcessRouteTarget target,
        ProcessIdentitySnapshot currentIdentity,
        PersistedEndpoint currentEndpoint)
    {
        if (target.ProcessId != currentIdentity.ProcessId ||
            !target.ProcessName.Equals(currentIdentity.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            (target.ProcessStartUtc is not null &&
             target.ProcessStartUtc.Value.UtcTicks != currentIdentity.StartUtc.UtcTicks))
        {
            return false;
        }

        return TryMigrateLegacyManagedRoute(route, currentIdentity, currentEndpoint);
    }

    private bool TryMigrateLegacyManagedRoute(
        ManagedRoute route,
        ProcessIdentitySnapshot currentIdentity,
        PersistedEndpoint currentEndpoint)
    {
        if (route.ExecutablePath is not null ||
            route.ProcessStartUtcTicks is null ||
            !currentEndpoint.HasExplicitEndpoint ||
            route.ProcessId != currentIdentity.ProcessId ||
            !route.ProcessName.Equals(currentIdentity.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            route.ProcessStartUtcTicks.Value != currentIdentity.StartUtc.UtcTicks ||
            !EndpointIdsEqual(route.EndpointId, currentEndpoint.EndpointId))
        {
            return false;
        }

        var normalizedPath = RouteOwnershipDecisions.NormalizeExecutablePath(currentIdentity.ExecutablePath);
        if (normalizedPath is null ||
            !RouteOwnershipDecisions.ProcessIdentityMatches(
                currentIdentity,
                _processIdentityProvider.Read(route.ProcessId)))
        {
            return false;
        }

        route.ExecutablePath = normalizedPath;
        return true;
    }

    private void KeepManagedRouteWhileEndpointIsUnavailable(ManagedRoute route, string reason)
    {
        // A sleeping or powered-off display can make Windows temporarily report
        // Default while the old per-app route is still able to reappear later.
        // Keep ownership so the next real target can clear or reapply it.
        if (IsPowerResumeRecoveryActive(DateTimeOffset.UtcNow))
        {
            _state.PowerResumeManaged[route.ProcessId.ToString()] = route;
        }

        Log.WriteThrottled(
            $"managed-route-temporary-default-{route.ProcessId}-{route.EndpointId}",
            $"Kept managed ownership for PID {route.ProcessId} ({route.ProcessName}) because Windows reported Default while {reason}.",
            TimeSpan.FromMinutes(5));
    }

    internal ManagedRouteClearOutcome ClearOwnedRouteWithReadback(ManagedRoute route, string reason)
    {
        // Clearing is also verified by readback. If Windows still reports our
        // endpoint, keep ownership so a later scan can retry instead of
        // mistaking the stuck route for a manual user assignment.
        var processStatus = RouteOwnershipDecisions.EvaluateOwnedProcessIdentity(
            route,
            _processIdentityProvider.Read(route.ProcessId));
        if (processStatus != OwnedProcessIdentityStatus.Match)
        {
            Log.WriteThrottled(
                $"managed-route-clear-identity-unverified-{route.ProcessId}-{route.EndpointId}",
                $"Kept managed ownership for PID {route.ProcessId} because its executable identity could not be verified before {reason}.",
                TimeSpan.FromMinutes(5));
            return ManagedRouteClearOutcome.IdentityUnverified;
        }

        _policy.ClearPersistedEndpoint(route.ProcessId);
        var afterClear = _policy.GetPersistedEndpoint(route.ProcessId);
        var outcome = RouteOwnershipDecisions.EvaluateClearReadback(afterClear, route.EndpointId);
        if (outcome == ManagedRouteClearOutcome.ClearedToDefault)
        {
            ForgetManagedRoute(route.ProcessId);
            return outcome;
        }

        if (outcome == ManagedRouteClearOutcome.ReadbackUnavailable)
        {
            Log.WriteThrottled(
                $"managed-route-clear-readback-unavailable-{route.ProcessId}-{route.EndpointId}",
                $"Kept managed ownership for PID {route.ProcessId} ({route.ProcessName}) because Windows could not verify the endpoint after {reason}.",
                TimeSpan.FromMinutes(5));
            return outcome;
        }

        if (outcome == ManagedRouteClearOutcome.StillOwned)
        {
            Log.WriteThrottled(
                $"managed-route-clear-still-explicit-{route.ProcessId}-{route.EndpointId}",
                $"Kept managed ownership for PID {route.ProcessId} ({route.ProcessName}) because clearing during {reason} did not restore Default.",
                TimeSpan.FromMinutes(5));
            return outcome;
        }

        Log.WriteThrottled(
            $"managed-route-clear-different-endpoint-{route.ProcessId}-{afterClear.EndpointId}",
            $"Forgot managed ownership for PID {route.ProcessId} ({route.ProcessName}) because Windows now reports a different explicit endpoint after {reason}.",
            TimeSpan.FromMinutes(5));
        ForgetManagedRoute(route.ProcessId);
        return outcome;
    }

    private void ForgetManagedRoute(int processId)
    {
        _state.Managed.Remove(processId.ToString());
        _state.PowerResumeManaged.Remove(processId.ToString());
    }

    private static string DescribePersistedEndpoint(PersistedEndpoint endpoint, List<AudioEndpoint> endpoints)
    {
        if (endpoint.Status == PersistedEndpointStatus.Unavailable)
        {
            return "an unavailable endpoint";
        }

        if (!endpoint.HasExplicitEndpoint)
        {
            return "Default";
        }

        return endpoints.FirstOrDefault(candidate =>
                   EndpointIdsEqual(candidate.Id, endpoint.EndpointId))?.Name
               ?? endpoint.EndpointId
               ?? "<unknown>";
    }

    private static bool ActiveSessionIsOnlyOnDifferentEndpoint(
        ProcessRouteTarget target,
        List<AudioSessionInfo> activeAudioSessions)
    {
        if (target.Endpoint is null)
        {
            return false;
        }

        var processSessions = activeAudioSessions
            .Where(session => session.ProcessId == target.ProcessId)
            .ToList();
        if (processSessions.Count == 0)
        {
            return false;
        }

        var hasTargetEndpoint = processSessions.Any(session =>
            EndpointIdsEqual(session.EndpointId, target.Endpoint.Id));
        var hasOtherEndpoint = processSessions.Any(session =>
            !EndpointIdsEqual(session.EndpointId, target.Endpoint.Id));
        return hasOtherEndpoint && !hasTargetEndpoint;
    }

    private bool ShouldReassertActiveSessionRoute(int processId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastActiveSessionReassertUtc.TryGetValue(processId, out var lastReassertUtc) &&
            now - lastReassertUtc < TimeSpan.FromMilliseconds(750))
        {
            return false;
        }

        _lastActiveSessionReassertUtc[processId] = now;
        return true;
    }

    private static void LogActiveSessionReassertion(
        ProcessRouteTarget target,
        List<AudioSessionInfo> activeAudioSessions)
    {
        if (target.Endpoint is null)
        {
            return;
        }

        var currentEndpoints = string.Join(
            ", ",
            activeAudioSessions
                .Where(session => session.ProcessId == target.ProcessId)
                .Select(session => session.EndpointName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        if (currentEndpoints.Length == 0)
        {
            currentEndpoints = "<unknown>";
        }

        Log.WriteThrottled(
            $"active-session-route-reassert-{target.ProcessId}-{target.Endpoint.Id}",
            $"Reasserted route for PID {target.ProcessId} ({target.ProcessName}) because its active audio session was still on {currentEndpoints} while the target monitor maps to {target.Endpoint.Name}.",
            TimeSpan.FromSeconds(30));
    }

    internal ManagedRoute? TryRebindDormantManagedRoute(
        ProcessRouteTarget target,
        ProcessIdentitySnapshot targetIdentity,
        PersistedEndpoint currentEndpoint)
    {
        foreach (var route in _state.Managed.Values.ToList())
        {
            if (!RouteOwnershipDecisions.CanRebindDormantRoute(
                    route,
                    target.ProcessId,
                    targetIdentity.ExecutablePath,
                    currentEndpoint,
                    RouteOwnershipDecisions.EvaluateOwnedProcessIdentity(
                        route,
                        _processIdentityProvider.Read(route.ProcessId))))
            {
                continue;
            }

            if (!RouteOwnershipDecisions.TargetIdentityMatches(
                    target,
                    targetIdentity,
                    _processIdentityProvider.Read(target.ProcessId)))
            {
                return null;
            }

            var reboundRoute = ManagedRoute.RebindTo(route, target, targetIdentity);
            ForgetManagedRoute(route.ProcessId);
            _state.Managed[target.ProcessId.ToString()] = reboundRoute;
            Log.WriteThrottled(
                $"managed-route-rebound-{route.ProcessId}-{target.ProcessId}-{route.EndpointId}",
                $"Transferred managed route ownership from dormant PID {route.ProcessId} to PID {target.ProcessId} for {target.ProcessName}.",
                TimeSpan.FromMinutes(5));
            return reboundRoute;
        }

        return null;
    }

    private ManagedRoute? TryGetPowerResumeManagedRoute(
        ProcessRouteTarget target,
        string targetExecutablePath,
        PersistedEndpoint currentEndpoint,
        List<AudioEndpoint> endpoints)
    {
        // Sleep/wake can disturb the scan timing before the browser extension
        // and audio-session events settle. If Windows still has the exact route
        // we set before resume, recover ownership instead of treating it as
        // a manual Volume Mixer assignment.
        if (!currentEndpoint.HasExplicitEndpoint ||
            _state.LastPowerResumeUtc is not DateTimeOffset lastPowerResumeUtc ||
            DateTimeOffset.UtcNow - lastPowerResumeUtc > PowerResumeRecoveryWindow)
        {
            return null;
        }

        if (!_state.PowerResumeManaged.TryGetValue(target.ProcessId.ToString(), out var route))
        {
            return null;
        }

        if (!target.MatchesProcessIdentity(route) ||
            !RouteOwnershipDecisions.CanClearManagedRoute(route, target.ProcessId, targetExecutablePath))
        {
            return null;
        }

        if (!EndpointIdsEqual(route.EndpointId, currentEndpoint.EndpointId))
        {
            _state.PowerResumeManaged.Remove(target.ProcessId.ToString());
            return null;
        }

        var currentEndpointName = endpoints.FirstOrDefault(endpoint =>
            EndpointIdsEqual(endpoint.Id, currentEndpoint.EndpointId))?.Name
            ?? currentEndpoint.EndpointId
            ?? "<unknown>";
        Log.WriteThrottled(
            $"power-resume-route-recovery-{target.ProcessId}-{currentEndpoint.EndpointId}",
            $"Recovered managed route ownership for PID {target.ProcessId} ({target.ProcessName}) after power resume; Windows still had it assigned to {currentEndpointName}.",
            TimeSpan.FromMinutes(5));
        return route;
    }

    private void PrunePowerResumeManagedRoutes(DateTimeOffset now)
    {
        if (IsPowerResumeRecoveryActive(now))
        {
            return;
        }

        _state.PowerResumeManaged.Clear();
        _state.LastPowerResumeUtc = null;
    }

    private bool IsPowerResumeRecoveryActive(DateTimeOffset now)
    {
        return _state.LastPowerResumeUtc is DateTimeOffset lastPowerResumeUtc &&
               now - lastPowerResumeUtc <= PowerResumeRecoveryWindow;
    }

    private static bool ManagedRouteProcessStillMatches(ManagedRoute route)
    {
        var process = GetProcessInfo(route.ProcessId);
        return process is not null && MatchesProcessIdentity(route, process.Value);
    }

    private ProcessRouteTargetBuildResult BuildProcessRouteTargets(
        List<WindowInfo> windows,
        HashSet<int> audioSessionProcessIds,
        List<AudioEndpoint> endpoints)
    {
        // This translates "what is visible and audible" into "which Windows
        // PID should be assigned to which endpoint." Native app windows can
        // map directly by PID. Browsers need extension hints because many tabs
        // can share a process and one browser process can have windows on
        // several monitors.
        var processSnapshot = _settings.RouteChildProcesses ? ProcessSnapshot.Capture() : ProcessSnapshot.Empty;
        var hints = BrowserHintStore.GetSnapshot();
        var processRouteTargets = new Dictionary<int, ProcessRouteTarget>();
        var ambiguousProcessIds = new HashSet<int>();
        var authoritativeHintProcessIds = new HashSet<int>();

        AddHintTargets(
            processRouteTargets,
            ambiguousProcessIds,
            authoritativeHintProcessIds,
            hints,
            audioSessionProcessIds,
            windows,
            endpoints);

        foreach (var window in windows)
        {
            if (!BrowserHintStore.WindowMatchesHints(hints, window))
            {
                continue;
            }

            var endpoint = FindEndpointForMonitor(window.Monitor, endpoints);
            if (audioSessionProcessIds.Contains(window.ProcessId) &&
                !authoritativeHintProcessIds.Contains(window.ProcessId))
            {
                var target = new ProcessRouteTarget(window.ProcessId, window.ProcessName, window.ProcessStartUtc, window.Monitor, endpoint);
                AddRouteTarget(processRouteTargets, ambiguousProcessIds, target);
            }

            if (!_settings.RouteChildProcesses)
            {
                continue;
            }

            foreach (var child in processSnapshot.GetDescendants(window.ProcessId))
            {
                if (!IsAllowedProcessName(child.ProcessName))
                {
                    continue;
                }

                if (!audioSessionProcessIds.Contains(child.ProcessId) ||
                    authoritativeHintProcessIds.Contains(child.ProcessId))
                {
                    continue;
                }

                AddRouteTarget(processRouteTargets, ambiguousProcessIds, new ProcessRouteTarget(child.ProcessId, child.ProcessName, child.ProcessStartUtc, window.Monitor, endpoint));
            }
        }

        foreach (var processId in processRouteTargets.Keys)
        {
            _lastAmbiguousTarget.Remove(processId);
        }

        var heldProcessIds = new HashSet<int>(ambiguousProcessIds);
        foreach (var route in _state.Managed.Values)
        {
            if (processRouteTargets.ContainsKey(route.ProcessId) ||
                !BrowserHintStore.IsBrowserProcessName(route.ProcessName))
            {
                continue;
            }

            var process = GetProcessInfo(route.ProcessId);
            if (process is not null && MatchesProcessIdentity(route, process.Value))
            {
                heldProcessIds.Add(route.ProcessId);
                Log.WriteThrottled(
                    $"held-browser-route-{route.ProcessId}",
                    $"Held the last verified route for PID {route.ProcessId} ({route.ProcessName}) while its browser window is paused or ambiguous.",
                    TimeSpan.FromMinutes(5));
            }
        }

        LogDebugRoutingIfChanged(windows, audioSessionProcessIds, hints, processRouteTargets);

        return new ProcessRouteTargetBuildResult(processRouteTargets, heldProcessIds);
    }

    private void LogDebugRoutingIfChanged(
        List<WindowInfo> windows,
        HashSet<int> audioSessionProcessIds,
        Dictionary<string, BrowserHintSet> hints,
        Dictionary<int, ProcessRouteTarget> targets)
    {
        if (!_settings.DebugLogging)
        {
            return;
        }

        var browserWindows = windows
            .Where(window => BrowserHintStore.IsBrowserProcessName(window.ProcessName))
            .Select(window =>
                $"{window.ProcessId}@{window.Monitor.BoundsKey}:" +
                BrowserHintStore.CreateTitleDiagnostic(new[] { window.Title }).Replace(' ', ';'));
        var hintSummary = hints.Values.Select(hintSet => $"{hintSet.ProcessName}:{hintSet.Windows.Count}");
        var targetSummary = targets.Values.Select(target => $"{target.ProcessId}->{target.Endpoint?.Name ?? "<none>"}@{target.Monitor.BoundsKey}");
        var signature =
            $"audio=[{string.Join(",", audioSessionProcessIds.OrderBy(processId => processId))}] " +
            $"hints=[{string.Join(",", hintSummary)}] " +
            $"browserWindows=[{string.Join(";", browserWindows)}] " +
            $"targets=[{string.Join(";", targetSummary)}]";

        if (string.Equals(_lastDebugSignature, signature, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastDebugSignature = signature;
        Log.Write($"Debug routing: {signature}");
    }

    private void AddHintTargets(
        Dictionary<int, ProcessRouteTarget> targets,
        HashSet<int> ambiguousProcessIds,
        HashSet<int> authoritativeHintProcessIds,
        Dictionary<string, BrowserHintSet> hints,
        HashSet<int> audioSessionProcessIds,
        List<WindowInfo> windows,
        List<AudioEndpoint> endpoints)
    {
        // Browser-provided PIDs are trusted only when Windows also reports a
        // relevant audio session for that PID. If the extension cannot provide
        // a usable PID, the fallback is a single matching browser audio session
        // plus one audible browser window.
        var monitors = WindowInspector.GetMonitors();
        foreach (var hintSet in hints.Values)
        {
            foreach (var source in hintSet.Sources)
            {
                var usableExplicitProcesses = source.Windows
                    .SelectMany(window => window.ProcessIds)
                    .Distinct()
                    .Select(processId => new
                    {
                        OwnsRelevantAudioSession = audioSessionProcessIds.Contains(processId),
                        Process = GetProcessInfo(processId)
                    })
                    .Where(candidate =>
                        candidate.Process is not null &&
                        BrowserHintStore.IsAdvisoryProcessMatch(
                            hintSet.ProcessName,
                            candidate.Process.Value.ProcessName,
                            candidate.OwnsRelevantAudioSession) &&
                        IsAllowedProcessName(candidate.Process.Value.ProcessName))
                    .Select(candidate => candidate.Process!.Value)
                    .ToDictionary(process => process.ProcessId);
                var routeWindows = source.Windows;
                if (source.PreferredWindowId is int preferredWindowId &&
                    source.Windows.Count > 1 &&
                    usableExplicitProcesses.Count == 0)
                {
                    var preferredWindows = source.Windows
                        .Where(window => window.WindowId == preferredWindowId)
                        .ToList();
                    if (preferredWindows.Count == 1)
                    {
                        routeWindows = preferredWindows;
                    }
                }

                var inferredProcess = routeWindows.Count == 1
                    ? audioSessionProcessIds
                        .Select(GetProcessInfo)
                        .Where(process =>
                            process is not null &&
                            BrowserHintStore.ProcessBelongsToFamily(
                                hintSet.Family,
                                process.Value.ProcessName) &&
                            IsAllowedProcessName(process.Value.ProcessName))
                        .Select(process => process!.Value)
                        .DistinctBy(process => process.ProcessId)
                        .ToList()
                    : new List<(int ProcessId, string ProcessName, DateTimeOffset? StartUtc, string? ExecutablePath)>();

                foreach (var hintWindow in routeWindows)
                {
                    var extensionMonitor = WindowInspector.PickMonitor(hintWindow.Bounds, monitors);
                    var matchingWindow = ResolveNativeBrowserWindow(hintSet, source, hintWindow, windows);
                    var monitor = matchingWindow?.Monitor ?? extensionMonitor;
                    if (matchingWindow is not null &&
                        !monitor.BoundsKey.Equals(extensionMonitor.BoundsKey, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.WriteThrottled(
                            $"corrected-browser-monitor-{hintSet.ProcessName}-{monitor.BoundsKey}",
                            $"Corrected stale {hintSet.ProcessName} extension bounds from {extensionMonitor.DeviceName} to native titled window {monitor.DeviceName}.",
                            TimeSpan.FromMinutes(5));
                    }

                    var endpoint = FindEndpointForMonitor(monitor, endpoints);
                    var matchedExplicitProcessIds = 0;
                    foreach (var processId in hintWindow.ProcessIds.Distinct())
                    {
                        if (!usableExplicitProcesses.TryGetValue(processId, out var process))
                        {
                            continue;
                        }

                        matchedExplicitProcessIds++;
                        AddRouteTarget(targets, ambiguousProcessIds, new ProcessRouteTarget(processId, process.ProcessName, process.StartUtc, monitor, endpoint));
                        authoritativeHintProcessIds.Add(processId);
                    }

                    if (matchedExplicitProcessIds == 0 && inferredProcess.Count == 1)
                    {
                        var process = inferredProcess[0];
                        Log.WriteThrottled(
                            $"inferred-browser-route-{process.ProcessId}-{monitor.BoundsKey}",
                            $"Matched PID {process.ProcessId} ({process.ProcessName}) to the sole audible browser window on {monitor.DeviceName}.",
                            TimeSpan.FromMinutes(5));
                        AddRouteTarget(
                            targets,
                            ambiguousProcessIds,
                            new ProcessRouteTarget(process.ProcessId, process.ProcessName, process.StartUtc, monitor, endpoint));
                        authoritativeHintProcessIds.Add(process.ProcessId);
                    }
                }
            }
        }
    }

    private WindowInfo? ResolveNativeBrowserWindow(
        BrowserHintSet hintSet,
        BrowserHintSourceSnapshot source,
        BrowserHintWindow hintWindow,
        List<WindowInfo> windows)
    {
        var candidates = windows
            .Where(window =>
                BrowserHintStore.ProcessBelongsToFamily(hintSet.Family, window.ProcessName) &&
                BrowserHintStore.WindowMatchesHint(hintWindow, window))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var cacheKey = $"{hintSet.ProcessName}:{source.SourceInstanceId ?? "legacy"}:{hintWindow.WindowId}";
        if (hintWindow.WindowId > 0 &&
            _browserWindowHandles.TryGetValue(cacheKey, out var cachedHandle))
        {
            var cached = candidates.FirstOrDefault(window => window.Handle == cachedHandle);
            if (cached is not null)
            {
                return cached;
            }

            _browserWindowHandles.Remove(cacheKey);
        }

        WindowInfo? match = candidates.Count == 1 ? candidates[0] : null;
        if (match is null)
        {
            var ranked = candidates
                .Select(window => new
                {
                    Window = window,
                    SizeDelta =
                        Math.Abs(window.Bounds.Width - hintWindow.Bounds.Width) +
                        Math.Abs(window.Bounds.Height - hintWindow.Bounds.Height)
                })
                .OrderBy(candidate => candidate.SizeDelta)
                .ToList();
            var best = ranked[0];
            var second = ranked[1];
            if (best.SizeDelta <= 128 && second.SizeDelta - best.SizeDelta >= 32)
            {
                match = best.Window;
                Log.WriteThrottled(
                    $"browser-window-size-match-{cacheKey}-{match.Handle}",
                    $"Matched {hintSet.ProcessName} extension window {hintWindow.WindowId} to native window 0x{match.Handle.ToInt64():X} by title and size.",
                    TimeSpan.FromMinutes(5));
            }
        }

        if (match is not null && hintWindow.WindowId > 0)
        {
            _browserWindowHandles[cacheKey] = match.Handle;
        }

        return match;
    }

    private static (int ProcessId, string ProcessName, DateTimeOffset? StartUtc, string? ExecutablePath)? GetProcessInfo(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
            DateTimeOffset? start = null;
            try
            {
                start = process.StartTime.ToUniversalTime();
            }
            catch
            {
                start = null;
            }

            string? executablePath = null;
            try
            {
                executablePath = RouteOwnershipDecisions.NormalizeExecutablePath(process.MainModule?.FileName);
            }
            catch
            {
                executablePath = null;
            }

            return (processId, name, start, executablePath);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesProcessIdentity(
        ManagedRoute route,
        (int ProcessId, string ProcessName, DateTimeOffset? StartUtc, string? ExecutablePath) process)
    {
        if (route.ProcessId != process.ProcessId ||
            !route.ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (route.ProcessStartUtcTicks is not null &&
            (process.StartUtc is null || route.ProcessStartUtcTicks.Value != process.StartUtc.Value.UtcTicks))
        {
            return false;
        }

        return route.ExecutablePath is null ||
               RouteOwnershipDecisions.CanClearManagedRoute(route, process.ProcessId, process.ExecutablePath);
    }

    private void AddRouteTarget(
        Dictionary<int, ProcessRouteTarget> targets,
        HashSet<int> ambiguousProcessIds,
        ProcessRouteTarget target)
    {
        if (ambiguousProcessIds.Contains(target.ProcessId))
        {
            return;
        }

        if (targets.TryGetValue(target.ProcessId, out var existing))
        {
            if (!EndpointIdsEqual(existing.Endpoint?.Id, target.Endpoint?.Id))
            {
                targets.Remove(target.ProcessId);
                ambiguousProcessIds.Add(target.ProcessId);
                var key = $"{existing.Endpoint?.Id ?? "<none>"}|{target.Endpoint?.Id ?? "<none>"}";
                if (!_lastAmbiguousTarget.TryGetValue(target.ProcessId, out var previous) ||
                    !previous.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write($"Skipped PID {target.ProcessId} ({target.ProcessName}) because it maps to multiple monitors/endpoints.");
                    _lastAmbiguousTarget[target.ProcessId] = key;
                }
            }

            return;
        }

        targets[target.ProcessId] = target;
    }

    private AudioEndpoint? FindEndpointForMonitor(MonitorInfo monitor, List<AudioEndpoint> endpoints)
    {
        var defaultEndpoint = endpoints.FirstOrDefault(endpoint => endpoint.IsDefault);
        foreach (var route in _settings.MonitorRoutes)
        {
            if (route.Matches(monitor))
            {
                return route.UsesSystemDefault ? defaultEndpoint : route.FindEndpoint(endpoints);
            }
        }

        return EndpointMatcher.Find(endpoints, _settings.FallbackAudioDeviceNameContains, _settings.FallbackAudioDeviceIdContains)
               ?? defaultEndpoint;
    }

    private bool IsAllowedWindow(WindowInfo window)
    {
        if (!IsAllowedProcessName(window.ProcessName))
        {
            return false;
        }

        if (_settings.IgnoreWindowTitlesContaining.Any(ignoredTitleText => !string.IsNullOrWhiteSpace(ignoredTitleText) &&
                                                                           window.Title.Contains(ignoredTitleText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private bool IsAllowedProcessName(string processName)
    {
        if (_settings.IgnoreProcessNames.Any(configuredProcessName => ProcessNameMatches(processName, configuredProcessName)))
        {
            return false;
        }

        return _settings.AllowProcessNames.Count == 0 ||
               _settings.AllowProcessNames.Any(configuredProcessName => ProcessNameMatches(processName, configuredProcessName));
    }

    private static bool ProcessNameMatches(string actual, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var normalized = actual.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? actual : actual + ".exe";
        var configuredNormalized = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured : configured + ".exe";
        return normalized.Equals(configuredNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndpointIdsEqual(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _policy.Dispose();
    }
}

internal sealed record ScanResult(bool Success, string Message, int Targets, int Changed, int SkippedManual)
{
    public override string ToString()
    {
        var prefix = Success ? "OK" : "Error";
        return $"{prefix}: {Message}; changed {Changed}; manual {SkippedManual}";
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Monitor Audio Router";

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    private static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Could not open current-user Run registry key.");
        key.SetValue(RunValueName, Quote(GetExecutablePath()), RegistryValueKind.String);
    }

    private static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? Application.ExecutablePath;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

internal sealed class RouterSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutostartEnabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 1500;
    public bool RouteChildProcesses { get; set; } = true;
    public bool DebugLogging { get; set; }
    public List<string> AllowProcessNames { get; set; } = new()
    {
        "chrome.exe",
        "chromium.exe",
        "msedge.exe",
        "firefox.exe",
        "brave.exe",
        "vivaldi.exe",
        "vlc.exe",
        "spotify.exe"
    };
    public List<string> IgnoreProcessNames { get; set; } = new()
    {
        "audacity.exe",
        "steam.exe",
        "steamvr.exe",
        "vrserver.exe",
        "vrcompositor.exe",
        "vrmonitor.exe",
        "oculusclient.exe",
        "ovrserver_x64.exe",
        "ovrredird.exe",
        "virtualdesktop.streamer.exe"
    };
    public List<string> IgnoreWindowTitlesContaining { get; set; } = new();
    public string? FallbackAudioDeviceNameContains { get; set; } = "Pebble V3";
    public string? FallbackAudioDeviceIdContains { get; set; }
    public List<MonitorRoute> MonitorRoutes { get; set; } = new()
    {
        new MonitorRoute
        {
            MonitorDeviceIdContains = "TCL0000",
            AudioDeviceNameContains = "55S405"
        }
    };
}

internal enum SettingsLoadStatus
{
    Valid,
    Missing,
    Invalid
}

internal sealed record SettingsLoadResult(
    RouterSettings Settings,
    SettingsLoadStatus Status,
    string? ErrorMessage);

internal sealed class MonitorRoute
{
    public string? MonitorDeviceNameContains { get; set; }
    public string? MonitorFriendlyNameContains { get; set; }
    public string? MonitorDeviceIdContains { get; set; }
    public string? MonitorBounds { get; set; }
    public bool? Primary { get; set; }
    public string? AudioDeviceNameContains { get; set; }
    public string? AudioDeviceIdContains { get; set; }

    public bool UsesSystemDefault =>
        string.IsNullOrWhiteSpace(AudioDeviceNameContains) &&
        string.IsNullOrWhiteSpace(AudioDeviceIdContains);

    public bool Matches(MonitorInfo monitor)
    {
        if (Primary.HasValue && Primary.Value != monitor.Primary)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MonitorDeviceNameContains) &&
            !monitor.DeviceName.Contains(MonitorDeviceNameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MonitorFriendlyNameContains) &&
            !monitor.FriendlyName.Contains(MonitorFriendlyNameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MonitorDeviceIdContains) &&
            !monitor.DeviceId.Contains(MonitorDeviceIdContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MonitorBounds) &&
            !monitor.BoundsKey.Equals(MonitorBounds.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public AudioEndpoint? FindEndpoint(List<AudioEndpoint> endpoints)
    {
        return EndpointMatcher.Find(endpoints, AudioDeviceNameContains, AudioDeviceIdContains);
    }
}

internal static class EndpointMatcher
{
    public static AudioEndpoint? Find(List<AudioEndpoint> endpoints, string? nameContains, string? idContains)
    {
        if (!string.IsNullOrWhiteSpace(idContains))
        {
            var match = endpoints.FirstOrDefault(endpoint => endpoint.Id.Contains(idContains, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var match = endpoints.FirstOrDefault(endpoint => endpoint.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}

internal sealed class RouterState
{
    public Dictionary<string, ManagedRoute> Managed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManagedRoute> PowerResumeManaged { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastPowerResumeUtc { get; set; }

    public ManagedRoute? Get(int processId)
    {
        return Managed.TryGetValue(processId.ToString(), out var route) ? route : null;
    }
}

internal sealed class ManagedRoute
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long? ProcessStartUtcTicks { get; set; }
    public string? ExecutablePath { get; set; }
    public string EndpointId { get; set; } = "";
    public string EndpointName { get; set; } = "";
    public DateTimeOffset LastSetUtc { get; set; }

    public static ManagedRoute FromTarget(ProcessRouteTarget target, ProcessIdentitySnapshot identity)
    {
        return new ManagedRoute
        {
            ProcessId = target.ProcessId,
            ProcessName = target.ProcessName,
            ProcessStartUtcTicks = identity.StartUtc.UtcTicks,
            ExecutablePath = RouteOwnershipDecisions.NormalizeExecutablePath(identity.ExecutablePath),
            EndpointId = target.Endpoint?.Id ?? "",
            EndpointName = target.Endpoint?.Name ?? "",
            LastSetUtc = DateTimeOffset.UtcNow
        };
    }

    public static ManagedRoute RebindTo(
        ManagedRoute dormantRoute,
        ProcessRouteTarget target,
        ProcessIdentitySnapshot identity)
    {
        return new ManagedRoute
        {
            ProcessId = target.ProcessId,
            ProcessName = identity.ProcessName,
            ProcessStartUtcTicks = identity.StartUtc.UtcTicks,
            ExecutablePath = RouteOwnershipDecisions.NormalizeExecutablePath(identity.ExecutablePath),
            EndpointId = dormantRoute.EndpointId,
            EndpointName = dormantRoute.EndpointName,
            LastSetUtc = dormantRoute.LastSetUtc
        };
    }
}

internal enum ManagedRouteClearOutcome
{
    ClearedToDefault,
    StillOwned,
    OwnershipLost,
    ReadbackUnavailable,
    IdentityUnverified
}

internal enum ManagedRouteIdentityStatus
{
    Match,
    Mismatch,
    Unavailable
}

internal enum OwnedProcessIdentityStatus
{
    Match,
    Dormant,
    Unavailable
}

internal enum ProcessIdentityReadStatus
{
    Available,
    Exited,
    Unavailable
}

internal sealed record ProcessIdentitySnapshot(
    int ProcessId,
    string ProcessName,
    DateTimeOffset StartUtc,
    string ExecutablePath);

internal sealed record ProcessIdentityRead(
    ProcessIdentityReadStatus Status,
    ProcessIdentitySnapshot? Identity)
{
    public static ProcessIdentityRead Exited { get; } = new(ProcessIdentityReadStatus.Exited, null);
    public static ProcessIdentityRead Unavailable { get; } = new(ProcessIdentityReadStatus.Unavailable, null);

    public static ProcessIdentityRead Available(ProcessIdentitySnapshot identity)
    {
        return new ProcessIdentityRead(ProcessIdentityReadStatus.Available, identity);
    }
}

internal interface IProcessIdentityProvider
{
    ProcessIdentityRead Read(int processId);
}

internal interface IAudioRoutingPolicy : IDisposable
{
    bool IsAvailable { get; }
    PersistedEndpoint GetPersistedEndpoint(int processId);
    bool SetPersistedEndpoint(int processId, string endpointId);
    bool ClearPersistedEndpoint(int processId);
}

internal static class RouteOwnershipDecisions
{
    public static bool TargetIdentityMatches(
        ProcessRouteTarget target,
        ProcessIdentitySnapshot expectedIdentity,
        ProcessIdentityRead currentRead)
    {
        return ProcessIdentityMatches(expectedIdentity, currentRead) &&
               target.ProcessId == expectedIdentity.ProcessId &&
               target.ProcessName.Equals(expectedIdentity.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               (target.ProcessStartUtc is null || target.ProcessStartUtc.Value.UtcTicks == expectedIdentity.StartUtc.UtcTicks);
    }

    public static bool ProcessIdentityMatches(
        ProcessIdentitySnapshot expectedIdentity,
        ProcessIdentityRead currentRead)
    {
        if (currentRead.Status != ProcessIdentityReadStatus.Available || currentRead.Identity is null)
        {
            return false;
        }

        var currentIdentity = currentRead.Identity;
        var expectedPath = NormalizeExecutablePath(expectedIdentity.ExecutablePath);
        var currentPath = NormalizeExecutablePath(currentIdentity.ExecutablePath);
        return expectedPath is not null &&
               currentPath is not null &&
               expectedIdentity.ProcessId == currentIdentity.ProcessId &&
               expectedIdentity.ProcessName.Equals(currentIdentity.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               expectedIdentity.StartUtc.UtcTicks == currentIdentity.StartUtc.UtcTicks &&
               expectedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanClearManagedRoute(
        ManagedRoute route,
        int currentProcessId,
        string? currentExecutablePath)
    {
        return EvaluateProcessIdentity(route, currentProcessId, currentExecutablePath) ==
               ManagedRouteIdentityStatus.Match;
    }

    public static ManagedRouteIdentityStatus EvaluateProcessIdentity(
        ManagedRoute route,
        int currentProcessId,
        string? currentExecutablePath)
    {
        if (route.ProcessId != currentProcessId)
        {
            return ManagedRouteIdentityStatus.Mismatch;
        }

        var ownedPath = NormalizeExecutablePath(route.ExecutablePath);
        var currentPath = NormalizeExecutablePath(currentExecutablePath);
        if (ownedPath is null || currentPath is null)
        {
            return ManagedRouteIdentityStatus.Unavailable;
        }

        return ownedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase)
            ? ManagedRouteIdentityStatus.Match
            : ManagedRouteIdentityStatus.Mismatch;
    }

    public static bool CanRebindDormantRoute(
        ManagedRoute route,
        int newProcessId,
        string? newExecutablePath,
        PersistedEndpoint currentEndpoint,
        OwnedProcessIdentityStatus ownedProcessStatus)
    {
        if (route.ProcessId == newProcessId ||
            ownedProcessStatus != OwnedProcessIdentityStatus.Dormant ||
            !currentEndpoint.HasExplicitEndpoint ||
            !string.Equals(route.EndpointId.Trim(), currentEndpoint.EndpointId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ownedPath = NormalizeExecutablePath(route.ExecutablePath);
        var newPath = NormalizeExecutablePath(newExecutablePath);
        return ownedPath is not null &&
               newPath is not null &&
               ownedPath.Equals(newPath, StringComparison.OrdinalIgnoreCase);
    }

    public static OwnedProcessIdentityStatus EvaluateOwnedProcessIdentity(
        ManagedRoute route,
        ProcessIdentityRead processRead)
    {
        if (processRead.Status == ProcessIdentityReadStatus.Exited)
        {
            return OwnedProcessIdentityStatus.Dormant;
        }

        if (processRead.Status != ProcessIdentityReadStatus.Available || processRead.Identity is null)
        {
            return OwnedProcessIdentityStatus.Unavailable;
        }

        if (route.ExecutablePath is null || route.ProcessStartUtcTicks is null)
        {
            return OwnedProcessIdentityStatus.Unavailable;
        }

        var identity = processRead.Identity;
        var routePath = NormalizeExecutablePath(route.ExecutablePath);
        var identityPath = NormalizeExecutablePath(identity.ExecutablePath);
        if (routePath is null || identityPath is null)
        {
            return OwnedProcessIdentityStatus.Unavailable;
        }

        return route.ProcessId == identity.ProcessId &&
               route.ProcessName.Equals(identity.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               route.ProcessStartUtcTicks.Value == identity.StartUtc.UtcTicks &&
               routePath.Equals(identityPath, StringComparison.OrdinalIgnoreCase)
            ? OwnedProcessIdentityStatus.Match
            : OwnedProcessIdentityStatus.Dormant;
    }

    public static bool ShouldClaimAfterSet(
        bool writeSucceeded,
        string requestedEndpointId,
        PersistedEndpoint readback)
    {
        // Windows can apply some roles while reporting aggregate failure, so
        // matching readback is authoritative for ownership.
        _ = writeSucceeded;
        return readback.HasExplicitEndpoint &&
               string.Equals(readback.EndpointId?.Trim(), requestedEndpointId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static ManagedRouteClearOutcome EvaluateClearReadback(
        PersistedEndpoint readback,
        string ownedEndpointId)
    {
        if (readback.Status == PersistedEndpointStatus.Unavailable)
        {
            return ManagedRouteClearOutcome.ReadbackUnavailable;
        }

        if (readback.IsDefault)
        {
            return ManagedRouteClearOutcome.ClearedToDefault;
        }

        return string.Equals(readback.EndpointId?.Trim(), ownedEndpointId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ManagedRouteClearOutcome.StillOwned
            : ManagedRouteClearOutcome.OwnershipLost;
    }

    public static string? NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

internal sealed record ProcessRouteTarget(int ProcessId, string ProcessName, DateTimeOffset? ProcessStartUtc, MonitorInfo Monitor, AudioEndpoint? Endpoint)
{
    public bool MatchesProcessIdentity(ManagedRoute route)
    {
        if (ProcessId != route.ProcessId ||
            !ProcessName.Equals(route.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return route.ProcessStartUtcTicks is null ||
               ProcessStartUtc is null ||
               route.ProcessStartUtcTicks.Value == ProcessStartUtc.Value.UtcTicks;
    }
}

internal sealed record ProcessRouteTargetBuildResult(
    Dictionary<int, ProcessRouteTarget> Targets,
    HashSet<int> HeldProcessIds);

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void EnsureConfigExists()
    {
        if (File.Exists(Paths.ConfigFile))
        {
            return;
        }

        Save(new RouterSettings());
    }

    public static SettingsLoadResult Load()
    {
        return Load(Paths.ConfigFile);
    }

    internal static SettingsLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(new RouterSettings(), SettingsLoadStatus.Missing, null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<RouterSettings>(json, JsonOptions)
                           ?? throw new JsonException("Configuration must contain a JSON object.");
            return new SettingsLoadResult(settings, SettingsLoadStatus.Valid, null);
        }
        catch (Exception exception)
        {
            Log.Write($"Could not load config: {exception}");
            var settings = new RouterSettings
            {
                Enabled = false
            };
            var errorMessage =
                $"Could not load the configuration. Routing is disabled and autostart was not changed. {exception.Message}";
            return new SettingsLoadResult(settings, SettingsLoadStatus.Invalid, errorMessage);
        }
    }

    public static void Save(RouterSettings settings)
    {
        AtomicFile.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

internal static class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static RouterState Load()
    {
        return Load(Paths.StateFile);
    }

    internal static RouterState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new RouterState();
            }

            var json = File.ReadAllText(path);
            return Normalize(JsonSerializer.Deserialize<RouterState>(json, JsonOptions) ?? new RouterState());
        }
        catch (Exception exception)
        {
            Log.Write($"Could not load state: {exception}");
            return new RouterState();
        }
    }

    public static void Save(RouterState state)
    {
        Save(Paths.StateFile, state);
    }

    internal static void Save(string path, RouterState state)
    {
        state = Normalize(state);
        var serializedState = JsonSerializer.Serialize(state, JsonOptions);
        var serializedBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(serializedState))
            .ToArray();
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(serializedBytes))
        {
            return;
        }

        AtomicFile.WriteAllText(path, serializedState);
    }

    private static RouterState Normalize(RouterState state)
    {
        state.Managed ??= new Dictionary<string, ManagedRoute>(StringComparer.OrdinalIgnoreCase);
        state.PowerResumeManaged ??= new Dictionary<string, ManagedRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in state.Managed.Values.Concat(state.PowerResumeManaged.Values))
        {
            route.ExecutablePath = RouteOwnershipDecisions.NormalizeExecutablePath(route.ExecutablePath);
        }

        return state;
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content, Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }
}

internal static class Log
{
    private const int MaxMessageChars = 4 * 1024;
    private const long MaxLogBytes = 1024 * 1024;
    private const long RetainedLogBytes = 768 * 1024;
    private const string MutexName = @"Local\MonitorAudioRouterLog";
    private static readonly object LockObject = new();
    private static readonly object ThrottleLockObject = new();
    private static readonly Dictionary<string, DateTimeOffset> LastThrottledWrites = new(StringComparer.OrdinalIgnoreCase);

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {SanitizeMessage(message)}{Environment.NewLine}";
            var entryBytes = Encoding.UTF8.GetBytes(line);
            lock (LockObject)
            {
                WriteEntry(entryBytes);
            }
        }
        catch
        {
            // Logging must never break routing.
        }
    }

    public static void WriteThrottled(string key, string message, TimeSpan interval)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (ThrottleLockObject)
            {
                if (LastThrottledWrites.TryGetValue(key, out var previous) &&
                    now - previous < interval)
                {
                    return;
                }

                LastThrottledWrites[key] = now;
            }

            Write(message);
        }
        catch
        {
            // Logging must never break routing.
        }
    }

    private static void WriteEntry(byte[] entryBytes)
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                return;
            }

            Directory.CreateDirectory(Paths.Root);
            TrimIfNeeded(entryBytes.Length);
            using var stream = new FileStream(Paths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(entryBytes, 0, entryBytes.Length);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Best effort only.
                }
            }
        }
    }

    private static string SanitizeMessage(string? message)
    {
        message ??= "";
        message = message.Replace("\r", "\\r").Replace("\n", "\\n");
        return message.Length <= MaxMessageChars
            ? message
            : message[..MaxMessageChars] + "... [truncated]";
    }

    private static void TrimIfNeeded(int incomingBytes)
    {
        if (!File.Exists(Paths.LogFile))
        {
            return;
        }

        var info = new FileInfo(Paths.LogFile);
        if (info.Length + incomingBytes <= MaxLogBytes)
        {
            return;
        }

        var keepBytes = Math.Min(RetainedLogBytes, info.Length);
        var tail = new byte[(int)keepBytes];
        var read = 0;
        using (var readStream = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            readStream.Seek(-keepBytes, SeekOrigin.End);
            while (read < tail.Length)
            {
                var count = readStream.Read(tail, read, tail.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }
        }

        var start = FindFirstCompleteLineStart(tail, read);
        var retainedLength = Math.Max(0, read - start);
        var header = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} Log trimmed; retained last {retainedLength} bytes in single-file rolling log.{Environment.NewLine}");
        using var writeStream = new FileStream(Paths.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writeStream.Write(header, 0, header.Length);
        if (retainedLength > 0)
        {
            writeStream.Write(tail, start, retainedLength);
        }
    }

    private static int FindFirstCompleteLineStart(byte[] buffer, int byteCount)
    {
        for (var bufferIndex = 0; bufferIndex < byteCount; bufferIndex++)
        {
            if (buffer[bufferIndex] == (byte)'\n')
            {
                return bufferIndex + 1;
            }
        }

        return 0;
    }
}

internal static class CommandLineDiagnostics
{
    public static void ListSetupInfo()
    {
        Console.WriteLine(BuildSetupInfo());
    }

    public static string BuildSetupInfo()
    {
        var output = new StringBuilder();
        output.AppendLine("Monitor Audio Router setup info");
        output.AppendLine();
        output.AppendLine($"Config: {Paths.ConfigFile}");
        output.AppendLine($"State:  {Paths.StateFile}");
        output.AppendLine($"Log:    {Paths.LogFile}");
        output.AppendLine();
        output.AppendLine("Monitors:");
        foreach (var monitor in WindowInspector.GetMonitors())
        {
            output.AppendLine($"- {monitor.DeviceName} name={monitor.FriendlyName} id={monitor.DeviceId} bounds={monitor.BoundsKey} primary={monitor.Primary}");
        }

        output.AppendLine();
        output.AppendLine("Render audio endpoints:");
        using var devices = new AudioDeviceManager();
        foreach (var endpoint in devices.GetRenderEndpoints())
        {
            var defaultMark = endpoint.IsDefault ? " default" : "";
            output.AppendLine($"- {endpoint.Name}{defaultMark}");
            output.AppendLine($"  id={endpoint.Id}");
        }

        output.AppendLine();
        output.AppendLine("Use the tray menu's Open config page to assign audio devices to monitors, or use View config JSON there to edit config.json directly.");
        return output.ToString();
    }

    public static void ListAudioSessions()
    {
        using var devices = new AudioDeviceManager();
        using var policy = new AppAudioPolicy();
        var endpoints = devices.GetRenderEndpoints().ToList();

        Console.WriteLine("Active render audio sessions:");
        var sessionsByProcess = devices.GetAudioSessions()
            .Where(session => session.State == AudioSessionState.Active)
            .GroupBy(session => session.ProcessId)
            .OrderBy(group => group.Key);
        foreach (var sessionGroup in sessionsByProcess)
        {
            var processId = sessionGroup.Key;
            string processName;
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName
                    : process.ProcessName + ".exe";
            }
            catch
            {
                processName = "<exited>";
            }

            var persisted = policy.GetPersistedEndpoint(processId);
            var endpointName = persisted.Status switch
            {
                PersistedEndpointStatus.Default => "Default",
                PersistedEndpointStatus.Unavailable => "Unavailable",
                _ => endpoints.FirstOrDefault(endpoint =>
                         endpoint.Id.Equals(persisted.EndpointId, StringComparison.OrdinalIgnoreCase))?.Name
                     ?? persisted.EndpointId
                     ?? "<unknown>"
            };
            var activeEndpointNames = string.Join(
                ", ",
                sessionGroup
                    .Select(session => session.EndpointName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"- PID {processId} {processName}: persisted {endpointName}; active on {activeEndpointNames}");
        }
    }
}

internal sealed class DeferredDisposalQueue<T> where T : class
{
    private readonly object _lockObject = new();
    private readonly Queue<T> _pending = new();
    private readonly HashSet<T> _queued = new();

    public bool Enqueue(T item)
    {
        lock (_lockObject)
        {
            if (!_queued.Add(item))
            {
                return false;
            }

            _pending.Enqueue(item);
            return true;
        }
    }

    public bool Contains(T item)
    {
        lock (_lockObject)
        {
            return _queued.Contains(item);
        }
    }

    public void Drain(Action<T> dispose)
    {
        while (true)
        {
            T item;
            lock (_lockObject)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                item = _pending.Dequeue();
                _queued.Remove(item);
            }

            dispose(item);
        }
    }
}

internal sealed class DeferredSubscriptionManager<T> : IDisposable where T : class
{
    private sealed class SubscriptionState
    {
        public bool IsTerminal;
        public int CallbackCount;
        public bool DisposalRequested;
        public bool DisposalCommitted;
    }

    private readonly object _lockObject = new();
    // Active items are the synchronously disposable subset of manager-owned items.
    // Terminal callback entry removes an item from that subset until deferred cleanup owns disposal.
    private readonly HashSet<T> _owned = new();
    private readonly HashSet<T> _active = new();
    // Keep disposal commitment visible to late callbacks without retaining disposed COM wrappers.
    private readonly ConditionalWeakTable<T, SubscriptionState> _states = new();
    private readonly DeferredDisposalQueue<T> _pending = new();
    private readonly Action<Action> _scheduleCleanup;
    private readonly Action<T> _disposeItem;
    private bool _disposed;

    public DeferredSubscriptionManager(Action<Action> scheduleCleanup, Action<T> disposeItem)
    {
        _scheduleCleanup = scheduleCleanup;
        _disposeItem = disposeItem;
    }

    public bool TryTakeOwnership(T item)
    {
        var scheduleCleanup = false;
        lock (_lockObject)
        {
            var state = GetStateLocked(item);
            if (state.DisposalCommitted)
            {
                return false;
            }

            var isTerminal = state.IsTerminal || state.DisposalRequested;
            if (_disposed && !isTerminal)
            {
                state.DisposalCommitted = true;
                return false;
            }

            _owned.Add(item);
            if (isTerminal)
            {
                scheduleCleanup = state.CallbackCount == 0
                    && state.DisposalRequested
                    && _pending.Enqueue(item);
            }
            else
            {
                _active.Add(item);
            }
        }

        if (scheduleCleanup)
        {
            _scheduleCleanup(DrainPending);
        }

        return true;
    }

    public void RunTerminalCallback(T item, Action callback)
    {
        if (!TryBeginTerminalCallback(item))
        {
            return;
        }

        try
        {
            callback();
        }
        finally
        {
            CompleteTerminalCallback(item);
        }
    }

    private bool TryBeginTerminalCallback(T item)
    {
        lock (_lockObject)
        {
            var state = GetStateLocked(item);
            if (state.DisposalCommitted)
            {
                return false;
            }

            state.IsTerminal = true;
            _active.Remove(item);
            state.CallbackCount++;
            return true;
        }
    }

    private void CompleteTerminalCallback(T item)
    {
        var scheduleCleanup = false;
        lock (_lockObject)
        {
            var state = GetStateLocked(item);
            state.CallbackCount--;
            if (state.CallbackCount == 0)
            {
                scheduleCleanup = state.DisposalRequested && _pending.Enqueue(item);
            }
        }

        if (scheduleCleanup)
        {
            _scheduleCleanup(DrainPending);
        }
    }

    public void QueueForDisposal(T item)
    {
        var scheduleCleanup = false;
        lock (_lockObject)
        {
            var state = GetStateLocked(item);
            if (state.DisposalCommitted)
            {
                return;
            }

            // Preserve requests from callbacks that overlap shutdown or admission.
            state.DisposalRequested = true;
            scheduleCleanup = state.CallbackCount == 0 && _pending.Enqueue(item);
        }

        if (scheduleCleanup)
        {
            _scheduleCleanup(DrainPending);
        }
    }

    private void DrainPending()
    {
        _pending.Drain(item =>
        {
            var disposalCommitted = false;
            lock (_lockObject)
            {
                disposalCommitted = TryCommitDisposalLocked(item);
            }

            if (disposalCommitted)
            {
                _disposeItem(item);
            }
        });
    }

    private bool TryCommitDisposalLocked(T item)
    {
        var state = GetStateLocked(item);
        if (!_owned.Contains(item) || state.CallbackCount != 0)
        {
            return false;
        }

        _owned.Remove(item);
        _active.Remove(item);
        state.DisposalRequested = false;
        state.DisposalCommitted = true;
        return true;
    }

    private SubscriptionState GetStateLocked(T item) =>
        _states.GetValue(item, static _ => new SubscriptionState());

    public void Dispose()
    {
        var disposalCommitted = new List<T>();
        lock (_lockObject)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var item in _active.ToArray())
            {
                if (TryCommitDisposalLocked(item))
                {
                    disposalCommitted.Add(item);
                }
            }
        }

        foreach (var item in disposalCommitted)
        {
            _disposeItem(item);
        }
    }
}

internal sealed class AudioEventWatcher : IDisposable
{
    private readonly Action<string> _requestBurst;
    private readonly Action<Action> _scheduleSessionCleanup;
    private readonly object _lockObject = new();
    private IMMDeviceEnumerator? _enumerator;
    private AudioEndpointNotificationClient? _endpointClient;
    private readonly List<AudioSessionDeviceSubscription> _sessionSubscriptions = new();
    private bool _disposed;

    public AudioEventWatcher(Action<string> requestBurst, Action<Action> scheduleSessionCleanup)
    {
        _requestBurst = requestBurst;
        _scheduleSessionCleanup = scheduleSessionCleanup;
    }

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            _endpointClient = new AudioEndpointNotificationClient(OnEndpointEvent);
            var hResult = _enumerator.RegisterEndpointNotificationCallback(_endpointClient);
            if (hResult != 0)
            {
                Log.Write($"Audio endpoint notification registration failed: HRESULT 0x{hResult:X8}.");
            }

            RefreshSubscriptions("audio watcher start");
            Log.Write("Audio event watcher started.");
        }
        catch (Exception exception)
        {
            Log.WriteThrottled(
                "audio-watcher-start-failed:" + exception.Message,
                $"Audio event watcher unavailable: {exception.Message}",
                TimeSpan.FromMinutes(5));
        }
    }

    public void RefreshSubscriptions(string reason)
    {
        lock (_lockObject)
        {
            if (_disposed || _enumerator is null)
            {
                return;
            }

            foreach (var subscription in _sessionSubscriptions)
            {
                subscription.Dispose();
            }

            _sessionSubscriptions.Clear();

            var hResult = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
            if (hResult != 0 || collection is null)
            {
                Log.WriteThrottled(
                    "audio-session-enum-failed:" + hResult,
                    $"Audio session subscription refresh failed: HRESULT 0x{hResult:X8}.",
                    TimeSpan.FromMinutes(5));
                return;
            }

            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                for (uint i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(collection.Item(i, out var devicePtr));
                    var device = ComInterop.CreateUniqueObject<IMMDevice>(devicePtr);
                    if (device is null)
                    {
                        continue;
                    }

                    try
                    {
                        var subscription = AudioSessionDeviceSubscription.TryCreate(
                            device,
                            OnAudioSessionEvent,
                            _scheduleSessionCleanup);
                        device = null!;
                        if (subscription is not null)
                        {
                            _sessionSubscriptions.Add(subscription);
                        }
                    }
                    finally
                    {
                        if (device is not null)
                        {
                            ComInterop.FinalRelease(device);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteThrottled(
                    "audio-session-subscribe-failed:" + exception.Message,
                    $"Audio session subscription refresh failed: {exception.Message}",
                    TimeSpan.FromMinutes(5));
            }
            finally
            {
                ComInterop.FinalRelease(collection);
            }
        }

        _requestBurst(reason);
    }

    private void OnEndpointEvent(string reason)
    {
        _requestBurst(reason);
        RefreshSubscriptions(reason);
    }

    private void OnAudioSessionEvent(string reason)
    {
        _requestBurst(reason);
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            _disposed = true;

            foreach (var subscription in _sessionSubscriptions)
            {
                subscription.Dispose();
            }

            _sessionSubscriptions.Clear();

            if (_enumerator is not null)
            {
                if (_endpointClient is not null)
                {
                    _ = _enumerator.UnregisterEndpointNotificationCallback(_endpointClient);
                }

                ComInterop.FinalRelease(_enumerator);
                _enumerator = null;
            }

            _endpointClient = null;
        }
    }
}

internal sealed class AudioSessionDeviceSubscription : IDisposable
{
    private readonly IAudioSessionManager2 _manager;
    private readonly AudioSessionNotificationClient _sessionNotification;
    private readonly Action<string> _requestBurst;
    private readonly object _lifecycleLock = new();
    private readonly DeferredSubscriptionManager<AudioSessionControlSubscription> _controls;
    private bool _disposed;

    private AudioSessionDeviceSubscription(
        IAudioSessionManager2 manager,
        Action<string> requestBurst,
        Action<Action> scheduleCleanup)
    {
        _manager = manager;
        _requestBurst = requestBurst;
        _sessionNotification = new AudioSessionNotificationClient(RegisterNewSession);
        _controls = new DeferredSubscriptionManager<AudioSessionControlSubscription>(
            scheduleCleanup,
            control => control.Dispose());
    }

    public static AudioSessionDeviceSubscription? TryCreate(
        IMMDevice device,
        Action<string> requestBurst,
        Action<Action> scheduleCleanup)
    {
        var interfaceId = typeof(IAudioSessionManager2).GUID;
        var managerPtr = IntPtr.Zero;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            var hResult = device.Activate(ref interfaceId, 23, IntPtr.Zero, out managerPtr);
            if (hResult != 0 || managerPtr == IntPtr.Zero)
            {
                return null;
            }

            manager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(managerPtr);
            if (manager.GetSessionEnumerator(out sessionEnumerator) != 0 || sessionEnumerator is null)
            {
                return null;
            }

            var subscription = new AudioSessionDeviceSubscription(manager, requestBurst, scheduleCleanup);
            manager = null;
            subscription.RegisterExistingSessions(sessionEnumerator);
            hResult = subscription._manager.RegisterSessionNotification(subscription._sessionNotification);
            if (hResult != 0)
            {
                Log.WriteThrottled(
                    "audio-session-notification-register-failed:" + hResult,
                    $"Audio session notification registration failed: HRESULT 0x{hResult:X8}.",
                    TimeSpan.FromMinutes(5));
            }

            return subscription;
        }
        catch (Exception exception)
        {
            Log.WriteThrottled(
                "audio-session-device-subscribe-failed:" + exception.Message,
                $"Audio session subscription failed: {exception.Message}",
                TimeSpan.FromMinutes(5));
            return null;
        }
        finally
        {
            if (sessionEnumerator is not null && Marshal.IsComObject(sessionEnumerator))
            {
                ComInterop.FinalRelease(sessionEnumerator);
            }

            if (manager is not null && Marshal.IsComObject(manager))
            {
                ComInterop.FinalRelease(manager);
            }

            if (managerPtr != IntPtr.Zero)
            {
                Marshal.Release(managerPtr);
            }

            ComInterop.FinalRelease(device);
        }
    }

    private void RegisterExistingSessions(IAudioSessionEnumerator sessionEnumerator)
    {
        if (sessionEnumerator.GetCount(out var count) != 0)
        {
            return;
        }

        for (var sessionIndex = 0; sessionIndex < count; sessionIndex++)
        {
            if (sessionEnumerator.GetSession(sessionIndex, out var control) != 0 || control is null)
            {
                continue;
            }

            RegisterSession(control, requestInitialScan: false);
        }
    }

    private void RegisterNewSession(IAudioSessionControl2 control)
    {
        RegisterSession(control, requestInitialScan: true);
    }

    private void RegisterSession(IAudioSessionControl2 control, bool requestInitialScan)
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                ComInterop.FinalRelease(control);
                return;
            }
        }

        try
        {
            var subscription = AudioSessionControlSubscription.TryCreate(
                control,
                _requestBurst,
                disconnectedControl => _controls.RunTerminalCallback(
                    disconnectedControl,
                    () => QueueDisconnectedControl(disconnectedControl)));
            if (subscription is not null)
            {
                control = null!;
                if (!_controls.TryTakeOwnership(subscription))
                {
                    subscription.Dispose();
                }
            }
        }
        finally
        {
            if (control is not null && Marshal.IsComObject(control))
            {
                ComInterop.FinalRelease(control);
            }
        }

        if (requestInitialScan)
        {
            _requestBurst("audio session created");
        }
    }

    private void QueueDisconnectedControl(AudioSessionControlSubscription control)
    {
        if (!control.MarkDisconnected())
        {
            return;
        }

        _controls.QueueForDisposal(control);
        _requestBurst("audio session disconnected");
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = _manager.UnregisterSessionNotification(_sessionNotification);
        _controls.Dispose();

        if (Marshal.IsComObject(_manager))
        {
            ComInterop.FinalRelease(_manager);
        }
    }
}

internal sealed class AudioSessionControlSubscription : IDisposable
{
    private readonly IAudioSessionControl2 _control;
    private readonly AudioSessionEventsClient _events;
    private int _disconnected;
    private bool _disposed;

    private AudioSessionControlSubscription(IAudioSessionControl2 control, AudioSessionEventsClient eventsClient)
    {
        _control = control;
        _events = eventsClient;
    }

    public static AudioSessionControlSubscription? TryCreate(
        IAudioSessionControl2 control,
        Action<string> requestBurst,
        Action<AudioSessionControlSubscription> onDisconnected)
    {
        AudioSessionControlSubscription? subscription = null;
        var eventsClient = new AudioSessionEventsClient(requestBurst, () =>
        {
            var disconnectedSubscription = Volatile.Read(ref subscription);
            if (disconnectedSubscription is not null)
            {
                onDisconnected(disconnectedSubscription);
            }
        });
        subscription = new AudioSessionControlSubscription(control, eventsClient);
        var hResult = control.RegisterAudioSessionNotification(eventsClient);
        if (hResult != 0)
        {
            Log.WriteThrottled(
                "audio-session-events-register-failed:" + hResult,
                $"Audio session state notification registration failed: HRESULT 0x{hResult:X8}.",
                TimeSpan.FromMinutes(5));
            return null;
        }

        if (control.GetState(out var state) == 0 && state == AudioSessionState.Active)
        {
            requestBurst("audio session active");
        }

        return subscription;
    }

    public bool MarkDisconnected()
    {
        return Interlocked.Exchange(ref _disconnected, 1) == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = _control.UnregisterAudioSessionNotification(_events);
        if (Marshal.IsComObject(_control))
        {
            ComInterop.FinalRelease(_control);
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class AudioEndpointNotificationClient : IMMNotificationClient
{
    private readonly Action<string> _requestBurst;

    public AudioEndpointNotificationClient(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
    }

    public int OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        _requestBurst("audio endpoint state changed");
        return 0;
    }

    public int OnDeviceAdded(string deviceId)
    {
        _requestBurst("audio endpoint added");
        return 0;
    }

    public int OnDeviceRemoved(string deviceId)
    {
        _requestBurst("audio endpoint removed");
        return 0;
    }

    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow == EDataFlow.eRender || flow == EDataFlow.eAll)
        {
            _requestBurst("default render endpoint changed");
        }

        return 0;
    }

    public int OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        _requestBurst("audio endpoint property changed");
        return 0;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class AudioSessionNotificationClient : IAudioSessionNotification
{
    private readonly Action<IAudioSessionControl2> _onSessionCreated;

    public AudioSessionNotificationClient(Action<IAudioSessionControl2> onSessionCreated)
    {
        _onSessionCreated = onSessionCreated;
    }

    public int OnSessionCreated(IAudioSessionControl2 newSession)
    {
        _onSessionCreated(newSession);
        return 0;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class AudioSessionEventsClient : IAudioSessionEvents
{
    private readonly Action<string> _requestBurst;
    private readonly Action _onDisconnected;

    public AudioSessionEventsClient(Action<string> requestBurst, Action onDisconnected)
    {
        _requestBurst = requestBurst;
        _onDisconnected = onDisconnected;
    }

    public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) => 0;

    public int OnIconPathChanged(string newIconPath, IntPtr eventContext) => 0;

    public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext) => 0;

    public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext) => 0;

    public int OnGroupingParamChanged(ref Guid newGroupingParam, IntPtr eventContext) => 0;

    public int OnStateChanged(AudioSessionState newState)
    {
        if (newState == AudioSessionState.Active)
        {
            _requestBurst("audio session active");
        }

        return 0;
    }

    public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
    {
        _onDisconnected();
        return 0;
    }
}

internal static class ComInterop
{
    public static T? CreateUniqueObject<T>(IntPtr pointer) where T : class
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (T)Marshal.GetUniqueObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public static void FinalRelease(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (InvalidComObjectException)
        {
            // The wrapper was already detached by another COM cleanup path.
        }
    }
}

internal sealed class AudioDeviceManager : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;

    public AudioDeviceManager()
    {
        _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
    }

    public IEnumerable<AudioEndpoint> GetRenderEndpoints()
    {
        var defaultEndpoint = GetDefaultRenderEndpoint();
        var defaultId = defaultEndpoint?.Id;
        Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection));
        try
        {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(i, out var devicePtr));
                var device = ComInterop.CreateUniqueObject<IMMDevice>(devicePtr);
                if (device is null)
                {
                    continue;
                }

                try
                {
                    var endpoint = ReadEndpoint(device, defaultId);
                    if (endpoint is not null)
                    {
                        yield return endpoint;
                    }
                }
                finally
                {
                    ComInterop.FinalRelease(device);
                }
            }
        }
        finally
        {
            ComInterop.FinalRelease(collection);
        }
    }

    public AudioEndpoint? GetDefaultRenderEndpoint()
    {
        var hResult = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var devicePtr);
        if (hResult != 0 || devicePtr == IntPtr.Zero)
        {
            return null;
        }

        var device = ComInterop.CreateUniqueObject<IMMDevice>(devicePtr);
        if (device is null)
        {
            return null;
        }

        try
        {
            return ReadEndpoint(device, null);
        }
        finally
        {
            ComInterop.FinalRelease(device);
        }
    }

    public IEnumerable<int> GetAudioSessionProcessIds()
    {
        return GetAudioSessions()
            .Select(session => session.ProcessId)
            .Where(processId => processId > 0);
    }

    public IEnumerable<AudioSessionInfo> GetAudioSessions()
    {
        foreach (var device in GetRenderDevices())
        {
            AudioEndpoint? endpoint;
            try
            {
                endpoint = ReadEndpoint(device, null);
            }
            catch
            {
                ComInterop.FinalRelease(device);
                continue;
            }

            if (endpoint is null)
            {
                ComInterop.FinalRelease(device);
                continue;
            }

            foreach (var session in GetAudioSessions(device, endpoint))
            {
                yield return session;
            }
        }
    }

    private IEnumerable<IMMDevice> GetRenderDevices()
    {
        Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection));
        try
        {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(i, out var devicePtr));
                var device = ComInterop.CreateUniqueObject<IMMDevice>(devicePtr);
                if (device is null)
                {
                    continue;
                }

                yield return device;
            }
        }
        finally
        {
            ComInterop.FinalRelease(collection);
        }
    }

    private static IEnumerable<AudioSessionInfo> GetAudioSessions(IMMDevice device, AudioEndpoint endpoint)
    {
        var interfaceId = typeof(IAudioSessionManager2).GUID;
        var managerPtr = IntPtr.Zero;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            var hResult = device.Activate(ref interfaceId, 23, IntPtr.Zero, out managerPtr);
            if (hResult != 0 || managerPtr == IntPtr.Zero)
            {
                yield break;
            }

            manager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(managerPtr);
            if (manager.GetSessionEnumerator(out enumerator) != 0 || enumerator is null)
            {
                yield break;
            }

            if (enumerator.GetCount(out var count) != 0)
            {
                yield break;
            }

            for (var sessionIndex = 0; sessionIndex < count; sessionIndex++)
            {
                if (enumerator.GetSession(sessionIndex, out var control) != 0 || control is null)
                {
                    continue;
                }

                try
                {
                    var state = AudioSessionState.Inactive;
                    _ = control.GetState(out state);
                    if (control.GetProcessId(out var processId) == 0 &&
                        processId > 0)
                    {
                        yield return new AudioSessionInfo(
                            (int)processId,
                            endpoint.Id,
                            endpoint.Name,
                            state);
                    }
                }
                finally
                {
                    ComInterop.FinalRelease(control);
                }
            }
        }
        finally
        {
            if (enumerator is not null && Marshal.IsComObject(enumerator))
            {
                ComInterop.FinalRelease(enumerator);
            }

            if (manager is not null && Marshal.IsComObject(manager))
            {
                ComInterop.FinalRelease(manager);
            }

            if (managerPtr != IntPtr.Zero)
            {
                Marshal.Release(managerPtr);
            }

            ComInterop.FinalRelease(device);
        }
    }

    private static AudioEndpoint? ReadEndpoint(IMMDevice device, string? defaultId)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var id));
        try
        {
            var idText = Marshal.PtrToStringUni(id) ?? "";
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out var store));
            try
            {
                var name = ReadStringProperty(store, PropertyKeys.PKEY_Device_FriendlyName) ?? idText;
                return new AudioEndpoint(idText, name, string.Equals(idText, defaultId, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                ComInterop.FinalRelease(store);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
        }
    }

    private static string? ReadStringProperty(IPropertyStore store, PropertyKey key)
    {
        var propKey = key;
        Marshal.ThrowExceptionForHR(store.GetValue(ref propKey, out var value));
        try
        {
            return value.AsString();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    public void Dispose()
    {
        // MMDeviceEnumerator can be returned through a shared RCW. Let the runtime
        // release this short-lived wrapper so it cannot detach AudioEventWatcher's
        // long-lived notification enumerator during config reload.
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

internal sealed record AudioEndpoint(string Id, string Name, bool IsDefault);

internal sealed record AudioSessionInfo(
    int ProcessId,
    string EndpointId,
    string EndpointName,
    AudioSessionState State);

// Windows exposes the Volume Mixer per-app output setting through internal COM
// interfaces. Keep this class as the narrow boundary around that unsupported
// API: callers ask for get/set/clear by PID, and this class handles Windows'
// endpoint ID packing and version-specific factory variants.
internal sealed class WindowsProcessIdentityProvider : IProcessIdentityProvider
{
    public ProcessIdentityRead Read(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return ProcessIdentityRead.Exited;
        }
        catch
        {
            return ProcessIdentityRead.Unavailable;
        }

        using (process)
        {
            try
            {
                var processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName
                    : process.ProcessName + ".exe";
                var executablePath = RouteOwnershipDecisions.NormalizeExecutablePath(process.MainModule?.FileName);
                if (executablePath is null)
                {
                    return ProcessIdentityRead.Unavailable;
                }

                return ProcessIdentityRead.Available(new ProcessIdentitySnapshot(
                    processId,
                    processName,
                    process.StartTime.ToUniversalTime(),
                    executablePath));
            }
            catch
            {
                return ProcessIdentityRead.Unavailable;
            }
        }
    }
}

internal sealed class AppAudioPolicy : IAudioRoutingPolicy
{
    private const string AudioRenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string AudioCaptureInterface = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const string MMDevApiToken = @"\\?\SWD#MMDEVAPI#";

    private readonly IAudioPolicyConfigFactory? _policy;

    public bool IsAvailable => _policy is not null;

    public AppAudioPolicy()
    {
        _policy = TryCreateWinRtFactory() ?? TryCreateComFactory();
    }

    internal AppAudioPolicy(IAudioPolicyConfigFactory policy)
    {
        _policy = policy;
    }

    public PersistedEndpoint GetPersistedEndpoint(int processId)
    {
        if (_policy is null)
        {
            return PersistedEndpoint.Unavailable;
        }

        var roleReadFailed = false;
        string? explicitEndpointId = null;
        var conflictingExplicitEndpoints = false;
        foreach (var role in ManagedRoles())
        {
            var hResult = _policy.GetPersistedDefaultAudioEndpoint((uint)processId, EDataFlow.eRender, role, out var endpoint);
            if (hResult != 0)
            {
                roleReadFailed = true;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(endpoint) &&
                !endpoint.Equals("DefaultRenderDevice", StringComparison.OrdinalIgnoreCase))
            {
                var roleEndpointId = UnpackDeviceId(endpoint);
                if (explicitEndpointId is null)
                {
                    explicitEndpointId = roleEndpointId;
                }
                else if (!explicitEndpointId.Equals(roleEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    conflictingExplicitEndpoints = true;
                }
            }
        }

        if (explicitEndpointId is not null && !conflictingExplicitEndpoints)
        {
            return PersistedEndpoint.Explicit(explicitEndpointId);
        }

        if (conflictingExplicitEndpoints)
        {
            return PersistedEndpoint.Unavailable;
        }

        return roleReadFailed ? PersistedEndpoint.Unavailable : PersistedEndpoint.Default;
    }

    public bool SetPersistedEndpoint(int processId, string endpointId)
    {
        if (_policy is null)
        {
            return false;
        }

        return SetForAllRoles(processId, endpointId);
    }

    public bool ClearPersistedEndpoint(int processId)
    {
        if (_policy is null)
        {
            return false;
        }

        return SetForAllRoles(processId, null);
    }

    private bool SetForAllRoles(int processId, string? endpointId)
    {
        var allRolesSucceeded = true;
        var policyEndpointId = endpointId is null ? null : GenerateDeviceId(endpointId, EDataFlow.eRender);

        // Volume Mixer may query different roles depending on the app and
        // Windows build. Set every render role so readback and playback agree.
        foreach (var role in ManagedRoles())
        {
            var hResult = _policy!.SetPersistedDefaultAudioEndpoint((uint)processId, EDataFlow.eRender, role, policyEndpointId);
            if (hResult != 0)
            {
                allRolesSucceeded = false;
                Log.Write($"SetPersistedDefaultAudioEndpoint failed for PID {processId}, role {role}, HRESULT 0x{hResult:X8}.");
            }
        }

        return allRolesSucceeded;
    }

    private static ERole[] ManagedRoles()
    {
        return new[] { ERole.eMultimedia, ERole.eConsole, ERole.eCommunications };
    }

    private static string GenerateDeviceId(string deviceId, EDataFlow flow)
    {
        return $"{MMDevApiToken}{deviceId}{(flow == EDataFlow.eRender ? AudioRenderInterface : AudioCaptureInterface)}";
    }

    private static string UnpackDeviceId(string deviceId)
    {
        if (deviceId.StartsWith(MMDevApiToken, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId[MMDevApiToken.Length..];
        }

        if (deviceId.EndsWith(AudioRenderInterface, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId[..^AudioRenderInterface.Length];
        }

        if (deviceId.EndsWith(AudioCaptureInterface, StringComparison.OrdinalIgnoreCase))
        {
            deviceId = deviceId[..^AudioCaptureInterface.Length];
        }

        return deviceId;
    }

    public void Dispose()
    {
        if (_policy is not null)
        {
            _policy.Dispose();
        }
    }

    private IAudioPolicyConfigFactory? TryCreateWinRtFactory()
    {
        return TryCreate21H2Factory() ?? TryCreateDownlevelFactory();
    }

    private static IAudioPolicyConfigFactory? TryCreateComFactory()
    {
        // Older examples used CPolicyConfigClient directly. Newer Windows builds expose
        // per-app routing through Windows.Media.Internal.AudioPolicyConfig instead.
        return null;
    }

    private static IAudioPolicyConfigFactory? TryCreate21H2Factory()
    {
        try
        {
            var interfaceId = typeof(IAudioPolicyConfigFactoryVariantFor21H2).GUID;
            var factory = GetAudioPolicyActivationFactoryPointer(interfaceId);
            return new RawAudioPolicyConfigFactory(factory, "21H2");
        }
        catch (Exception exception)
        {
            Log.Write($"21H2 audio policy factory unavailable: {exception.Message}");
            return null;
        }
    }

    private static IAudioPolicyConfigFactory? TryCreateDownlevelFactory()
    {
        try
        {
            var interfaceId = typeof(IAudioPolicyConfigFactoryVariantForDownlevel).GUID;
            var factory = GetAudioPolicyActivationFactoryPointer(interfaceId);
            return new RawAudioPolicyConfigFactory(factory, "Downlevel");
        }
        catch (Exception exception)
        {
            Log.Write($"Downlevel audio policy factory unavailable: {exception.Message}");
            return null;
        }
    }

    private static IntPtr GetAudioPolicyActivationFactoryPointer(Guid interfaceId)
    {
        var className = "Windows.Media.Internal.AudioPolicyConfig";
        var hstring = IntPtr.Zero;
        var factoryPtr = IntPtr.Zero;
        try
        {
            var hResult = NativeMethods.WindowsCreateString(className, className.Length, out hstring);
            if (hResult != 0)
            {
                Marshal.ThrowExceptionForHR(hResult);
            }

            hResult = NativeMethods.RoGetActivationFactoryRaw(hstring, ref interfaceId, out factoryPtr);
            if (hResult != 0)
            {
                Marshal.ThrowExceptionForHR(hResult);
            }

            var activationFactoryPointer = factoryPtr;
            factoryPtr = IntPtr.Zero;
            return activationFactoryPointer;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }

            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }
}

internal interface IAudioPolicyConfigFactory : IDisposable
{
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId);
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? deviceId);
    int ClearAllPersistedApplicationDefaultEndpoints();
}

internal sealed class AudioPolicyConfigFactory21H2 : IAudioPolicyConfigFactory
{
    private readonly IAudioPolicyConfigFactoryVariantFor21H2 _factory;

    public AudioPolicyConfigFactory21H2(IAudioPolicyConfigFactoryVariantFor21H2 factory)
    {
        _factory = factory;
    }

    public int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId)
    {
        return WithOptionalHString(deviceId, ptr => _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, ptr));
    }

    public int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? deviceId)
    {
        var hResult = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var value);
        deviceId = value;
        return hResult;
    }

    public int ClearAllPersistedApplicationDefaultEndpoints()
    {
        return _factory.ClearAllPersistedApplicationDefaultEndpoints();
    }

    public void Dispose()
    {
        if (Marshal.IsComObject(_factory))
        {
            Marshal.FinalReleaseComObject(_factory);
        }
    }

    private static int WithOptionalHString(string? value, Func<IntPtr, int> action)
    {
        var hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var hResult = NativeMethods.WindowsCreateString(value, value.Length, out hstring);
                if (hResult != 0)
                {
                    return hResult;
                }
            }

            return action(hstring);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }
}

internal sealed class RawAudioPolicyConfigFactory : IAudioPolicyConfigFactory
{
    private const int IInspectableMethodCount = 6;
    private const int AudioPolicyReservedMethodCount = 19;
    private const int SetPersistedDefaultAudioEndpointSlot = IInspectableMethodCount + AudioPolicyReservedMethodCount;
    private const int GetPersistedDefaultAudioEndpointSlot = SetPersistedDefaultAudioEndpointSlot + 1;
    private const int ClearAllPersistedApplicationDefaultEndpointsSlot = SetPersistedDefaultAudioEndpointSlot + 2;

    private readonly string _variant;
    private IntPtr _thisPtr;
    private readonly SetPersistedDefaultAudioEndpointDelegate _set;
    private readonly GetPersistedDefaultAudioEndpointDelegate _get;
    private readonly ClearAllPersistedApplicationDefaultEndpointsDelegate _clear;

    public RawAudioPolicyConfigFactory(IntPtr thisPtr, string variant)
    {
        _thisPtr = thisPtr;
        _variant = variant;
        _set = GetMethod<SetPersistedDefaultAudioEndpointDelegate>(SetPersistedDefaultAudioEndpointSlot);
        _get = GetMethod<GetPersistedDefaultAudioEndpointDelegate>(GetPersistedDefaultAudioEndpointSlot);
        _clear = GetMethod<ClearAllPersistedApplicationDefaultEndpointsDelegate>(ClearAllPersistedApplicationDefaultEndpointsSlot);
        Log.Write($"Using raw AudioPolicyConfigFactory variant {_variant}.");
    }

    public int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId)
    {
        var hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var hResult = NativeMethods.WindowsCreateString(deviceId, deviceId.Length, out hstring);
                if (hResult != 0)
                {
                    return hResult;
                }
            }

            return _set(_thisPtr, processId, flow, role, hstring);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }

    public int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? deviceId)
    {
        deviceId = null;
        var hResult = _get(_thisPtr, processId, flow, role, out var hstring);
        if (hResult != 0 || hstring == IntPtr.Zero)
        {
            return hResult;
        }

        try
        {
            var buffer = NativeMethods.WindowsGetStringRawBuffer(hstring, out var length);
            deviceId = buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer, (int)length);
            return hResult;
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hstring);
        }
    }

    public int ClearAllPersistedApplicationDefaultEndpoints()
    {
        return _clear(_thisPtr);
    }

    public void Dispose()
    {
        if (_thisPtr != IntPtr.Zero)
        {
            Marshal.Release(_thisPtr);
            _thisPtr = IntPtr.Zero;
        }
    }

    private T GetMethod<T>(int slot) where T : Delegate
    {
        var vtbl = Marshal.ReadIntPtr(_thisPtr);
        var method = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(IntPtr thisPtr, uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpointDelegate(IntPtr thisPtr, uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllPersistedApplicationDefaultEndpointsDelegate(IntPtr thisPtr);
}

internal sealed class AudioPolicyConfigFactoryDownlevel : IAudioPolicyConfigFactory
{
    private readonly IAudioPolicyConfigFactoryVariantForDownlevel _factory;

    public AudioPolicyConfigFactoryDownlevel(IAudioPolicyConfigFactoryVariantForDownlevel factory)
    {
        _factory = factory;
    }

    public int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, string? deviceId)
    {
        return WithOptionalHString(deviceId, ptr => _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, ptr));
    }

    public int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out string? deviceId)
    {
        var hResult = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var value);
        deviceId = value;
        return hResult;
    }

    public int ClearAllPersistedApplicationDefaultEndpoints()
    {
        return _factory.ClearAllPersistedApplicationDefaultEndpoints();
    }

    public void Dispose()
    {
        if (Marshal.IsComObject(_factory))
        {
            Marshal.FinalReleaseComObject(_factory);
        }
    }

    private static int WithOptionalHString(string? value, Func<IntPtr, int> action)
    {
        var hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var hResult = NativeMethods.WindowsCreateString(value, value.Length, out hstring);
                if (hResult != 0)
                {
                    return hResult;
                }
            }

            return action(hstring);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }
}

internal enum PersistedEndpointStatus
{
    Default,
    Explicit,
    Unavailable
}

internal sealed record PersistedEndpoint(PersistedEndpointStatus Status, string? EndpointId)
{
    public static PersistedEndpoint Default { get; } = new(PersistedEndpointStatus.Default, null);
    public static PersistedEndpoint Unavailable { get; } = new(PersistedEndpointStatus.Unavailable, null);

    public bool IsDefault => Status == PersistedEndpointStatus.Default;
    public bool HasExplicitEndpoint => Status == PersistedEndpointStatus.Explicit;

    public static PersistedEndpoint Explicit(string endpointId)
    {
        return new PersistedEndpoint(PersistedEndpointStatus.Explicit, endpointId);
    }
}

internal static class WindowInspector
{
    public static List<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var monitors = GetMonitors();

        NativeMethods.EnumWindows((windowHandle, unusedCallbackParameter) =>
        {
            if (!NativeMethods.IsWindowVisible(windowHandle) || NativeMethods.IsIconic(windowHandle))
            {
                return true;
            }

            if (NativeMethods.TryGetCloaked(windowHandle, out var cloaked) && cloaked)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(windowHandle, out var rect))
            {
                return true;
            }

            var bounds = rect.ToRectangle();
            if (bounds.Width < 40 || bounds.Height < 40)
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0)
            {
                return true;
            }

            var processName = GetProcessName((int)processId);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return true;
            }

            var monitor = PickMonitor(bounds, monitors);
            windows.Add(new WindowInfo(
                windowHandle,
                (int)processId,
                processName,
                GetProcessStart((int)processId),
                GetWindowText(windowHandle),
                bounds,
                monitor));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static List<MonitorInfo> GetMonitors()
    {
        return Screen.AllScreens
            .Select(screen =>
            {
                var identity = GetMonitorIdentity(screen.DeviceName);
                return new MonitorInfo(screen.DeviceName, identity.FriendlyName, identity.DeviceId, screen.Bounds, screen.Primary);
            })
            .ToList();
    }

    private static (string FriendlyName, string DeviceId) GetMonitorIdentity(string displayDeviceName)
    {
        try
        {
            var monitor = new NativeMethods.DisplayDevice();
            monitor.cb = Marshal.SizeOf<NativeMethods.DisplayDevice>();
            if (NativeMethods.EnumDisplayDevices(displayDeviceName, 0, ref monitor, 0) &&
                (!string.IsNullOrWhiteSpace(monitor.DeviceString) || !string.IsNullOrWhiteSpace(monitor.DeviceID)))
            {
                var registryName = ReadMonitorFriendlyNameFromRegistry(monitor.DeviceID);
                return (!string.IsNullOrWhiteSpace(registryName) ? registryName : monitor.DeviceString ?? "", monitor.DeviceID ?? "");
            }
        }
        catch
        {
            // Monitor identity is best-effort; routes can still use display name or bounds.
        }

        return ("", "");
    }

    private static string? ReadMonitorFriendlyNameFromRegistry(string? deviceId)
    {
        var modelId = GetMonitorModelId(deviceId);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        try
        {
            using var modelKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{modelId}");
            if (modelKey is null)
            {
                return null;
            }

            foreach (var instanceName in modelKey.GetSubKeyNames())
            {
                using var instanceKey = modelKey.OpenSubKey(instanceName);
                var friendlyName = NormalizeMonitorFriendlyName(instanceKey?.GetValue("FriendlyName") as string);
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    return friendlyName;
                }
            }
        }
        catch
        {
            // Registry monitor names are best-effort enrichment.
        }

        return null;
    }

    private static string? GetMonitorModelId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var parts = deviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("MONITOR", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    private static string? NormalizeMonitorFriendlyName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var marker = value.LastIndexOf(";(", StringComparison.Ordinal);
        if (marker >= 0 && value.EndsWith(")", StringComparison.Ordinal))
        {
            value = value[(marker + 2)..^1].Trim();
        }
        else if (value.Contains(';'))
        {
            value = value.Split(';', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? value;
        }

        return value.Contains("Generic", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    public static MonitorInfo PickMonitor(Rectangle windowBounds, List<MonitorInfo> monitors)
    {
        return monitors
            .OrderByDescending(monitor => IntersectionArea(windowBounds, monitor.Bounds))
            .FirstOrDefault() ?? monitors.First();
    }

    private static long IntersectionArea(Rectangle firstBounds, Rectangle secondBounds)
    {
        var intersectionLeft = Math.Max((long)firstBounds.Left, secondBounds.Left);
        var intersectionTop = Math.Max((long)firstBounds.Top, secondBounds.Top);
        var intersectionRight = Math.Min((long)firstBounds.Left + firstBounds.Width, (long)secondBounds.Left + secondBounds.Width);
        var intersectionBottom = Math.Min((long)firstBounds.Top + firstBounds.Height, (long)secondBounds.Top + secondBounds.Height);
        return Math.Max(0L, intersectionRight - intersectionLeft) * Math.Max(0L, intersectionBottom - intersectionTop);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static DateTimeOffset? GetProcessStart(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowText(IntPtr windowHandle)
    {
        var windowTextLength = NativeMethods.GetWindowTextLength(windowHandle);
        if (windowTextLength <= 0)
        {
            return "";
        }

        var windowTextBuilder = new StringBuilder(windowTextLength + 1);
        _ = NativeMethods.GetWindowText(windowHandle, windowTextBuilder, windowTextBuilder.Capacity);
        return windowTextBuilder.ToString();
    }
}

internal sealed record WindowInfo(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    DateTimeOffset? ProcessStartUtc,
    string Title,
    Rectangle Bounds,
    MonitorInfo Monitor);

internal sealed record MonitorInfo(string DeviceName, string FriendlyName, string DeviceId, Rectangle Bounds, bool Primary)
{
    public string BoundsKey => $"{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}";
}

internal sealed class ProcessSnapshot
{
    public static ProcessSnapshot Empty { get; } = new(new Dictionary<int, List<ProcessInfo>>());

    private readonly Dictionary<int, List<ProcessInfo>> _children;

    private ProcessSnapshot(Dictionary<int, List<ProcessInfo>> children)
    {
        _children = children;
    }

    public static ProcessSnapshot Capture()
    {
        var processes = new List<ProcessInfo>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return Empty;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32();
            entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return Empty;
            }

            do
            {
                processes.Add(new ProcessInfo(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile,
                    GetProcessStart((int)entry.th32ProcessID)));
            } while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        var children = processes
            .GroupBy(processInfo => processInfo.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());
        return new ProcessSnapshot(children);
    }

    public IEnumerable<ProcessInfo> GetDescendants(int processId)
    {
        var stack = new Stack<int>();
        stack.Push(processId);
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            if (!_children.TryGetValue(parent, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;
                stack.Push(child.ProcessId);
            }
        }
    }

    private static DateTimeOffset? GetProcessStart(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record ProcessInfo(int ProcessId, int ParentProcessId, string ProcessName, DateTimeOffset? ProcessStartUtc);

internal static class TrayIconFactory
{
    public static Icon Create(bool enabled)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var speakerBrush = new SolidBrush(enabled ? Color.FromArgb(36, 110, 185) : Color.FromArgb(92, 92, 92));
        using var wavePen = new Pen(enabled ? Color.FromArgb(36, 110, 185) : Color.FromArgb(92, 92, 92), 3);
        var speaker = new[]
        {
            new Point(5, 13),
            new Point(11, 13),
            new Point(18, 7),
            new Point(18, 25),
            new Point(11, 19),
            new Point(5, 19)
        };
        graphics.FillPolygon(speakerBrush, speaker);
        graphics.DrawArc(wavePen, 15, 9, 9, 14, -45, 90);
        graphics.DrawArc(wavePen, 18, 6, 11, 20, -45, 90);

        if (!enabled)
        {
            using var disabledIconPen = new Pen(Color.FromArgb(210, 20, 20), 4);
            graphics.DrawLine(disabledIconPen, 20, 20, 30, 30);
            graphics.DrawLine(disabledIconPen, 30, 20, 20, 30);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}

internal static class TestTone
{
    public static byte[] GenerateWav()
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int durationMilliseconds = 400;
        var sampleCount = sampleRate * durationMilliseconds / 1000;
        var dataSize = sampleCount * channels * bitsPerSample / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var timeSeconds = sampleIndex / (double)sampleRate;
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * timeSeconds) * short.MaxValue * 0.04);
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }
}

internal static class NativeConsole
{
    private const int AttachParentProcess = -1;

    public static void AttachToParent()
    {
        NativeMethods.AttachConsole(AttachParentProcess);
        try
        {
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch
        {
            // The process may already have a console or stdout may be redirected.
        }
    }
}

internal static class NativeMethods
{
    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public static readonly IntPtr InvalidHandleValue = new(-1);
    private const int DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public static bool TryGetCloaked(IntPtr hwnd, out bool cloaked)
    {
        var hResult = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var value, sizeof(int));
        cloaked = value != 0;
        return hResult == 0;
    }

    [DllImport("kernel32.dll")]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory")]
    public static extern int RoGetActivationFactoryRaw(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public Rectangle ToRectangle()
    {
        return Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}

internal static class PropertyKeys
{
    public static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;

    public PropertyKey(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr p;
    public int p2;

    public string? AsString()
    {
        const ushort VT_LPWSTR = 31;
        return vt == VT_LPWSTR && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
    }
}

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

internal enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

internal enum AudioSessionDisconnectReason
{
    DeviceRemoval = 0,
    ServerShutdown = 1,
    FormatChanged = 2,
    SessionLogoff = 3,
    SessionDisconnected = 4,
    ExclusiveModeOverride = 5
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject
{
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, DeviceState dwNewState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IntPtr ppDevice);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId(out IntPtr ppstrId);

    [PreserveSig]
    int GetState(out DeviceState pdwState);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant propvar);

    [PreserveSig]
    int Commit();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int sessionCount);

    [PreserveSig]
    int GetSession(int sessionCount, out IAudioSessionControl2 session);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName(out IntPtr displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents newNotifications);
    [PreserveSig] int GetSessionIdentifier(out IntPtr retVal);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr retVal);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(IAudioSessionControl2 newSession);
}

[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string newDisplayName, IntPtr eventContext);

    [PreserveSig]
    int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string newIconPath, IntPtr eventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(float newVolume, [MarshalAs(UnmanagedType.Bool)] bool newMute, IntPtr eventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext);

    [PreserveSig]
    int OnGroupingParamChanged(ref Guid newGroupingParam, IntPtr eventContext);

    [PreserveSig]
    int OnStateChanged(AudioSessionState newState);

    [PreserveSig]
    int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
[Guid("AB3D4648-E242-459F-B02F-541C70306324")]
internal interface IAudioPolicyConfigFactoryVariantFor21H2
{
    [PreserveSig] int Reserved01();
    [PreserveSig] int Reserved02();
    [PreserveSig] int Reserved03();
    [PreserveSig] int Reserved04();
    [PreserveSig] int Reserved05();
    [PreserveSig] int Reserved06();
    [PreserveSig] int Reserved07();
    [PreserveSig] int Reserved08();
    [PreserveSig] int Reserved09();
    [PreserveSig] int Reserved10();
    [PreserveSig] int Reserved11();
    [PreserveSig] int Reserved12();
    [PreserveSig] int Reserved13();
    [PreserveSig] int Reserved14();
    [PreserveSig] int Reserved15();
    [PreserveSig] int Reserved16();
    [PreserveSig] int Reserved17();
    [PreserveSig] int Reserved18();
    [PreserveSig] int Reserved19();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
[Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258")]
internal interface IAudioPolicyConfigFactoryVariantForDownlevel
{
    [PreserveSig] int Reserved01();
    [PreserveSig] int Reserved02();
    [PreserveSig] int Reserved03();
    [PreserveSig] int Reserved04();
    [PreserveSig] int Reserved05();
    [PreserveSig] int Reserved06();
    [PreserveSig] int Reserved07();
    [PreserveSig] int Reserved08();
    [PreserveSig] int Reserved09();
    [PreserveSig] int Reserved10();
    [PreserveSig] int Reserved11();
    [PreserveSig] int Reserved12();
    [PreserveSig] int Reserved13();
    [PreserveSig] int Reserved14();
    [PreserveSig] int Reserved15();
    [PreserveSig] int Reserved16();
    [PreserveSig] int Reserved17();
    [PreserveSig] int Reserved18();
    [PreserveSig] int Reserved19();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}
