using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

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
        // single-instance mutex this process is still holding at the moment of the click.
        // "/Run" fires it immediately rather than waiting for its next 5-minute tick; the
        // "timeout 2" delay gives this process time to actually exit and release the mutex
        // first, since schtasks itself returns as soon as the run is *requested*, not once
        // the launched exe has actually started.
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

        System.Windows.Forms.Application.Exit();
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

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)
            : System.Windows.Media.Color.FromRgb(0x1F, 0x7A, 0x5C));
        StatusText.Visibility = Visibility.Visible;
    }
}
