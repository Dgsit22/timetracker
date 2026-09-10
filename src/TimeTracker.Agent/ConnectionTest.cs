using System.Net;
using System.Text.Json;
using System.Windows.Forms;

namespace TimeTracker.Agent;

/// <summary>
/// Handles `TimeTracker.Agent.exe --test-connection [&lt;serverUrl&gt;|&lt;apiKey&gt;]`, invoked from
/// the "Test Connection" Start Menu shortcut the installer creates. Hits the same
/// authenticated endpoint SyncClient does (/api/devices/{id}/policy, which the server
/// accepts even for an unknown device) so a pass here means the real sync path would
/// also work, then reports the result via MessageBox since this is a WinExe with no
/// console and is launched by double-click, not from a terminal.
/// If no argument is given, reads the currently installed agent-settings.json instead
/// of requiring the values be re-typed - that's the shortcut's normal use case.
/// </summary>
public static class ConnectionTest
{
    public static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length < 1 || args[0] != "--test-connection")
        {
            return false;
        }

        var (serverUrl, apiKey) = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? ParsePipeJoined(args[1])
            : ReadFromInstalledConfig();

        RunCoreAsync(serverUrl, apiKey).GetAwaiter().GetResult();
        return true;
    }

    // For the tray icon's "Test Connection" menu item: runs the same check in-process
    // instead of spawning a second exe just to show a result MessageBox.
    public static Task RunFromInstalledConfigAsync()
    {
        var (serverUrl, apiKey) = ReadFromInstalledConfig();
        return RunCoreAsync(serverUrl, apiKey);
    }

    // For the tray icon's "Open Server" menu item.
    public static string GetConfiguredServerUrl() => ReadFromInstalledConfig().ServerUrl;

    // For the Settings window's "Test Connection" button: runs the same check against
    // whatever is currently typed into the form, before it's been saved.
    public static Task RunAsync(string serverUrl, string apiKey) => RunCoreAsync(serverUrl, apiKey);

    private static (string ServerUrl, string ApiKey) ParsePipeJoined(string arg)
    {
        var parts = arg.Split('|', 2);
        return (parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
    }

    internal static (string ServerUrl, string ApiKey) ReadFromInstalledConfig()
    {
        const string defaultUrl = "http://localhost:5081";
        var path = InstallTimeConfig.GetPath();
        if (!File.Exists(path))
        {
            return (defaultUrl, "");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            {
                return (defaultUrl, "");
            }

            var serverUrl = agent.TryGetProperty("ServerBaseUrl", out var s) ? s.GetString() : null;
            var apiKey = agent.TryGetProperty("AgentApiKey", out var k) ? k.GetString() : null;
            return (string.IsNullOrWhiteSpace(serverUrl) ? defaultUrl : serverUrl!, apiKey ?? "");
        }
        catch (JsonException)
        {
            return (defaultUrl, "");
        }
    }

    private static async Task RunCoreAsync(string serverUrl, string apiKey)
    {
        const string title = "TimeTracker Agent - Connection Test";

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            MessageBox.Show("No server URL is configured.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string message;
        MessageBoxIcon icon;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{serverUrl.TrimEnd('/')}/api/devices/{Guid.NewGuid()}/policy");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("X-Agent-Key", apiKey);
            }

            using var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                message = $"Success.\n\nReached {serverUrl} and the server accepted the Agent API key.";
                icon = MessageBoxIcon.Information;
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                message = $"Reached {serverUrl}, but the server rejected the Agent API key.\n\n"
                    + "Check that it matches the AGENT_API_KEY set in the server's .env file.";
                icon = MessageBoxIcon.Error;
            }
            else
            {
                message = $"Reached {serverUrl}, but got an unexpected response: {(int)response.StatusCode} {response.StatusCode}.";
                icon = MessageBoxIcon.Warning;
            }
        }
        catch (TaskCanceledException)
        {
            message = $"Timed out trying to reach {serverUrl}.\n\n"
                + "Check the URL, that the server is running, and that nothing (e.g. a firewall) is blocking the connection.";
            icon = MessageBoxIcon.Error;
        }
        catch (HttpRequestException ex)
        {
            message = $"Could not reach {serverUrl}.\n\n{ex.Message}\n\n"
                + "Check the URL, that the server is running, and network/firewall settings.";
            icon = MessageBoxIcon.Error;
        }

        MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
    }
}
