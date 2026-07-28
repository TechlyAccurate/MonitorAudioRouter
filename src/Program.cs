using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorAudioRouter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Paths.Initialize();

        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (args.Any(a => a.Equals("--native-host", StringComparison.OrdinalIgnoreCase)) ||
            exeName.Equals("MonitorAudioRouterNativeHost", StringComparison.OrdinalIgnoreCase))
        {
            NativeMessagingHost.Run();
            return 0;
        }
        if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            Cli.ListSetupInfo();
            return 0;
        }

        if (args.Any(a => a.Equals("--list-audio-sessions", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            Cli.ListAudioSessions();
            return 0;
        }

        if (args.Any(a => a.Equals("--scan-once", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            var settings = SettingsStore.Load();
            using var engine = new RoutingEngine(settings);
            var result = engine.Scan();
            Console.WriteLine(result);
            return result.Success ? 0 : 1;
        }

        if (args.Any(a => a.Equals("--clear-managed-routes", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            var settings = SettingsStore.Load();
            using var engine = new RoutingEngine(settings);
            var result = engine.ClearManagedRoutes();
            Console.WriteLine(result);
            return result.Success ? 0 : 1;
        }

        if (args.Any(a => a.Equals("--probe-set-clear", StringComparison.OrdinalIgnoreCase)))
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

            var pid = Environment.ProcessId;
            var setOk = policy.SetPersistedEndpoint(pid, endpoint.Id);
            var afterSet = policy.GetPersistedEndpoint(pid);
            var clearOk = policy.ClearPersistedEndpoint(pid);
            var afterClear = policy.GetPersistedEndpoint(pid);
            Console.WriteLine($"Set current PID {pid} to default endpoint explicitly: {setOk}; readback explicit={afterSet.HasExplicitEndpoint}");
            Console.WriteLine($"Cleared current PID {pid} back to Default: {clearOk}; readback explicit={afterClear.HasExplicitEndpoint}");
            return setOk && clearOk && !afterClear.HasExplicitEndpoint ? 0 : 1;
        }

        if (args.Any(a => a.Equals("--probe-tone-set-clear", StringComparison.OrdinalIgnoreCase)))
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

            var pid = Environment.ProcessId;
            var setOk = policy.SetPersistedEndpoint(pid, endpoint.Id);
            var afterSet = policy.GetPersistedEndpoint(pid);
            var clearOk = policy.ClearPersistedEndpoint(pid);
            var afterClear = policy.GetPersistedEndpoint(pid);
            player.Stop();

            Console.WriteLine($"Set audio-active PID {pid} to default endpoint explicitly: {setOk}; readback explicit={afterSet.HasExplicitEndpoint}");
            Console.WriteLine($"Cleared audio-active PID {pid} back to Default: {clearOk}; readback explicit={afterClear.HasExplicitEndpoint}");
            return setOk && clearOk && !afterClear.HasExplicitEndpoint ? 0 : 1;
        }

        if (args.Any(a => a.Equals("--probe-policy", StringComparison.OrdinalIgnoreCase)))
        {
            NativeConsole.AttachToParent();
            using var policy = new AppAudioPolicy();
            var pid = Environment.ProcessId;
            if (!policy.IsAvailable)
            {
                Console.WriteLine("Policy backend unavailable. See router.log.");
                return 1;
            }

            var endpoint = policy.GetPersistedEndpoint(pid);
            Console.WriteLine(endpoint.HasExplicitEndpoint
                ? $"Policy query OK for PID {pid}: explicit endpoint {endpoint.EndpointId}"
                : $"Policy query OK for PID {pid}: Default");
            return 0;
        }

        using var singleInstance = SingleInstanceLock.TryAcquire();
        if (!singleInstance.Acquired)
        {
            Log.Write("Another Monitor Audio Router tray instance is already running; duplicate instance exiting.");
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new RouterTrayContext();
        Application.Run(context);
        return 0;
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
        catch (Exception ex)
        {
            Log.Write($"Could not migrate {fileName} to user data folder: {ex.Message}");
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
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            mutex?.Dispose();
            Log.Write($"Could not acquire tray single-instance lock; exiting duplicate defensively: {ex.Message}");
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

internal sealed class RouterTrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
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
        _settings = SettingsStore.Load();
        _engine = new RoutingEngine(_settings);
        ApplyAutostartSetting();
        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(_settings.Enabled),
            Text = BuildTooltip(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

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
        _audioEventWatcher = new AudioEventWatcher(reason => _scanScheduler.RequestBurst(reason));

        _hintServer.Start();
        _windowEventWatcher.Start();
        _displayEventWatcher.Start();
        _powerEventWatcher.Start();
        _audioEventWatcher.Start();
        _scanScheduler.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var enabledItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _settings.Enabled,
            CheckOnClick = false
        };
        enabledItem.Click += (_, _) => ToggleEnabled();

        var autostartItem = new ToolStripMenuItem("Autostart")
        {
            Checked = _settings.AutostartEnabled,
            CheckOnClick = false
        };
        autostartItem.Click += (_, _) => ToggleAutostart();

        var scanItem = new ToolStripMenuItem("Scan now");
        scanItem.Click += (_, _) => Scan("manual", force: true);

        var reloadItem = new ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, _) => ReloadConfig();

        var configureRoutesItem = new ToolStripMenuItem("Open config");
        configureRoutesItem.Click += (_, _) => ConfigureRoutes();

        var showInfoItem = new ToolStripMenuItem("Show setup info");
        showInfoItem.Click += (_, _) => MessageBox.Show(Cli.BuildSetupInfo(), "Monitor Audio Router", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(enabledItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(scanItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(configureRoutesItem);
        menu.Items.Add(showInfoItem);
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
        catch (Exception ex)
        {
            _lastStatus = $"Autostart update failed: {ex.Message}";
            Log.Write($"Autostart update failed: {ex}");
            MessageBox.Show(ex.Message, "Could not update autostart", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshTray();
    }

    private void ApplyAutostartSetting()
    {
        try
        {
            StartupRegistration.SetEnabled(_settings.AutostartEnabled);
        }
        catch (Exception ex)
        {
            _lastStatus = $"Autostart update failed: {ex.Message}";
            Log.Write($"Autostart update failed: {ex}");
        }
    }

    private void ReloadConfig()
    {
        _settings = SettingsStore.Load();
        ApplyAutostartSetting();
        _engine.Dispose();
        _engine = new RoutingEngine(_settings);
        _scanScheduler.SetPassiveInterval(Math.Max(500, _settings.PollMilliseconds));
        _audioEventWatcher.RefreshSubscriptions("config reload");
        _lastStatus = "Config reloaded";
        RefreshTray();
        _scanScheduler.RequestBurst("config reload");
    }

    private void ConfigureRoutes()
    {
        try
        {
            using var form = new RouteConfigForm(SettingsStore.Load());
            if (form.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            SettingsStore.Save(form.Settings);
            ReloadConfig();
            _lastStatus = "Routes saved";
            RefreshTray();
            _scanScheduler.RequestBurst("routes saved");
        }
        catch (Exception ex)
        {
            Log.Write($"Route configuration failed: {ex}");
            MessageBox.Show(ex.Message, "Could not configure routes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconFactory.Create(_settings.Enabled);
        oldIcon?.Dispose();
        _notifyIcon.Text = BuildTooltip();
        _notifyIcon.ContextMenuStrip = BuildMenu();
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        _notifyIcon.Dispose();
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
        catch (Exception ex)
        {
            Log.Write($"Exit cleanup failed: {ex}");
        }
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
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var devices = new AudioDeviceManager();
        _endpoints = devices.GetRenderEndpoints()
            .OrderByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
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
            TextAlign = ContentAlignment.MiddleLeft
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
        var defaultEndpoint = _endpoints.FirstOrDefault(e => e.IsDefault);
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
        var route = _settings.MonitorRoutes.FirstOrDefault(r => r.Matches(monitor));
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
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is AudioDeviceChoice choice &&
                choice.Endpoint is not null &&
                string.Equals(choice.Endpoint.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open config JSON", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        var duplicateCount = _monitors.Count(m => string.Equals(GetStableMonitorDeviceId(m.DeviceId), stableId, StringComparison.OrdinalIgnoreCase));
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
        var token = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(char.ToUpperInvariant(ch));
                continue;
            }

            foreach (var value in FlushToken(token))
            {
                yield return value;
            }
        }

        foreach (var value in FlushToken(token))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> FlushToken(StringBuilder token)
    {
        if (token.Length == 0)
        {
            yield break;
        }

        var value = token.ToString();
        token.Clear();
        foreach (var candidate in ExpandToken(value))
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
        foreach (var ch in token)
        {
            if (char.IsLetter(ch))
            {
                letters.Append(ch);
            }
            else if (char.IsDigit(ch))
            {
                digits.Append(ch);
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

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
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
        catch (Exception ex)
        {
            Log.WriteThrottled(
                "scan-scheduler-failed:" + ex.Message,
                $"Scheduled scan failed: {ex.Message}",
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

internal sealed class BrowserHintServer : IDisposable
{
    public const string PipeName = "MonitorAudioRouterHints";
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
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null)
                    {
                        break;
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Write($"Browser hint server error: {ex.Message}");
                await Task.Delay(1000, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
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

internal static class BrowserHintStore
{
    private const int MaxHintWindows = 32;
    private const int MaxProcessIdsPerWindow = 32;
    private const int MaxTitlesPerWindow = 16;
    private const int MaxTitleChars = 256;
    private static readonly object LockObject = new();
    private static readonly Dictionary<string, BrowserHintSet> HintsByProcess = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string> LastLoggedHintSignatures = new(StringComparer.OrdinalIgnoreCase);

    public static bool ApplyJson(string json)
    {
        try
        {
            var update = JsonSerializer.Deserialize<BrowserHintUpdate>(json, JsonOptions);
            if (update?.Type is null || !update.Type.Equals("audibleWindows", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var processName = BrowserToProcessName(update.Browser);
            if (processName is null)
            {
                return false;
            }

            var windows = (update.Windows ?? new List<BrowserHintWindowUpdate>())
                .Where(w => w.Width > 0 && w.Height > 0)
                .Take(MaxHintWindows)
                .Select(w => new BrowserHintWindow(
                    w.WindowId,
                    new Rectangle(w.Left, w.Top, w.Width, w.Height),
                    (w.ProcessIds ?? new List<int>())
                        .Where(pid => pid > 0)
                        .Distinct()
                        .Take(MaxProcessIdsPerWindow)
                        .ToList(),
                    (w.Titles ?? new List<string>())
                        .Select(NormalizeHintTitle)
                        .Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(MaxTitlesPerWindow)
                        .ToList(),
                    (w.WindowTitles ?? new List<string>())
                        .Select(NormalizeHintTitle)
                        .Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(MaxTitlesPerWindow)
                        .ToList()))
                .ToList();

            int? preferredWindowId;
            lock (LockObject)
            {
                HintsByProcess.TryGetValue(processName, out var previous);
                preferredWindowId = DeterminePreferredWindowId(previous, windows);
                HintsByProcess[processName] = new BrowserHintSet(
                    processName,
                    DateTimeOffset.UtcNow,
                    windows,
                    preferredWindowId);
            }

            var changed = LogHintIfChanged(processName, windows, preferredWindowId);
            return changed;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled(
                "browser-hint-parse-error:" + ex.Message,
                $"Browser hint parse error: {ShortError(ex.Message)}",
                TimeSpan.FromMinutes(1));
            return false;
        }
    }

    public static Dictionary<string, BrowserHintSet> GetSnapshot()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-12);
        lock (LockObject)
        {
            foreach (var stale in HintsByProcess.Where(kvp => kvp.Value.UpdatedUtc < cutoff).Select(kvp => kvp.Key).ToList())
            {
                HintsByProcess.Remove(stale);
            }

            return HintsByProcess.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool WindowMatchesHints(Dictionary<string, BrowserHintSet> hints, WindowInfo window)
    {
        if (!hints.TryGetValue(window.ProcessName, out var hintSet))
        {
            return !IsBrowserProcessName(window.ProcessName);
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

    private static string? BrowserToProcessName(string? browser)
    {
        return browser?.ToLowerInvariant() switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            "firefox" => "firefox.exe",
            _ => null
        };
    }

    public static bool IsBrowserProcessName(string processName)
    {
        return processName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static int? DeterminePreferredWindowId(
        BrowserHintSet? previous,
        List<BrowserHintWindow> windows)
    {
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

    private static bool LogHintIfChanged(
        string processName,
        List<BrowserHintWindow> windows,
        int? preferredWindowId)
    {
        var monitors = WindowInspector.GetMonitors();
        var signatureParts = windows
            .Select(w =>
            {
                var monitor = WindowInspector.PickMonitor(w.Bounds, monitors);
                return $"id={w.WindowId};{ShortBounds(w.Bounds)}>{monitor.BoundsKey};pids={CompactPidList(w.ProcessIds)};tabs={w.Titles.Count};active={w.WindowTitles.Count};titles={CompactTitleSignature(w)}";
            });
        var signature = $"{processName}|preferred={preferredWindowId?.ToString() ?? "none"}|{string.Join("|", signatureParts)}";
        lock (LockObject)
        {
            if (LastLoggedHintSignatures.TryGetValue(processName, out var previousSignature) &&
                string.Equals(previousSignature, signature, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            LastLoggedHintSignatures[processName] = signature;
        }

        var summary = string.Join("; ", windows.Select((w, index) =>
        {
            var monitor = WindowInspector.PickMonitor(w.Bounds, monitors);
            var activeTitle = w.WindowTitles.FirstOrDefault() ?? w.Titles.FirstOrDefault() ?? "";
            return $"w{index + 1}@{ShortMonitor(monitor)} bounds={ShortBounds(w.Bounds)} pids={CompactPidList(w.ProcessIds)} tabs={w.Titles.Count} active=\"{ShortLogValue(activeTitle)}\"";
        }));
        Log.Write(
            $"Browser hint: {processName} windows={windows.Count} preferred={preferredWindowId?.ToString() ?? "none"}" +
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
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
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
                     " - Firefox"
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

    private static string ShortLogValue(string value)
    {
        value = value
            .Replace("|", " ")
            .Replace(";", " ")
            .Replace("\"", "'")
            .Replace("\r", " ")
            .Replace("\n", " ");
        return value.Length <= 40 ? value : value[..40] + "...";
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

    private static string CompactPidList(List<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return "none";
        }

        var visible = processIds.Take(3).Select(pid => pid.ToString());
        return processIds.Count <= 3
            ? string.Join(",", visible)
            : string.Join(",", visible) + $"+{processIds.Count - 3}";
    }

    private static string CompactTitleSignature(BrowserHintWindow window)
    {
        var joined = string.Join(
            "\u001F",
            window.Titles
                .Concat(window.WindowTitles)
                .Select(NormalizeTitle)
                .Where(title => title.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(title => title, StringComparer.OrdinalIgnoreCase));
        if (joined.Length == 0)
        {
            return "none";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..10].ToLowerInvariant();
    }

}

internal sealed class BrowserHintUpdate
{
    public string? Type { get; set; }
    public string? Browser { get; set; }
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

internal sealed record BrowserHintSet(
    string ProcessName,
    DateTimeOffset UpdatedUtc,
    List<BrowserHintWindow> Windows,
    int? PreferredWindowId);

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
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string? _token;

    public static string CreateEnvelope(string payloadJson)
    {
        return JsonSerializer.Serialize(new BrowserBridgeEnvelope
        {
            Type = EnvelopeType,
            Token = GetToken(),
            Payload = payloadJson
        });
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
                    if (existing.Length >= 32)
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
        var lengthBytes = ReadExact(input, 4);
        if (lengthBytes is null)
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 1024 * 1024)
        {
            return null;
        }

        var payload = ReadExact(input, length);
        return payload is null ? null : Encoding.UTF8.GetString(payload);
    }

    private static byte[]? ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private static bool ForwardToTray(string json)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", BrowserHintServer.PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(BrowserBridgeSecurity.CreateEnvelope(json));
            return true;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled(
                "native-host-forward-failed:" + ex.Message,
                $"Native host forward failed: {ex.Message}",
                TimeSpan.FromMinutes(5));
            return false;
        }
    }

    private static void WriteMessage(Stream output, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var length = BitConverter.GetBytes(payload.Length);
        output.Write(length, 0, length.Length);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }
}

internal sealed class RoutingEngine : IDisposable
{
    private readonly RouterSettings _settings;
    private readonly AppAudioPolicy _policy = new();
    private readonly RouterState _state;
    private readonly Dictionary<int, string> _lastAmbiguousTarget = new();
    private readonly Dictionary<string, IntPtr> _browserWindowHandles = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _holdManagedRoutesUntilUtc = DateTimeOffset.MinValue;
    private string? _lastDebugSignature;

    public RoutingEngine(RouterSettings settings)
    {
        _settings = settings;
        _state = StateStore.Load();
    }

    public void HoldManagedRoutes(TimeSpan duration, string reason)
    {
        var until = DateTimeOffset.UtcNow.Add(duration);
        if (until > _holdManagedRoutesUntilUtc)
        {
            _holdManagedRoutesUntilUtc = until;
        }

        Log.WriteThrottled(
            $"managed-route-hold-{reason}",
            $"Holding managed route ownership for {duration.TotalSeconds:0}s after {reason}.",
            TimeSpan.FromMinutes(1));
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
            var targetBuild = BuildPidTargets(windows, devices, endpoints);
            var targetPids = targetBuild.Targets;
            var changed = 0;
            var skippedManual = 0;
            var failed = 0;
            var untargeted = ClearUntargetedManagedRoutes(targetPids, targetBuild.HeldPids);
            changed += untargeted.Changed;
            failed += untargeted.Failed;

            foreach (var target in targetPids.Values)
            {
                if (target.Endpoint is null)
                {
                    failed++;
                    Log.Write($"No endpoint matched route for PID {target.ProcessId} ({target.ProcessName}) on {target.Monitor.DeviceName}.");
                    continue;
                }

                var existingState = _state.Get(target.ProcessId);
                if (existingState is not null && !target.MatchesProcessIdentity(existingState))
                {
                    _state.Managed.Remove(target.ProcessId.ToString());
                    existingState = null;
                }

                var currentEndpoint = _policy.GetPersistedEndpoint(target.ProcessId);
                var hasManualOverride = currentEndpoint.HasExplicitEndpoint &&
                                        (existingState is null ||
                                         !EndpointIdsEqual(existingState.EndpointId, currentEndpoint.EndpointId));

                if (hasManualOverride)
                {
                    skippedManual++;
                    var currentEndpointName = endpoints.FirstOrDefault(endpoint =>
                        EndpointIdsEqual(endpoint.Id, currentEndpoint.EndpointId))?.Name
                        ?? currentEndpoint.EndpointId
                        ?? "<unknown>";
                    Log.WriteThrottled(
                        $"manual-audio-override-{target.ProcessId}-{currentEndpoint.EndpointId}",
                        $"Skipped PID {target.ProcessId} ({target.ProcessName}) because Windows Volume Mixer assigns it to {currentEndpointName}. Set its output device to Default to restore automatic routing.",
                        TimeSpan.FromMinutes(5));
                    _state.Managed.Remove(target.ProcessId.ToString());
                    continue;
                }

                var desiredIsSystemDefault = EndpointIdsEqual(target.Endpoint.Id, defaultEndpoint.Id);
                if (desiredIsSystemDefault)
                {
                    if (existingState is not null || currentEndpoint.HasExplicitEndpoint)
                    {
                        if (_policy.ClearPersistedEndpoint(target.ProcessId))
                        {
                            changed++;
                            _state.Managed.Remove(target.ProcessId.ToString());
                        }
                        else
                        {
                            failed++;
                        }
                    }

                    continue;
                }

                if (existingState is not null && EndpointIdsEqual(existingState.EndpointId, target.Endpoint.Id))
                {
                    continue;
                }

                if (_policy.SetPersistedEndpoint(target.ProcessId, target.Endpoint.Id))
                {
                    changed++;
                    _state.Managed[target.ProcessId.ToString()] = ManagedRoute.FromTarget(target);
                }
                else
                {
                    failed++;
                }
            }

            StateStore.Save(_state);
            return new ScanResult(failed == 0, $"Windows: {windows.Count}, targets: {targetPids.Count}", targetPids.Count, changed, skippedManual);
        }
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            return new ScanResult(false, ex.Message, 0, 0, 0);
        }
    }

    public ScanResult ClearManagedRoutes()
    {
        var changed = 0;
        var failed = 0;

        foreach (var route in _state.Managed.Values.ToList())
        {
            var currentEndpoint = _policy.GetPersistedEndpoint(route.ProcessId);
            if (!currentEndpoint.HasExplicitEndpoint)
            {
                _state.Managed.Remove(route.ProcessId.ToString());
                continue;
            }

            if (!EndpointIdsEqual(currentEndpoint.EndpointId, route.EndpointId))
            {
                _state.Managed.Remove(route.ProcessId.ToString());
                continue;
            }

            if (_policy.ClearPersistedEndpoint(route.ProcessId))
            {
                changed++;
                _state.Managed.Remove(route.ProcessId.ToString());
            }
            else
            {
                failed++;
            }
        }

        StateStore.Save(_state);
        return new ScanResult(failed == 0, $"Cleared managed routes: {changed}", 0, changed, 0);
    }

    private (int Changed, int Failed) ClearUntargetedManagedRoutes(
        Dictionary<int, PidTarget> targets,
        HashSet<int> heldPids)
    {
        var changed = 0;
        var failed = 0;

        var temporarilyHoldingManagedRoutes = DateTimeOffset.UtcNow < _holdManagedRoutesUntilUtc;
        foreach (var route in _state.Managed.Values.ToList())
        {
            if (targets.ContainsKey(route.ProcessId))
            {
                continue;
            }

            if (temporarilyHoldingManagedRoutes && ManagedRouteProcessStillMatches(route))
            {
                continue;
            }

            if (heldPids.Contains(route.ProcessId))
            {
                var heldEndpoint = _policy.GetPersistedEndpoint(route.ProcessId);
                if (heldEndpoint.HasExplicitEndpoint &&
                    EndpointIdsEqual(heldEndpoint.EndpointId, route.EndpointId))
                {
                    continue;
                }

                // A user or Windows changed the endpoint while the route was held.
                // Forget our ownership without changing their current selection.
                _state.Managed.Remove(route.ProcessId.ToString());
                continue;
            }

            var currentEndpoint = _policy.GetPersistedEndpoint(route.ProcessId);
            if (currentEndpoint.HasExplicitEndpoint &&
                EndpointIdsEqual(currentEndpoint.EndpointId, route.EndpointId))
            {
                if (_policy.ClearPersistedEndpoint(route.ProcessId))
                {
                    changed++;
                    _state.Managed.Remove(route.ProcessId.ToString());
                }
                else
                {
                    failed++;
                }

                continue;
            }

            _state.Managed.Remove(route.ProcessId.ToString());
        }

        return (changed, failed);
    }

    private static bool ManagedRouteProcessStillMatches(ManagedRoute route)
    {
        var process = GetProcessInfo(route.ProcessId);
        return process is not null && MatchesProcessIdentity(route, process.Value);
    }

    private PidTargetBuildResult BuildPidTargets(
        List<WindowInfo> windows,
        AudioDeviceManager devices,
        List<AudioEndpoint> endpoints)
    {
        var audioSessionPids = devices.GetAudioSessionProcessIds().ToHashSet();
        var processSnapshot = _settings.RouteChildProcesses ? ProcessSnapshot.Capture() : ProcessSnapshot.Empty;
        var hints = BrowserHintStore.GetSnapshot();
        var result = new Dictionary<int, PidTarget>();
        var ambiguousPids = new HashSet<int>();
        var authoritativeHintPids = new HashSet<int>();

        AddHintTargets(
            result,
            ambiguousPids,
            authoritativeHintPids,
            hints,
            audioSessionPids,
            windows,
            endpoints);

        foreach (var window in windows)
        {
            if (!BrowserHintStore.WindowMatchesHints(hints, window))
            {
                continue;
            }

            var endpoint = FindEndpointForMonitor(window.Monitor, endpoints);
            if (audioSessionPids.Contains(window.ProcessId) &&
                !authoritativeHintPids.Contains(window.ProcessId))
            {
                var target = new PidTarget(window.ProcessId, window.ProcessName, window.ProcessStartUtc, window.Monitor, endpoint);
                AddTarget(result, ambiguousPids, target);
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

                if (!audioSessionPids.Contains(child.ProcessId) ||
                    authoritativeHintPids.Contains(child.ProcessId))
                {
                    continue;
                }

                AddTarget(result, ambiguousPids, new PidTarget(child.ProcessId, child.ProcessName, child.ProcessStartUtc, window.Monitor, endpoint));
            }
        }

        foreach (var pid in result.Keys)
        {
            _lastAmbiguousTarget.Remove(pid);
        }

        var heldPids = new HashSet<int>(ambiguousPids);
        foreach (var route in _state.Managed.Values)
        {
            if (result.ContainsKey(route.ProcessId) ||
                !BrowserHintStore.IsBrowserProcessName(route.ProcessName))
            {
                continue;
            }

            var process = GetProcessInfo(route.ProcessId);
            if (process is not null && MatchesProcessIdentity(route, process.Value))
            {
                heldPids.Add(route.ProcessId);
                Log.WriteThrottled(
                    $"held-browser-route-{route.ProcessId}",
                    $"Held the last verified route for PID {route.ProcessId} ({route.ProcessName}) while its browser window is paused or ambiguous.",
                    TimeSpan.FromMinutes(5));
            }
        }

        LogDebugRoutingIfChanged(windows, audioSessionPids, hints, result);

        return new PidTargetBuildResult(result, heldPids);
    }

    private void LogDebugRoutingIfChanged(
        List<WindowInfo> windows,
        HashSet<int> audioSessionPids,
        Dictionary<string, BrowserHintSet> hints,
        Dictionary<int, PidTarget> targets)
    {
        if (!_settings.DebugLogging)
        {
            return;
        }

        static string ShortTitle(string title)
        {
            title = title.Replace("|", " ").Replace(Environment.NewLine, " ");
            return title.Length <= 60 ? title : title[..60] + "...";
        }

        var browserWindows = windows
            .Where(w => BrowserHintStore.IsBrowserProcessName(w.ProcessName))
            .Select(w => $"{w.ProcessId}@{w.Monitor.BoundsKey}:{ShortTitle(w.Title)}");
        var hintSummary = hints.Values.Select(h => $"{h.ProcessName}:{h.Windows.Count}");
        var targetSummary = targets.Values.Select(t => $"{t.ProcessId}->{t.Endpoint?.Name ?? "<none>"}@{t.Monitor.BoundsKey}");
        var signature =
            $"audio=[{string.Join(",", audioSessionPids.OrderBy(pid => pid))}] " +
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
        Dictionary<int, PidTarget> targets,
        HashSet<int> ambiguousPids,
        HashSet<int> authoritativeHintPids,
        Dictionary<string, BrowserHintSet> hints,
        HashSet<int> audioSessionPids,
        List<WindowInfo> windows,
        List<AudioEndpoint> endpoints)
    {
        var monitors = WindowInspector.GetMonitors();
        foreach (var hintSet in hints.Values)
        {
            var usableExplicitProcessIds = hintSet.Windows
                .SelectMany(window => window.ProcessIds)
                .Where(audioSessionPids.Contains)
                .ToHashSet();
            var routeWindows = hintSet.Windows;
            if (hintSet.PreferredWindowId is int preferredWindowId &&
                hintSet.Windows.Count > 1 &&
                usableExplicitProcessIds.Count == 0)
            {
                var preferredWindows = hintSet.Windows
                    .Where(window => window.WindowId == preferredWindowId)
                    .ToList();
                if (preferredWindows.Count == 1)
                {
                    routeWindows = preferredWindows;
                }
            }

            var inferredProcess = routeWindows.Count == 1
                ? audioSessionPids
                    .Select(GetProcessInfo)
                    .Where(process =>
                        process is not null &&
                        process.Value.ProcessName.Equals(hintSet.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                        IsAllowedProcessName(process.Value.ProcessName))
                    .Select(process => process!.Value)
                    .DistinctBy(process => process.ProcessId)
                    .ToList()
                : new List<(int ProcessId, string ProcessName, DateTimeOffset? StartUtc)>();

            foreach (var hintWindow in routeWindows)
            {
                var extensionMonitor = WindowInspector.PickMonitor(hintWindow.Bounds, monitors);
                var matchingWindow = ResolveNativeBrowserWindow(hintSet, hintWindow, windows);
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
                    if (!audioSessionPids.Contains(processId))
                    {
                        continue;
                    }

                    var process = GetProcessInfo(processId);
                    if (process is null || !IsAllowedProcessName(process.Value.ProcessName))
                    {
                        continue;
                    }

                    matchedExplicitProcessIds++;
                    AddTarget(targets, ambiguousPids, new PidTarget(processId, process.Value.ProcessName, process.Value.StartUtc, monitor, endpoint));
                    authoritativeHintPids.Add(processId);
                }

                if (matchedExplicitProcessIds == 0 && inferredProcess.Count == 1)
                {
                    var process = inferredProcess[0];
                    Log.WriteThrottled(
                        $"inferred-browser-route-{process.ProcessId}-{monitor.BoundsKey}",
                        $"Matched PID {process.ProcessId} ({process.ProcessName}) to the sole audible browser window on {monitor.DeviceName}.",
                        TimeSpan.FromMinutes(5));
                    AddTarget(
                        targets,
                        ambiguousPids,
                        new PidTarget(process.ProcessId, process.ProcessName, process.StartUtc, monitor, endpoint));
                    authoritativeHintPids.Add(process.ProcessId);
                }
            }
        }
    }

    private WindowInfo? ResolveNativeBrowserWindow(
        BrowserHintSet hintSet,
        BrowserHintWindow hintWindow,
        List<WindowInfo> windows)
    {
        var candidates = windows
            .Where(window =>
                window.ProcessName.Equals(hintSet.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                BrowserHintStore.WindowMatchesHint(hintWindow, window))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var cacheKey = $"{hintSet.ProcessName}:{hintWindow.WindowId}";
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

    private static (int ProcessId, string ProcessName, DateTimeOffset? StartUtc)? GetProcessInfo(int processId)
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

            return (processId, name, start);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesProcessIdentity(
        ManagedRoute route,
        (int ProcessId, string ProcessName, DateTimeOffset? StartUtc) process)
    {
        if (route.ProcessId != process.ProcessId ||
            !route.ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return route.ProcessStartUtcTicks is null ||
               process.StartUtc is null ||
               route.ProcessStartUtcTicks.Value == process.StartUtc.Value.UtcTicks;
    }

    private void AddTarget(Dictionary<int, PidTarget> targets, HashSet<int> ambiguousPids, PidTarget target)
    {
        if (ambiguousPids.Contains(target.ProcessId))
        {
            return;
        }

        if (targets.TryGetValue(target.ProcessId, out var existing))
        {
            if (!EndpointIdsEqual(existing.Endpoint?.Id, target.Endpoint?.Id))
            {
                targets.Remove(target.ProcessId);
                ambiguousPids.Add(target.ProcessId);
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
        var defaultEndpoint = endpoints.FirstOrDefault(e => e.IsDefault);
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

        if (_settings.IgnoreWindowTitlesContaining.Any(s => !string.IsNullOrWhiteSpace(s) &&
                                                            window.Title.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private bool IsAllowedProcessName(string processName)
    {
        if (_settings.IgnoreProcessNames.Any(p => ProcessNameMatches(processName, p)))
        {
            return false;
        }

        return _settings.AllowProcessNames.Count == 0 ||
               _settings.AllowProcessNames.Any(p => ProcessNameMatches(processName, p));
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
            var match = endpoints.FirstOrDefault(e => e.Id.Contains(idContains, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var match = endpoints.FirstOrDefault(e => e.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
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
    public string EndpointId { get; set; } = "";
    public string EndpointName { get; set; } = "";
    public DateTimeOffset LastSetUtc { get; set; }

    public static ManagedRoute FromTarget(PidTarget target)
    {
        return new ManagedRoute
        {
            ProcessId = target.ProcessId,
            ProcessName = target.ProcessName,
            ProcessStartUtcTicks = target.ProcessStartUtc?.UtcTicks,
            EndpointId = target.Endpoint?.Id ?? "",
            EndpointName = target.Endpoint?.Name ?? "",
            LastSetUtc = DateTimeOffset.UtcNow
        };
    }
}

internal sealed record PidTarget(int ProcessId, string ProcessName, DateTimeOffset? ProcessStartUtc, MonitorInfo Monitor, AudioEndpoint? Endpoint)
{
    public bool MatchesProcessIdentity(ManagedRoute route)
    {
        if (!ProcessName.Equals(route.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return route.ProcessStartUtcTicks is null ||
               ProcessStartUtc is null ||
               route.ProcessStartUtcTicks.Value == ProcessStartUtc.Value.UtcTicks;
    }
}

internal sealed record PidTargetBuildResult(
    Dictionary<int, PidTarget> Targets,
    HashSet<int> HeldPids);

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

    public static RouterSettings Load()
    {
        try
        {
            var json = File.ReadAllText(Paths.ConfigFile);
            return JsonSerializer.Deserialize<RouterSettings>(json, JsonOptions) ?? new RouterSettings();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not load config: {ex}");
            return new RouterSettings();
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
        try
        {
            if (!File.Exists(Paths.StateFile))
            {
                return new RouterState();
            }

            var json = File.ReadAllText(Paths.StateFile);
            return JsonSerializer.Deserialize<RouterState>(json, JsonOptions) ?? new RouterState();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not load state: {ex}");
            return new RouterState();
        }
    }

    public static void Save(RouterState state)
    {
        AtomicFile.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(state, JsonOptions));
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

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
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

    private static int FindFirstCompleteLineStart(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                return i + 1;
            }
        }

        return 0;
    }
}

internal static class Cli
{
    public static void ListSetupInfo()
    {
        Console.WriteLine(BuildSetupInfo());
    }

    public static string BuildSetupInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Monitor Audio Router setup info");
        sb.AppendLine();
        sb.AppendLine($"Config: {Paths.ConfigFile}");
        sb.AppendLine($"State:  {Paths.StateFile}");
        sb.AppendLine($"Log:    {Paths.LogFile}");
        sb.AppendLine();
        sb.AppendLine("Monitors:");
        foreach (var monitor in WindowInspector.GetMonitors())
        {
            sb.AppendLine($"- {monitor.DeviceName} name={monitor.FriendlyName} id={monitor.DeviceId} bounds={monitor.BoundsKey} primary={monitor.Primary}");
        }

        sb.AppendLine();
        sb.AppendLine("Render audio endpoints:");
        using var devices = new AudioDeviceManager();
        foreach (var endpoint in devices.GetRenderEndpoints())
        {
            var defaultMark = endpoint.IsDefault ? " default" : "";
            sb.AppendLine($"- {endpoint.Name}{defaultMark}");
            sb.AppendLine($"  id={endpoint.Id}");
        }

        sb.AppendLine();
        sb.AppendLine("Use the tray menu's Open config page to assign audio devices to monitors, or use View config JSON there to edit config.json directly.");
        return sb.ToString();
    }

    public static void ListAudioSessions()
    {
        using var devices = new AudioDeviceManager();
        using var policy = new AppAudioPolicy();
        var endpoints = devices.GetRenderEndpoints().ToList();

        Console.WriteLine("Active render audio sessions:");
        foreach (var processId in devices.GetAudioSessionProcessIds().Distinct().OrderBy(id => id))
        {
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
            var endpointName = persisted.HasExplicitEndpoint
                ? endpoints.FirstOrDefault(endpoint =>
                    endpoint.Id.Equals(persisted.EndpointId, StringComparison.OrdinalIgnoreCase))?.Name
                  ?? persisted.EndpointId
                  ?? "<unknown>"
                : "Default";
            Console.WriteLine($"- PID {processId} {processName}: {endpointName}");
        }
    }
}

internal sealed class AudioEventWatcher : IDisposable
{
    private readonly Action<string> _requestBurst;
    private readonly object _lockObject = new();
    private IMMDeviceEnumerator? _enumerator;
    private AudioEndpointNotificationClient? _endpointClient;
    private readonly List<AudioSessionDeviceSubscription> _sessionSubscriptions = new();
    private bool _disposed;

    public AudioEventWatcher(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
    }

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            _endpointClient = new AudioEndpointNotificationClient(OnEndpointEvent);
            var hr = _enumerator.RegisterEndpointNotificationCallback(_endpointClient);
            if (hr != 0)
            {
                Log.Write($"Audio endpoint notification registration failed: hr 0x{hr:X8}.");
            }

            RefreshSubscriptions("audio watcher start");
            Log.Write("Audio event watcher started.");
        }
        catch (Exception ex)
        {
            Log.WriteThrottled(
                "audio-watcher-start-failed:" + ex.Message,
                $"Audio event watcher unavailable: {ex.Message}",
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

            var hr = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
            if (hr != 0 || collection is null)
            {
                Log.WriteThrottled(
                    "audio-session-enum-failed:" + hr,
                    $"Audio session subscription refresh failed: hr 0x{hr:X8}.",
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
                        var subscription = AudioSessionDeviceSubscription.TryCreate(device, OnAudioSessionEvent);
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
            catch (Exception ex)
            {
                Log.WriteThrottled(
                    "audio-session-subscribe-failed:" + ex.Message,
                    $"Audio session subscription refresh failed: {ex.Message}",
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
    private readonly List<AudioSessionControlSubscription> _controls = new();
    private bool _disposed;

    private AudioSessionDeviceSubscription(IAudioSessionManager2 manager, Action<string> requestBurst)
    {
        _manager = manager;
        _requestBurst = requestBurst;
        _sessionNotification = new AudioSessionNotificationClient(RegisterNewSession);
    }

    public static AudioSessionDeviceSubscription? TryCreate(IMMDevice device, Action<string> requestBurst)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        var managerPtr = IntPtr.Zero;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            var hr = device.Activate(ref iid, 23, IntPtr.Zero, out managerPtr);
            if (hr != 0 || managerPtr == IntPtr.Zero)
            {
                return null;
            }

            manager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(managerPtr);
            if (manager.GetSessionEnumerator(out sessionEnumerator) != 0 || sessionEnumerator is null)
            {
                return null;
            }

            var subscription = new AudioSessionDeviceSubscription(manager, requestBurst);
            manager = null;
            subscription.RegisterExistingSessions(sessionEnumerator);
            hr = subscription._manager.RegisterSessionNotification(subscription._sessionNotification);
            if (hr != 0)
            {
                Log.WriteThrottled(
                    "audio-session-notification-register-failed:" + hr,
                    $"Audio session notification registration failed: hr 0x{hr:X8}.",
                    TimeSpan.FromMinutes(5));
            }

            return subscription;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled(
                "audio-session-device-subscribe-failed:" + ex.Message,
                $"Audio session subscription failed: {ex.Message}",
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

        for (var i = 0; i < count; i++)
        {
            if (sessionEnumerator.GetSession(i, out var control) != 0 || control is null)
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
        if (_disposed)
        {
            ComInterop.FinalRelease(control);
            return;
        }

        try
        {
            var subscription = AudioSessionControlSubscription.TryCreate(control, _requestBurst);
            if (subscription is not null)
            {
                control = null!;
                _controls.Add(subscription);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = _manager.UnregisterSessionNotification(_sessionNotification);
        foreach (var control in _controls)
        {
            control.Dispose();
        }

        _controls.Clear();
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
    private bool _disposed;

    private AudioSessionControlSubscription(IAudioSessionControl2 control, AudioSessionEventsClient eventsClient)
    {
        _control = control;
        _events = eventsClient;
    }

    public static AudioSessionControlSubscription? TryCreate(IAudioSessionControl2 control, Action<string> requestBurst)
    {
        var eventsClient = new AudioSessionEventsClient(requestBurst);
        var hr = control.RegisterAudioSessionNotification(eventsClient);
        if (hr != 0)
        {
            Log.WriteThrottled(
                "audio-session-events-register-failed:" + hr,
                $"Audio session state notification registration failed: hr 0x{hr:X8}.",
                TimeSpan.FromMinutes(5));
            return null;
        }

        if (control.GetState(out var state) == 0 && state == AudioSessionState.Active)
        {
            requestBurst("audio session active");
        }

        return new AudioSessionControlSubscription(control, eventsClient);
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

    public AudioSessionEventsClient(Action<string> requestBurst)
    {
        _requestBurst = requestBurst;
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

    public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => 0;
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
        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var devicePtr);
        if (hr != 0 || devicePtr == IntPtr.Zero)
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
        foreach (var device in GetRenderDevices())
        {
            foreach (var pid in GetAudioSessionProcessIds(device))
            {
                if (pid > 0)
                {
                    yield return pid;
                }
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

    private static IEnumerable<int> GetAudioSessionProcessIds(IMMDevice device)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        var managerPtr = IntPtr.Zero;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            var hr = device.Activate(ref iid, 23, IntPtr.Zero, out managerPtr);
            if (hr != 0 || managerPtr == IntPtr.Zero)
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

            for (var i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out var control) != 0 || control is null)
                {
                    continue;
                }

                try
                {
                    if (control.GetProcessId(out var pid) == 0)
                    {
                        yield return (int)pid;
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

internal sealed class AppAudioPolicy : IDisposable
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

    public PersistedEndpoint GetPersistedEndpoint(int processId)
    {
        if (_policy is null)
        {
            return PersistedEndpoint.Unavailable;
        }

        foreach (var role in ManagedRoles())
        {
            var hr = _policy.GetPersistedDefaultAudioEndpoint((uint)processId, EDataFlow.eRender, role, out var endpoint);
            if (hr == 0)
            {
                if (!string.IsNullOrWhiteSpace(endpoint) &&
                    !endpoint.Equals("DefaultRenderDevice", StringComparison.OrdinalIgnoreCase))
                {
                    return new PersistedEndpoint(true, UnpackDeviceId(endpoint));
                }
            }
        }

        return PersistedEndpoint.Default;
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
        var ok = true;
        var policyEndpointId = endpointId is null ? null : GenerateDeviceId(endpointId, EDataFlow.eRender);
        foreach (var role in ManagedRoles())
        {
            var hr = _policy!.SetPersistedDefaultAudioEndpoint((uint)processId, EDataFlow.eRender, role, policyEndpointId);
            if (hr != 0)
            {
                ok = false;
                Log.Write($"SetPersistedDefaultAudioEndpoint failed for PID {processId}, role {role}, hr 0x{hr:X8}.");
            }
        }

        return ok;
    }

    private static ERole[] ManagedRoles()
    {
        return new[] { ERole.eMultimedia, ERole.eConsole };
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
            var iid = typeof(IAudioPolicyConfigFactoryVariantFor21H2).GUID;
            var factory = GetAudioPolicyActivationFactoryPointer(iid);
            return new RawAudioPolicyConfigFactory(factory, "21H2");
        }
        catch (Exception ex)
        {
            Log.Write($"21H2 audio policy factory unavailable: {ex.Message}");
            return null;
        }
    }

    private static IAudioPolicyConfigFactory? TryCreateDownlevelFactory()
    {
        try
        {
            var iid = typeof(IAudioPolicyConfigFactoryVariantForDownlevel).GUID;
            var factory = GetAudioPolicyActivationFactoryPointer(iid);
            return new RawAudioPolicyConfigFactory(factory, "Downlevel");
        }
        catch (Exception ex)
        {
            Log.Write($"Downlevel audio policy factory unavailable: {ex.Message}");
            return null;
        }
    }

    private static IntPtr GetAudioPolicyActivationFactoryPointer(Guid iid)
    {
        var className = "Windows.Media.Internal.AudioPolicyConfig";
        var hstring = IntPtr.Zero;
        var factoryPtr = IntPtr.Zero;
        try
        {
            var hr = NativeMethods.WindowsCreateString(className, className.Length, out hstring);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = NativeMethods.RoGetActivationFactoryRaw(hstring, ref iid, out factoryPtr);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var ret = factoryPtr;
            factoryPtr = IntPtr.Zero;
            return ret;
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
        var hr = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var value);
        deviceId = value;
        return hr;
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
                var hr = NativeMethods.WindowsCreateString(value, value.Length, out hstring);
                if (hr != 0)
                {
                    return hr;
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
                var hr = NativeMethods.WindowsCreateString(deviceId, deviceId.Length, out hstring);
                if (hr != 0)
                {
                    return hr;
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
        var hr = _get(_thisPtr, processId, flow, role, out var hstring);
        if (hr != 0 || hstring == IntPtr.Zero)
        {
            return hr;
        }

        try
        {
            var buffer = NativeMethods.WindowsGetStringRawBuffer(hstring, out var length);
            deviceId = buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer, (int)length);
            return hr;
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
        var hr = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var value);
        deviceId = value;
        return hr;
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
                var hr = NativeMethods.WindowsCreateString(value, value.Length, out hstring);
                if (hr != 0)
                {
                    return hr;
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

internal sealed record PersistedEndpoint(bool HasExplicitEndpoint, string? EndpointId)
{
    public static PersistedEndpoint Default { get; } = new(false, null);
    public static PersistedEndpoint Unavailable { get; } = new(false, null);
}

internal static class WindowInspector
{
    public static List<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var monitors = GetMonitors();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            if (NativeMethods.TryGetCloaked(hwnd, out var cloaked) && cloaked)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var bounds = rect.ToRectangle();
            if (bounds.Width < 40 || bounds.Height < 40)
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return true;
            }

            var processName = GetProcessName((int)pid);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return true;
            }

            var monitor = PickMonitor(bounds, monitors);
            windows.Add(new WindowInfo(
                hwnd,
                (int)pid,
                processName,
                GetProcessStart((int)pid),
                GetWindowText(hwnd),
                bounds,
                monitor));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static List<MonitorInfo> GetMonitors()
    {
        return Screen.AllScreens
            .Select(s =>
            {
                var identity = GetMonitorIdentity(s.DeviceName);
                return new MonitorInfo(s.DeviceName, identity.FriendlyName, identity.DeviceId, s.Bounds, s.Primary);
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
            .OrderByDescending(m => IntersectionArea(windowBounds, m.Bounds))
            .FirstOrDefault() ?? monitors.First();
    }

    private static long IntersectionArea(Rectangle a, Rectangle b)
    {
        var x1 = Math.Max((long)a.Left, b.Left);
        var y1 = Math.Max((long)a.Top, b.Top);
        var x2 = Math.Min((long)a.Left + a.Width, (long)b.Left + b.Width);
        var y2 = Math.Min((long)a.Top + a.Height, (long)b.Top + b.Height);
        return Math.Max(0L, x2 - x1) * Math.Max(0L, y2 - y1);
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static DateTimeOffset? GetProcessStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
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
            .GroupBy(p => p.ParentProcessId)
            .ToDictionary(g => g.Key, g => g.ToList());
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

    private static DateTimeOffset? GetProcessStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
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
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

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
        g.FillPolygon(speakerBrush, speaker);
        g.DrawArc(wavePen, 15, 9, 9, 14, -45, 90);
        g.DrawArc(wavePen, 18, 6, 11, 20, -45, 90);

        if (!enabled)
        {
            using var xPen = new Pen(Color.FromArgb(210, 20, 20), 4);
            g.DrawLine(xPen, 20, 20, 30, 30);
            g.DrawLine(xPen, 30, 20, 20, 30);
        }

        var handle = bmp.GetHicon();
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

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * t) * short.MaxValue * 0.04);
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
        var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var value, sizeof(int));
        cloaked = value != 0;
        return hr == 0;
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
