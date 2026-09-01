namespace TimeTracker.Shared.Events;

public record ScreenshotEventDto(
    Guid EventId,
    Guid DeviceId,
    DateTimeOffset CapturedAtUtc,
    int MonitorIndex,
    int WidthPx,
    int HeightPx,
    string ContentType);
