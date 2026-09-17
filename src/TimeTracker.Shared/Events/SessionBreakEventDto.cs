namespace TimeTracker.Shared.Events;

/// <param name="IdleSecondsAtStart">
/// Seconds without keyboard or mouse input when the break began - the evidence behind telling a
/// user's own lock from an automatic one. Optional so older Agents and servers stay compatible;
/// null means it wasn't measured.
/// </param>
public record SessionBreakEventDto(
    Guid EventId,
    Guid DeviceId,
    DateTimeOffset BreakStartUtc,
    DateTimeOffset? BreakEndUtc,
    SessionBreakReason Reason,
    SessionBreakEndReason? EndReason,
    double? IdleSecondsAtStart = null);
