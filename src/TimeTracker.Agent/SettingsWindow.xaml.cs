using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TimeTracker.Agent.Diagnostics;

namespace TimeTracker.Agent;

/// <summary>
/// The tray icon's "Settings..." entry: lets someone view/edit ServerBaseUrl and
/// AgentApiKey without hand-editing agent-settings.json or re-running the installer.
/// Writes through the same InstallTimeConfig.Write path the installer itself uses, and
/// tests via the same ConnectionTest logic the "Test Connection" Start Menu shortcut and
/// tray menu item use - this window doesn't duplicate either, just gives them a form.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var (serverUrl, apiKey) = ConnectionTest.ReadFromInstalledConfig();
        ServerUrlBox.Text = serverUrl;
        ApiKeyPasswordBox.Password = apiKey;
        ApiKeyTextBox.Text = apiKey;

        LoadAgentStatus();
        LoadLocalConfiguration();
    }

    /// <summary>
    /// Fills the status card from what can actually be known here. This window runs in two
    /// hosts - inside the tray process, and as its own process from the Start Menu - so nothing
    /// may be read out of the running Agent's memory. Everything shown comes from the status
    /// file the Agent writes, the single-instance mutex, and this assembly.
    /// </summary>
    private void LoadAgentStatus()
    {
        VersionText.Text = AgentVersion();
        AboutVersionText.Text = AgentVersion();
        InstallPathText.Text = AppContext.BaseDirectory;
        EventSourceText.Text = InstallTimeConfig.EventSourceName;

        var running = IsAgentRunning();
        RunningBadgeText.Text = running ? "Running" : "Not running";
        RunningBadge.Opacity = running ? 1 : 0.55;

        var status = AgentStatusFile.TryRead(AgentStatusFile.PathFor(DataDirectory()));

        if (status is null)
        {
            // Either the Agent has never completed a sync on this machine, or it predates the
            // status file. Saying so beats showing a dash that could mean anything.
            HeartbeatText.Text = running ? "waiting for first sync" : "unknown";
            QueuedText.Text = "unknown";
            return;
        }

        HeartbeatText.Text = status.LastSyncUtc is { } last ? Ago(last) : "no successful sync yet";
        LastCheckedText.Text = status.WrittenAtUtc.ToLocalTime().ToString("d MMM yyyy, hh:mm tt");
        QueuedText.Text = status.QueuedEvents == 0 ? "none" : status.QueuedEvents.ToString("N0");

        if (status.LastErrorMessage is { Length: > 0 } error)
        {
            ConnStatusTitle.Text = error;
            StatusText.Text = status.QueuedEvents > 0
                ? $"{status.QueuedEvents:N0} events are waiting on this machine and will be sent once this is fixed."
                : "Nothing is lost - events are kept on this machine until it reconnects.";
        }
        else if (status.LastSyncUtc is { } ok)
        {
            ConnStatusTitle.Text = "Connection verified";
            StatusText.Text = $"Last successful sync {Ago(ok)}.";
        }
    }

    private void LoadLocalConfiguration()
    {
        var dataDirectory = DataDirectory();
        DataDirText.Text = dataDirectory;

        // Read straight from the same file the Agent reads, so this cannot drift from what is
        // actually in force. Absent values fall back to the shipped defaults.
        var (sync, screenshot, idle) = ReadIntervals();
        SyncIntervalText.Text = $"every {sync}s";
        ScreenshotIntervalText.Text = screenshot % 60 == 0 ? $"every {screenshot / 60} min" : $"every {screenshot}s";
        IdleThresholdText.Text = idle % 60 == 0 ? $"{idle / 60} min without input" : $"{idle}s without input";

        var deviceIdPath = Path.Combine(dataDirectory, "device-id.txt");
        DeviceIdText.Text = File.Exists(deviceIdPath)
            ? File.ReadAllText(deviceIdPath).Trim()
            : "not enrolled yet";
    }

    private static string AgentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// The tracking instance holds a session-scoped mutex (see Program.cs). Failing to create it
    /// is the same signal a second copy of the Agent uses to bow out.
    /// </summary>
    private static bool IsAgentRunning()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: true, @"Local\TimeTrackerAgent-SingleInstance", out var createdNew);
            if (createdNew)
            {
                mutex.ReleaseMutex();
            }

            return !createdNew;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DataDirectory() =>
        Path.GetDirectoryName(InstallTimeConfig.GetPath()) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeTracker");

    private static (int Sync, int Screenshot, int Idle) ReadIntervals()
    {
        const int defaultSync = 30, defaultScreenshot = 600, defaultIdle = 300;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return (defaultSync, defaultScreenshot, defaultIdle);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Agent", out var agent))
            {
                return (defaultSync, defaultScreenshot, defaultIdle);
            }

            int Read(string name, int fallback) =>
                agent.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

            return (Read("SyncIntervalSeconds", defaultSync),
                    Read("ScreenshotIntervalSeconds", defaultScreenshot),
                    Read("IdleThresholdSeconds", defaultIdle));
        }
        catch (Exception)
        {
            return (defaultSync, defaultScreenshot, defaultIdle);
        }
    }

    private static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.UtcNow - moment;

        return span switch
        {
            { TotalSeconds: < 90 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} minutes ago",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} hours ago",
            _ => moment.ToLocalTime().ToString("d MMM yyyy, HH:mm"),
        };
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        // Set by the constructor before the panels exist in the visual tree.
        if (ConnectionPanel is null)
        {
            return;
        }

        var connection = NavConnection.IsChecked == true;
        var general = NavGeneral.IsChecked == true;

        ConnectionPanel.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        (SectionTitle.Text, SectionSubtitle.Text) = connection
            ? ("Connection settings", "Keep this device securely connected to your TimeTracker workspace.")
            : general
                ? ("General", "How this Agent is configured on this machine.")
                : ("About", "What this Agent is, and where it lives on this machine.");
    }

    /// <summary>The icon beside the field does the same job as the checkbox below it.</summary>
    private void OnToggleKeyVisibility(object sender, RoutedEventArgs e)
    {
        ShowKeyCheckBox.IsChecked = ShowKeyCheckBox.IsChecked != true;
    }

    private void OnCopyKey(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyValue.Trim();

        if (key.Length == 0)
        {
            ShowStatus("There is no key to copy yet.", isError: true);
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(key);
            ShowStatus("API key copied to the clipboard.", isError: false);
        }
        catch (Exception)
        {
            // The clipboard can be locked by another process; not worth more than a line.
            ShowStatus("Could not reach the clipboard - copy the key manually.", isError: true);
        }
    }

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int BackdropAcrylic = 3;   // DWMSBT_TRANSIENTWINDOW
    private const int CornerRound = 2;       // DWMWCP_ROUND

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Asks the compositor for a real acrylic backdrop rather than faking one.
    ///
    /// A blur drawn inside the window can only blur the window's own content; what makes glass
    /// read as glass is sampling what is *behind* it, which only the desktop compositor can do.
    /// So this hands the job to DWM and clears WPF's own background so the backdrop shows
    /// through - the two have to happen together, or the window paints over the effect it just
    /// asked for.
    ///
    /// Older Windows returns a failure code and nothing changes: the window keeps the opaque
    /// ivory canvas it is built with, which is why that background is only cleared once the
    /// call has actually succeeded.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var backdrop = BackdropAcrylic;

        if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
        {
            return;
        }

        var corners = CornerRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        Background = System.Windows.Media.Brushes.Transparent;
    }

    private string ApiKeyValue => ShowKeyCheckBox.IsChecked == true ? ApiKeyTextBox.Text : ApiKeyPasswordBox.Password;

    private void OnShowKeyChanged(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheckBox.IsChecked == true)
        {
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
            ApiKeyPasswordBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            // Shows its own result MessageBox (same as the tray menu's "Test Connection") -
            // nothing further to do with the result here.
            await ConnectionTest.RunAsync(ServerUrlBox.Text.Trim(), ApiKeyValue.Trim());
            LastCheckedText.Text = DateTimeOffset.Now.ToString("d MMM yyyy, hh:mm tt");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TrySave())
        {
            return;
        }

        ShowStatus("Saved. Restart the Agent (tray menu, or wait for the watchdog) to apply it.", isError: false);
    }

    private void OnSaveAndRestart(object sender, RoutedEventArgs e)
    {
        if (!TrySave())
        {
            return;
        }

        // The watchdog task (see installer/Product.wxs) already knows how to relaunch this
        // exe - reuse it instead of spawning a new instance directly, which would race the
        // single-instance mutex the tracking instance is still holding at the moment of the
        // click. "/Run" fires it immediately rather than waiting for its next 5-minute tick;
        // the "timeout 2" delay gives that instance time to actually exit and release the
        // mutex first, since schtasks itself returns as soon as the run is *requested*, not
        // once the launched exe has actually started.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 2 /nobreak >nul & schtasks.exe /Run /TN \"TimeTracker Agent Watchdog\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception)
        {
            // Best effort - worst case the watchdog's own next scheduled tick (within 5
            // minutes) picks this up instead, same as if this button didn't exist at all.
        }

        // Signal rather than exit this process. This window has two hosts: the tray, where it
        // runs inside the tracking process, and the Start Menu shortcut, where it runs as its
        // own UI-only process. The old Application.Exit() only worked in the first - in the
        // second it exited nothing, the tracking instance kept the mutex, and the relaunch
        // scheduled above died instantly as a duplicate, leaving the saved settings unapplied
        // with nothing on screen to say so. The named event reaches the tracking instance in
        // both cases, and in the tray case that is simply this process signalling itself.
        if (!AgentShutdownSignal.Request())
        {
            // No instance was listening, so there is nothing to restart and the watchdog run
            // scheduled above is what will start it. Saying so beats a window that looks like
            // it did nothing.
            ShowStatus("Saved. The Agent was not running; the watchdog will start it shortly.", isError: false);
            return;
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Saves by relaunching this same exe elevated with --write-config, rather than writing the
    /// file in-process. Two reasons, one practical and one deliberate:
    /// agent-settings.json is created by the installer's elevated custom action, so it lands
    /// SYSTEM-owned with Users:(RX) - this process, running as the logged-in user, can read it
    /// but gets UnauthorizedAccessException on any write. And that permission split is worth
    /// keeping: this is a workplace monitoring agent, so an ordinary employee must not be able to
    /// silently repoint it at a server of their choosing or blank its key. Requiring the UAC
    /// prompt makes changing it an administrator action, which is the intended security posture.
    /// </summary>
    private bool TrySave()
    {
        var serverUrl = ServerUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            ShowStatus("Server URL can't be empty.", isError: true);
            return false;
        }

        try
        {
            using var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--write-config \"{serverUrl}|{ApiKeyValue.Trim()}\"",
                UseShellExecute = true, // required for Verb; also what surfaces the UAC prompt
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            elevated?.WaitForExit();

            if (elevated is { ExitCode: not 0 })
            {
                ShowStatus("Couldn't write the settings file. Check that you're an administrator.", isError: true);
                return false;
            }
        }
        catch (Win32Exception)
        {
            // Thrown as ERROR_CANCELLED when the UAC prompt is dismissed or declined.
            ShowStatus("Saving needs administrator approval - the prompt was cancelled.", isError: true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Both outcomes are written in the palette rather than the usual red and green: the brief
    /// rules those out, and an error is legible here through its words and a deepened rust
    /// rather than through hue alone - which is the more accessible signal anyway.
    /// </summary>
    private void ShowStatus(string text, bool isError)
    {
        ConnStatusTitle.Text = isError ? "Something needs attention" : "Done";
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;

        ConnStatusTitle.Foreground = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xA3, 0x2F, 0x22)
            : System.Windows.Media.Color.FromRgb(0x8C, 0x38, 0x1F));
        ConnStatusDot.Background = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xA3, 0x2F, 0x22)
            : System.Windows.Media.Color.FromRgb(0xFB, 0x98, 0x24));
    }
}
