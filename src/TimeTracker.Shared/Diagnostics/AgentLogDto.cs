namespace TimeTracker.Shared.Diagnostics;

public enum AgentLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// A fault the Agent noticed about itself, on its way to the admin console.
///
/// These ride the ordinary sync batch and sit in the same local outbox as activity events, which
/// is the only arrangement that works for the failure that matters most: an Agent that cannot
/// reach the server has no way to report that it cannot reach the server, so the report has to
/// wait on disk until the connection comes back and then explain what happened while it was gone.
/// </summary>
/// <param name="EventId">Identifies the report so a retried batch cannot duplicate it.</param>
/// <param name="OccurredAtUtc">When the fault happened, not when it was finally delivered.</param>
/// <param name="Source">The part of the Agent that raised it, e.g. SyncClient.</param>
/// <param name="Message">One line, suitable for a list.</param>
/// <param name="Detail">Exception text or context. Null when the message says everything.</param>
/// <param name="OccurrenceCount">
/// How many times this repeated before it was reported. A connection failure recurs every sync
/// interval, and sending each one would flood the outbox it is queued in - identical faults are
/// coalesced and carry a count instead.
/// </param>
public record AgentLogDto(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    AgentLogLevel Level,
    string Source,
    string Message,
    string? Detail,
    int OccurrenceCount);
