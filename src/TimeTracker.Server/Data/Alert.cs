namespace TimeTracker.Server.Data;

public enum AlertKind
{
    /// <summary>A device stopped checking in. The Agent heartbeats hourly even when idle.</summary>
    DeviceSilent = 0,

    /// <summary>Agents are reaching the server and being refused for a bad or missing API key.</summary>
    IngestKeyRejected = 1,
}

/// <summary>
/// A condition worth telling an admin about, raised and cleared by <see cref="AlertMonitor"/>.
///
/// Stored rather than computed on each page load because the useful part is the history: when a
/// machine went quiet, and whether it came back on its own. A purely derived banner cannot say
/// "this has been happening since Tuesday", which is the question actually being asked.
///
/// At most one unresolved alert exists per (Kind, Scope) - the monitor updates the existing row
/// instead of adding another, so a device silent for a week is one alert, not a thousand.
/// </summary>
public class Alert
{
    public Guid AlertId { get; set; }

    public AlertKind Kind { get; set; }

    /// <summary>
    /// What the alert is about: a device id for DeviceSilent, empty for server-wide conditions.
    /// Kept as text so a scope can be something other than a device without a migration.
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    public string Title { get; set; } = default!;

    public string Detail { get; set; } = default!;

    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Refreshed each time the condition is still true, so "since" stays honest.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Set when the condition clears on its own - the device reported again.</summary>
    public DateTimeOffset? ResolvedUtc { get; set; }

    /// <summary>
    /// Set when someone dismisses it. Distinct from resolved: acknowledging says "I know", not
    /// "it stopped", so an acknowledged alert leaves the bell but stays in the list until the
    /// underlying condition actually clears.
    /// </summary>
    public DateTimeOffset? AcknowledgedUtc { get; set; }

    public bool IsOpen => ResolvedUtc is null;
}
