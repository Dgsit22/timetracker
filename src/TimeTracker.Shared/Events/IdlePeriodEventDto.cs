namespace TimeTracker.Shared.Events;

public record IdlePeriodEventDto(
    Guid EventId,
    Guid DeviceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double DurationSeconds,
    int IdleThresholdSeconds);
