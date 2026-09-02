using System.Diagnostics;
using System.Text.Json;

namespace TimeTracker.Agent;

/// <summary>
/// Writes %ProgramData%\TimeTracker\agent-settings.json, which the MSI installer
/// invokes at install time (as `TimeTracker.Agent.exe --write-config &lt;serverUrl&gt; &lt;apiKey&gt;`)
/// instead of relying on machine environment variables: a Windows Service inherits
/// its environment from the Service Control Manager's snapshot at boot, so a fresh
/// install's machine env var isn't visible to the service until a reboot. This file
/// is read as a config layer on every startup, so editing it and restarting the
/// service (no reboot needed) is enough to repoint the Agent later.
/// Also pre-creates the Event Log source here, while running elevated during install,
/// since a normal per-user run afterward won't have rights to create a new one.
/// </summary>
public static class InstallTimeConfig
{
    public const string FileName = "agent-settings.json";
    public const string EventSourceName = "TimeTracker.Agent";

    public static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeTracker", FileName);

    public static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length < 1 || args[0] != "--write-config")
        {
            return false;
        }

        var serverUrl = args.Length > 1 ? args[1] : "";
        var apiKey = args.Length > 2 ? args[2] : "";

        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Omit blank values rather than writing them, so an unset field falls back to
        // appsettings.json instead of overriding it with an empty string.
        var agent = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            agent["ServerBaseUrl"] = serverUrl;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            agent["AgentApiKey"] = apiKey;
        }

        var json = JsonSerializer.Serialize(new { Agent = agent }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);

        try
        {
            if (!EventLog.SourceExists(EventSourceName))
            {
                EventLog.CreateEventSource(EventSourceName, "Application");
            }
        }
        catch (Exception)
        {
            // Best effort: if this isn't elevated for some reason, logging just falls back
            // to nothing rather than blocking the install over a diagnostics nicety.
        }

        return true;
    }
}
