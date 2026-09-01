namespace TimeTracker.Shared.Sync;

public record SyncBatchResponse(
    List<Guid> AcceptedEventIds,
    List<SyncErrorDto> Rejected);
