namespace TimeTracker.Shared.Events;

public record SessionBreakEventDto(
    Guid EventId,
    Guid DeviceId,
    DateTimeOffset BreakStartUtc,
    DateTimeOffset? BreakEndUtc,
    SessionBreakReason Reason,
    SessionBreakEndReason? EndReason);
