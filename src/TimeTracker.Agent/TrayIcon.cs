using System.Diagnostics;
using System.Windows.Forms;

namespace TimeTracker.Agent;

/// <summary>
/// The Agent's only visible UI: a notification-area icon with a right-click menu, since
/// double-clicking the exe itself shows nothing (it's a background tracker, not a GUI app) -
/// this exists so a person can tell it's running and reach the same troubleshooting check
/// the Start Menu "Test Connection" shortcut offers, without hunting through Start Menu.
/// Must be constructed and disposed on a dedicated STA thread running an
/// <see cref="Application.Run()"/> message loop - see Program.cs.
/// </summary>
public sealed class AgentTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly string _serverUrl;

    public AgentTrayIcon(string serverUrl)
    {
        _serverUrl = serverUrl;

        var menu = _menu = new ContextMenuStrip();
        menu.Items.Add("Settings...", null, OnSettings);
        menu.Items.Add("Test Connection", null, OnTestConnection);
        menu.Items.Add("Open Server", null, OnOpenServer);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _notifyIcon = new NotifyIcon
        {
            // The exe's own icon (ApplicationIcon in the .csproj) - same branded clock icon
            // shown in Explorer/Task Manager, so it's recognizable there too.
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "TimeTracker Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    // WPF needs an Application instance for resource/dispatcher lookups to work (e.g. the
    // DynamicResource bindings in SettingsWindow.xaml), even though nothing here calls its
    // Run() - this thread's own message loop (Application.Run() below, the WinForms one)
    // already pumps messages for both; a WPF Window's ShowDialog() nests its own dispatcher
    // loop inside that the same way any modal Win32 dialog would. Created lazily, once, on
    // first use rather than eagerly at startup, since most sessions never open Settings.
    private System.Windows.Application? _wpfApplication;
    private SettingsWindow? _settingsWindow;

    private void OnSettings(object? sender, EventArgs e)
    {
        // Deferred rather than opened inline. This runs from a ToolStripMenuItem click, and the
        // context menu still holds mouse capture at that point: a modal window opened underneath
        // it never receives input, so the Agent looks frozen with no way back. Posting through
        // the menu's own control lets it finish closing and release capture first.
        // BeginInvoke needs a created handle. The menu always has one by the time one of its
        // items is clicked, but falling back keeps this from throwing if that ever stops holding.
        if (_menu.IsHandleCreated)
        {
            _menu.BeginInvoke(new Action(ShowSettings));
        }
        else
        {
            ShowSettings();
        }
    }

    private void ShowSettings()
    {
        try
        {
            if (_wpfApplication is null)
            {
                _wpfApplication = System.Windows.Application.Current ?? new System.Windows.Application();

                // Without this the default OnLastWindowClose applies, and closing Settings once
                // calls Application.Shutdown(), which shuts down this thread's WPF dispatcher for
                // good. The tray kept working, so the Agent looked alive, but every later attempt
                // to open Settings hung or threw on a dead dispatcher. The WinForms loop below
                // owns this thread's lifetime; WPF must not end it.
                _wpfApplication.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }

            // A second ShowDialog while one is already open nests another dispatcher frame, and
            // the older window is the one holding input - indistinguishable from a hang.
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;

            // Opened from the notification area, so there is no parent window to come up in front
            // of: without this the window can arrive behind whatever is focused, which reads as
            // nothing having happened.
            _settingsWindow.Topmost = true;
            _settingsWindow.Loaded += (_, _) =>
            {
                _settingsWindow!.Activate();
                _settingsWindow.Topmost = false;
            };

            _settingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            // An exception escaping here would surface as the WinForms unhandled-exception dialog,
            // which can itself open behind other windows - another silent freeze. Say what broke.
            _settingsWindow = null;
            MessageBox.Show(
                $"Could not open Settings.\n\n{ex.Message}",
                "TimeTracker Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // async void, not a blocking GetResult(): this handler runs on the WinForms UI thread, which
    // carries a SynchronizationContext. RunCoreAsync's awaits post their continuations back to
    // that thread, so blocking it here deadlocked the Agent permanently the first time anyone
    // used this menu item - the tray stopped responding and only Task Manager could clear it.
    private async void OnTestConnection(object? sender, EventArgs e)
    {
        try
        {
            await ConnectionTest.RunFromInstalledConfigAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not run the connection test.\n\n{ex.Message}",
                "TimeTracker Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnOpenServer(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_serverUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {_serverUrl}.\n\n{ex.Message}", "TimeTracker Agent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
