using TimeTracker.Shared.Diagnostics;

namespace TimeTracker.Server.Data;

/// <summary>
/// A fault, as reported by an Agent or noticed by this server about an Agent.
///
/// Kept apart from the alert table: an alert is a condition that is either true or false right
/// now and gets cleared, while these are a log - each row happened, and stays. The console reads
/// them together, but only one of them can be resolved.
/// </summary>
public class AgentLogEntry
{
    public Guid EntryId { get; set; }

    /// <summary>Null for entries this server raised about a device that stopped reporting.</summary>
    public Guid? DeviceId { get; set; }

    public string MachineName { get; set; } = default!;

    public string UserName { get; set; } = default!;

    public AgentLogLevel Level { get; set; }

    /// <summary>
    /// Which part raised it: an Agent component such as SyncClient, or "Server" when this server
    /// noticed the absence itself. Reports cannot arrive from an Agent that cannot connect, so
    /// some entries have to come from this side.
    /// </summary>
    public string Source { get; set; } = default!;

    public string Message { get; set; } = default!;

    public string? Detail { get; set; }

    /// <summary>How many times the Agent saw this before it managed to report it.</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>When the fault happened on the machine.</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>
    /// When it reached the server. The gap between this and OccurredAtUtc is itself the evidence
    /// of an outage: a report delivered two hours late is a report that waited in the outbox.
    /// </summary>
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
