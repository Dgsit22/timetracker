using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Storage;

public record PendingScreenshot(ScreenshotEventDto Dto, string FilePath);

public record PendingBatch(
    List<AppUsageEventDto> AppUsageEvents,
    List<IdlePeriodEventDto> IdlePeriods,
    List<UrlVisitEventDto> UrlVisits,
    List<SessionBreakEventDto> SessionBreaks,
    List<PendingScreenshot> Screenshots)
{
    public bool IsEmpty =>
        AppUsageEvents.Count == 0 &&
        IdlePeriods.Count == 0 &&
        UrlVisits.Count == 0 &&
        SessionBreaks.Count == 0 &&
        Screenshots.Count == 0;

    public IEnumerable<Guid> AllEventIds =>
        AppUsageEvents.Select(e => e.EventId)
            .Concat(IdlePeriods.Select(e => e.EventId))
            .Concat(UrlVisits.Select(e => e.EventId))
            .Concat(SessionBreaks.Select(e => e.EventId))
            .Concat(Screenshots.Select(s => s.Dto.EventId));
}
