using Microsoft.EntityFrameworkCore;

namespace TimeTracker.Server.Data;

/// <summary>
/// Deletes activity older than the configured windows. Without this nothing is ever removed:
/// screenshots in particular are stored as bytes in the database, so a handful of devices adds
/// gigabytes a month, and every backup and restore carries all of it forever.
///
/// Screenshots and events get separate windows because they cost very differently - the bytes are
/// what actually hurt, while a year of app-usage rows is small and is what the reports are for.
/// </summary>
public class RetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IServiceScopeFactory scopeFactory,
        RetentionOptions options,
        ILogger<RetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Retention is disabled; nothing will be deleted. Set RETENTION_ENABLED=true to turn it on.");
            return;
        }

        _logger.LogInformation(
            "Retention is on: screenshots kept {ScreenshotDays} days, events kept {EventDays} days, sweeping every {IntervalHours}h",
            _options.ScreenshotDays, _options.EventDays, _options.SweepIntervalHours);

        // A first sweep on startup would land in the middle of migrations and seeding on a cold
        // boot, so the service waits out one short delay before its first pass.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.SweepIntervalHours));

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep must never take the server down with it: the next tick retries,
                // and the only consequence of a miss is that deletion happens later.
                _logger.LogError(ex, "Retention sweep failed; will retry on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimeTrackerDbContext>();

        var now = DateTimeOffset.UtcNow;
        var screenshotCutoff = now.AddDays(-_options.ScreenshotDays);
        var eventCutoff = now.AddDays(-_options.EventDays);

        // Screenshots first and on their own: they are the reason this exists, and doing them
        // before the cheaper tables means a sweep that is interrupted still reclaims the space
        // that matters.
        var screenshots = await DeleteInBatchesAsync(
            db, () => db.Screenshots.Where(e => e.CapturedAtUtc < screenshotCutoff), cancellationToken);

        var appUsage = await DeleteInBatchesAsync(
            db, () => db.AppUsageEvents.Where(e => e.StartedAtUtc < eventCutoff), cancellationToken);

        var urlVisits = await DeleteInBatchesAsync(
            db, () => db.UrlVisits.Where(e => e.StartedAtUtc < eventCutoff), cancellationToken);

        var idlePeriods = await DeleteInBatchesAsync(
            db, () => db.IdlePeriods.Where(e => e.StartedAtUtc < eventCutoff), cancellationToken);

        var sessionBreaks = await DeleteInBatchesAsync(
            db, () => db.SessionBreaks.Where(e => e.BreakStartUtc < eventCutoff), cancellationToken);

        var total = screenshots + appUsage + urlVisits + idlePeriods + sessionBreaks;

        if (total == 0)
        {
            _logger.LogInformation("Retention sweep: nothing past the cutoffs");
            return;
        }

        _logger.LogInformation(
            "Retention sweep deleted {Total} rows: {Screenshots} screenshots, {AppUsage} app usage, "
            + "{UrlVisits} url visits, {IdlePeriods} idle periods, {SessionBreaks} session breaks",
            total, screenshots, appUsage, urlVisits, idlePeriods, sessionBreaks);
    }

    /// <summary>
    /// Deletes in bounded batches rather than one statement. The first sweep after this ships can
    /// match an enormous number of rows, and a single delete of that size holds locks and inflates
    /// the WAL for as long as it runs, stalling the ingest endpoint behind it.
    /// </summary>
    private async Task<int> DeleteInBatchesAsync<T>(
        TimeTrackerDbContext db,
        Func<IQueryable<T>> query,
        CancellationToken cancellationToken) where T : class
    {
        var deleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await query().Take(_options.BatchSize).ExecuteDeleteAsync(cancellationToken);
            deleted += batch;

            if (batch < _options.BatchSize)
            {
                break;
            }

            // Let ingest and page loads through between batches.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return deleted;
    }
}

/// <summary>
/// Read once at startup. Deliberately plain values rather than IOptions binding so the resolved
/// numbers can be logged on boot - retention that silently did not run is worse than none.
/// </summary>
public class RetentionOptions
{
    public bool Enabled { get; init; } = true;
    public int ScreenshotDays { get; init; } = 30;
    public int EventDays { get; init; } = 180;
    public int SweepIntervalHours { get; init; } = 24;
    public int BatchSize { get; init; } = 5000;

    public static RetentionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Retention");

        var options = new RetentionOptions
        {
            Enabled = section.GetValue("Enabled", true),
            ScreenshotDays = section.GetValue("ScreenshotDays", 30),
            EventDays = section.GetValue("EventDays", 180),
            SweepIntervalHours = section.GetValue("SweepIntervalHours", 24),
            BatchSize = section.GetValue("BatchSize", 5000),
        };

        // A zero or negative window would delete everything the moment it ran, including data
        // written seconds ago. Treat it as a misconfiguration and refuse to start.
        if (options.Enabled && (options.ScreenshotDays < 1 || options.EventDays < 1))
        {
            throw new InvalidOperationException(
                "Retention:ScreenshotDays and Retention:EventDays must be at least 1 day when retention is enabled.");
        }

        if (options.Enabled && options.SweepIntervalHours < 1)
        {
            throw new InvalidOperationException("Retention:SweepIntervalHours must be at least 1 hour.");
        }

        return options;
    }
}
