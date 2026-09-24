using System.Text.Json;

namespace TimeTracker.Agent.Diagnostics;

/// <param name="LastSyncUtc">Null until the Agent has managed one successful sync.</param>
/// <param name="LastErrorMessage">Null when the last attempt succeeded.</param>
/// <param name="QueuedEvents">What is waiting to be delivered.</param>
/// <param name="WrittenAtUtc">Lets a reader tell a current file from a stale one.</param>
public record AgentStatusSnapshot(
    DateTimeOffset? LastSyncUtc,
    string? LastErrorMessage,
    int QueuedEvents,
    DateTimeOffset WrittenAtUtc);

/// <summary>
/// The Agent's last known condition, on disk.
///
/// The Settings window has two hosts. Inside the tray it can read <see cref="AgentHealth"/>
/// directly, but from the Start Menu shortcut it is a separate process with no access to the
/// running one's memory - and that is exactly when someone is most likely to open it, because
/// something looks wrong. A small file is the only thing both hosts can see.
///
/// Written to the data directory rather than beside agent-settings.json: the settings file is
/// deliberately SYSTEM-owned and read-only to the user, while the data directory is granted to
/// Users at install time (GrantDataDirAccess) because the outbox lives there.
/// </summary>
public static class AgentStatusFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathFor(string dataDirectory) =>
        Path.Combine(dataDirectory, "agent-status.json");

    public static void Write(string path, AgentStatusSnapshot snapshot)
    {
        // Written via a temporary file and moved into place: the Settings window can be reading
        // this at the moment the Agent writes it, and a half-written file would show as a
        // deserialisation failure rather than a status.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static AgentStatusSnapshot? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AgentStatusSnapshot>(File.ReadAllText(path))
                : null;
        }
        catch (Exception)
        {
            // A missing or unreadable file means "nothing known", which the window says plainly.
            return null;
        }
    }
}
