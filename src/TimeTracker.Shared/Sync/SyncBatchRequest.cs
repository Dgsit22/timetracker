using TimeTracker.Shared.Events;

namespace TimeTracker.Shared.Sync;

/// <summary>
/// JSON body of the "batch" part in the multipart/form-data POST to /api/ingest/sync.
/// Screenshot image bytes travel as separate file parts, keyed by ScreenshotEventDto.EventId.
/// </summary>
public record SyncBatchRequest(
    Guid DeviceId,
    string AgentVersion,
    string UserName,
    string MachineName,
    DateTimeOffset SentAtUtc,
    List<AppUsageEventDto> AppUsageEvents,
    List<IdlePeriodEventDto> IdlePeriods,
    List<UrlVisitEventDto> UrlVisits,
    List<ScreenshotEventDto> Screenshots,
    List<SessionBreakEventDto> SessionBreaks);
