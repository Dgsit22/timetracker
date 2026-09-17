using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Shared.Devices;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Periodically captures one screenshot per attached monitor.
/// </summary>
public class ScreenshotCapturer : BackgroundService
{
    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DevicePolicyCache _policyCache;
    private readonly AgentOptions _options;
    private readonly ILogger<ScreenshotCapturer> _logger;

    public ScreenshotCapturer(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        DevicePolicyCache policyCache,
        IOptions<AgentOptions> options,
        ILogger<ScreenshotCapturer> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _policyCache = policyCache;
        _options = options.Value;
        _logger = logger;
    }

    // How often the loop re-checks whether a capture is due. Short, so an admin shortening the
    // interval from an hour to a minute doesn't wait out the rest of the old hour first.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        DateTimeOffset? lastCaptureUtc = null;

        do
        {
            var now = DateTimeOffset.UtcNow;
            if (lastCaptureUtc is { } last && now - last < CurrentInterval())
            {
                continue;
            }

            // Stamped before capturing, so a capture that throws still waits a full interval
            // rather than retrying every 15 seconds.
            lastCaptureUtc = now;

            try
            {
                await CaptureAllAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture screenshots");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// The server's per-device interval when it sends one, otherwise this Agent's own configured
    /// ScreenshotIntervalSeconds - the fallback for a server that predates the setting.
    /// </summary>
    private TimeSpan CurrentInterval()
    {
        if (_policyCache.Current.ScreenshotIntervalMinutes is { } minutes)
        {
            return TimeSpan.FromMinutes(Math.Clamp(
                minutes,
                DeviceCapturePolicyDto.MinScreenshotIntervalMinutes,
                DeviceCapturePolicyDto.MaxScreenshotIntervalMinutes));
        }

        return TimeSpan.FromSeconds(Math.Max(60, _options.ScreenshotIntervalSeconds));
    }

    private async Task CaptureAllAsync(CancellationToken cancellationToken)
    {
        if (!_policyCache.Current.CaptureScreenshots)
        {
            return;
        }

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var screens = Screen.AllScreens;

        for (var i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);

            var dto = new ScreenshotEventDto(
                Guid.NewGuid(),
                _deviceIdentity.DeviceId,
                capturedAtUtc,
                i,
                bounds.Width,
                bounds.Height,
                "image/png");

            await _store.AddScreenshotAsync(dto, stream.ToArray(), cancellationToken);
        }
    }
}
