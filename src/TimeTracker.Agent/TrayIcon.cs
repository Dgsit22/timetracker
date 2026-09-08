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
    private readonly string _serverUrl;

    public AgentTrayIcon(string serverUrl)
    {
        _serverUrl = serverUrl;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Test Connection", null, OnTestConnection);
        menu.Items.Add("Open Server", null, OnOpenServer);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TimeTracker Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void OnTestConnection(object? sender, EventArgs e)
        => ConnectionTest.RunFromInstalledConfigAsync().GetAwaiter().GetResult();

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
