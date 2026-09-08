using System.Diagnostics;
using System.Text.Json;

namespace TimeTracker.Agent;

/// <summary>
/// Writes %ProgramData%\TimeTracker\agent-settings.json, which the MSI installer
/// invokes at install time instead of relying on machine environment variables: a
/// Windows Service inherits its environment from the Service Control Manager's
/// snapshot at boot, so a fresh install's machine env var isn't visible to the
/// service until a reboot. This file is read as a config layer on every startup, so
/// editing it and restarting the service (no reboot needed) is enough to repoint
/// the Agent later.
/// Also pre-creates the Event Log source here, while running elevated during install,
/// since a normal per-user run afterward won't have rights to create a new one.
///
/// Getting the wizard's values here takes two hops, not one direct write, because the
/// write itself must run as an elevated *deferred* custom action (it runs after
/// InstallFiles, by which point the UI - and with it the unprivileged user context -
/// is long gone), and a deferred custom action cannot see the MSI property table:
/// `[SERVERURL]`/`[AGENTAPIKEY]` resolve blank there. The documented fix for this is
/// normally to stash the data in a same-named `CustomActionData` property and let the
/// engine copy it into the deferred action's own CustomActionData - but that mechanism
/// turns out not to reach an *EXE*-type deferred action's command line at all (verified
/// empirically: neither `[CustomActionData]` token substitution nor the
/// `%CustomActionData%` environment variable carried the value through, despite the
/// property being set correctly beforehand - apparently that channel only reaches a
/// DLL-type CA's in-process Session.CustomActionData, not a launched EXE's argv/env).
/// So instead: an *immediate* `cmd.exe` custom action (with direct property access)
/// stages the pipe-joined value to a fixed, machine-wide temp path itself - see
/// Product.wxs - and the deferred CA here reads it back via `--write-staged-config`,
/// deleting the file immediately after so the API key doesn't linger on disk.
/// </summary>
public static class InstallTimeConfig
{
    public const string FileName = "agent-settings.json";
    public const string EventSourceName = "TimeTracker.Agent";

    public static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeTracker", FileName);

    // Must match the path StageAgentConfig's cmd.exe invocation writes to in Product.wxs.
    private static string StagePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "timetracker-agent-stage.tmp");

    public static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length < 1)
        {
            return false;
        }

        switch (args[0])
        {
            case "--write-staged-config":
                var staged = File.Exists(StagePath) ? File.ReadAllText(StagePath) : "";
                WriteConfig(staged);
                try { File.Delete(StagePath); } catch (IOException) { }
                return true;

            case "--write-config":
                WriteConfig(args.Length > 1 ? args[1] : "");
                return true;

            default:
                return false;
        }
    }

    private static void WriteConfig(string pipeJoined)
    {
        // Trim: the staged file is written by `cmd /c echo ...`, which appends a CRLF.
        var parts = pipeJoined.Split('|', 2);
        var serverUrl = parts.Length > 0 ? parts[0].Trim() : "";
        var apiKey = parts.Length > 1 ? parts[1].Trim() : "";

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
    }
}
