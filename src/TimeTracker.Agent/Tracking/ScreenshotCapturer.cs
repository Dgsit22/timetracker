using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ScreenshotIntervalSeconds));

        do
        {
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
