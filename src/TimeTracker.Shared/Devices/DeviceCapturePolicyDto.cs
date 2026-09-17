namespace TimeTracker.Shared.Devices;

/// <param name="ScreenshotIntervalMinutes">
/// How often to capture, set per device by an admin. Optional with a default so the two sides can
/// be upgraded in either order: an older server omits it and the Agent falls back to its own
/// configured interval, and an older Agent simply ignores the extra property.
/// </param>
public record DeviceCapturePolicyDto(
    bool CaptureAppUsage,
    bool CaptureUrlVisits,
    bool CaptureIdle,
    bool CaptureSessionBreaks,
    bool CaptureScreenshots,
    int? ScreenshotIntervalMinutes = null)
{
    public const int DefaultScreenshotIntervalMinutes = 10;

    public const int MinScreenshotIntervalMinutes = 1;

    public const int MaxScreenshotIntervalMinutes = 240;

    /// <summary>The choices offered in the admin console.</summary>
    public static readonly int[] ScreenshotIntervalChoices = { 1, 2, 5, 10, 15, 30, 60, 120, 240 };
}
