namespace TimeTracker.Shared.Devices;

public record DeviceCapturePolicyDto(
    bool CaptureAppUsage,
    bool CaptureUrlVisits,
    bool CaptureIdle,
    bool CaptureSessionBreaks,
    bool CaptureScreenshots);
