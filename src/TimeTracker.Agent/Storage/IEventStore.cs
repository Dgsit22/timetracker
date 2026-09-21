using TimeTracker.Shared.Diagnostics;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Storage;

public interface IEventStore
{
    Task AddAppUsageEventAsync(AppUsageEventDto evt, CancellationToken cancellationToken);

    Task AddIdlePeriodAsync(IdlePeriodEventDto evt, CancellationToken cancellationToken);

    Task AddUrlVisitAsync(UrlVisitEventDto evt, CancellationToken cancellationToken);

    Task AddSessionBreakAsync(SessionBreakEventDto evt, CancellationToken cancellationToken);

    Task AddScreenshotAsync(ScreenshotEventDto evt, byte[] imageBytes, CancellationToken cancellationToken);

    Task AddDiagnosticAsync(AgentLogDto entry, CancellationToken cancellationToken);

    Task<PendingBatch> GetPendingBatchAsync(int maxItems, CancellationToken cancellationToken);

    /// <summary>How much is waiting to be delivered - shown locally when the server is unreachable.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken);

    Task RemoveEventsAsync(IEnumerable<Guid> eventIds, CancellationToken cancellationToken);
}
