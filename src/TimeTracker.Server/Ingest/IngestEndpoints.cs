using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Devices;
using TimeTracker.Shared.Events;
using TimeTracker.Shared.Sync;

namespace TimeTracker.Server.Ingest;

public static class IngestEndpoints
{
    private const string ApiKeyHeader = "X-Agent-Key";
    private const string DeviceTokenHeader = "X-Device-Token";

    /// <summary>
    /// Ordinary string comparison returns as soon as it hits a differing character, which leaks how
    /// much of a secret was correct through response timing. Length is not itself secret here.
    /// </summary>
    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// The only content type screenshots are ever stored or served as. The Agent captures PNG and
    /// nothing else (ScreenshotCapturer saves with ImageFormat.Png), so this is not a restriction
    /// in practice - but it must never be taken from the request. A client-supplied content type
    /// was previously stored verbatim and echoed back on download, and since the admin console
    /// links each thumbnail with target="_blank", anything claiming to be text/html would render
    /// as HTML in the console's own authenticated origin on click - stored XSS, reachable by
    /// anyone holding the shared agent key.
    /// </summary>
    private const string ScreenshotContentType = "image/png";

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    public static void MapIngestEndpoints(this WebApplication app, IConfiguration configuration)
    {
        var apiKey = configuration["Agent:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            // Surfaced once at startup so this is discoverable in logs, rather than only showing up
            // later as agents quietly failing to sync against a 503.
            app.Services.GetRequiredService<ILogger<Program>>().LogWarning(
                "Agent:ApiKey is not configured - /api/ingest/sync will reject all agent traffic. "
                + "Set AGENT_API_KEY (docker compose) or Agent__ApiKey.");
        }

        app.MapPost("/api/ingest/sync", HandleSyncAsync)
            .AddEndpointFilter(new ApiKeyFilter(apiKey));

        app.MapGet("/api/devices/{deviceId:guid}/policy", HandleGetPolicyAsync)
            .AddEndpointFilter(new ApiKeyFilter(apiKey));

        // Consumed by the admin console's <img> tags, so it uses cookie auth, not the agent API key.
        app.MapGet("/api/screenshots/{eventId:guid}", HandleGetScreenshotAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> HandleSyncAsync(
        HttpRequest request,
        TimeTrackerDbContext db,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest("Expected multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var batchJson = form["batch"].ToString();
        if (string.IsNullOrEmpty(batchJson))
        {
            return Results.BadRequest("Missing 'batch' part.");
        }

        SyncBatchRequest batch;
        try
        {
            batch = JsonSerializer.Deserialize<SyncBatchRequest>(batchJson)
                     ?? throw new JsonException("Batch payload was null.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected sync batch with malformed JSON");
            return Results.BadRequest("Malformed 'batch' JSON.");
        }

        var device = await GetOrCreateDeviceAsync(
            db, batch, request.Headers[DeviceTokenHeader].ToString(), cancellationToken);

        if (device is null)
        {
            logger.LogWarning(
                "Rejected sync for device {DeviceId} ({UserName}): device token mismatch",
                batch.DeviceId, batch.UserName);
            return Results.Problem(
                "Device token does not match the token this device enrolled with.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var accepted = new List<Guid>();
        var rejected = new List<SyncErrorDto>();

        if (device.CaptureAppUsage)
        {
            await AddNewAsync(db.AppUsageEvents, batch.AppUsageEvents, dto => dto.EventId,
                dto => new AppUsageEvent
                {
                    EventId = dto.EventId,
                    DeviceId = batch.DeviceId,
                    UserName = batch.UserName,
                    ProcessName = dto.ProcessName,
                    WindowTitle = dto.WindowTitle,
                    StartedAtUtc = dto.StartedAtUtc,
                    EndedAtUtc = dto.EndedAtUtc,
                    DurationSeconds = dto.DurationSeconds,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                }, accepted, cancellationToken);
        }
        else
        {
            DropByPolicy(logger, batch, "app usage", batch.AppUsageEvents.Select(e => e.EventId), accepted);
        }

        if (device.CaptureIdle)
        {
            await AddNewAsync(db.IdlePeriods, batch.IdlePeriods, dto => dto.EventId,
                dto => new IdlePeriodEvent
                {
                    EventId = dto.EventId,
                    DeviceId = batch.DeviceId,
                    UserName = batch.UserName,
                    StartedAtUtc = dto.StartedAtUtc,
                    EndedAtUtc = dto.EndedAtUtc,
                    DurationSeconds = dto.DurationSeconds,
                    IdleThresholdSeconds = dto.IdleThresholdSeconds,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                }, accepted, cancellationToken);
        }
        else
        {
            DropByPolicy(logger, batch, "idle", batch.IdlePeriods.Select(e => e.EventId), accepted);
        }

        if (device.CaptureUrlVisits)
        {
            await AddNewAsync(db.UrlVisits, batch.UrlVisits, dto => dto.EventId,
                dto => new UrlVisitEvent
                {
                    EventId = dto.EventId,
                    DeviceId = batch.DeviceId,
                    UserName = batch.UserName,
                    Browser = dto.Browser,
                    Url = dto.Url,
                    PageTitle = dto.PageTitle,
                    StartedAtUtc = dto.StartedAtUtc,
                    EndedAtUtc = dto.EndedAtUtc,
                    DurationSeconds = dto.DurationSeconds,
                    CaptureMethod = dto.CaptureMethod,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                }, accepted, cancellationToken);
        }
        else
        {
            DropByPolicy(logger, batch, "url visit", batch.UrlVisits.Select(e => e.EventId), accepted);
        }

        if (device.CaptureSessionBreaks)
        {
            await AddNewAsync(db.SessionBreaks, batch.SessionBreaks, dto => dto.EventId,
                dto => new SessionBreakEvent
                {
                    EventId = dto.EventId,
                    DeviceId = batch.DeviceId,
                    UserName = batch.UserName,
                    BreakStartUtc = dto.BreakStartUtc,
                    BreakEndUtc = dto.BreakEndUtc,
                    Reason = dto.Reason,
                    EndReason = dto.EndReason,
                    IdleSecondsAtStart = dto.IdleSecondsAtStart,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                }, accepted, cancellationToken);
        }
        else
        {
            DropByPolicy(logger, batch, "session break", batch.SessionBreaks.Select(e => e.EventId), accepted);
        }

        if (!device.CaptureScreenshots && batch.Screenshots.Count > 0)
        {
            DropByPolicy(logger, batch, "screenshot", batch.Screenshots.Select(e => e.EventId), accepted);
        }

        foreach (var dto in batch.Screenshots)
        {
            if (!device.CaptureScreenshots)
            {
                continue;
            }

            if (await db.Screenshots.AnyAsync(e => e.EventId == dto.EventId, cancellationToken))
            {
                accepted.Add(dto.EventId);
                continue;
            }

            var file = form.Files[dto.EventId.ToString()];
            if (file is null)
            {
                rejected.Add(new SyncErrorDto(dto.EventId, "Missing screenshot file part."));
                continue;
            }

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            var imageBytes = stream.ToArray();

            // Checked against the actual bytes, not the declared type: a client can claim anything.
            // Rejected (rather than silently coerced) so a device sending non-PNG is visible in the
            // sync response instead of quietly filling the table with unrenderable rows. The Agent
            // drops rejected events from its outbox, so this can't become a retry loop.
            if (!IsPng(imageBytes))
            {
                rejected.Add(new SyncErrorDto(dto.EventId, "Screenshot payload is not a PNG."));
                continue;
            }

            db.Screenshots.Add(new ScreenshotEvent
            {
                EventId = dto.EventId,
                DeviceId = batch.DeviceId,
                UserName = batch.UserName,
                CapturedAtUtc = dto.CapturedAtUtc,
                MonitorIndex = dto.MonitorIndex,
                WidthPx = dto.WidthPx,
                HeightPx = dto.HeightPx,
                ContentType = ScreenshotContentType,
                ImageBytes = imageBytes,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
            });
            accepted.Add(dto.EventId);
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sync batch from device {DeviceId} ({UserName}): {Accepted} accepted, {Rejected} rejected",
            batch.DeviceId, batch.UserName, accepted.Count, rejected.Count);

        return Results.Ok(new SyncBatchResponse(accepted, rejected));
    }

    /// <summary>
    /// Acknowledges events the device's capture policy says not to store, and says so in the log.
    /// They must still be acknowledged - the Agent deletes whatever comes back as accepted, and
    /// leaving them unacknowledged would wedge the outbox retrying them forever. But a bare
    /// AddRange made a switched-off capture type indistinguishable from a healthy one: the events
    /// were dropped here and deleted at source with no trace at either end, so an admin who had
    /// cleared a toggle by accident saw an empty Activity page and nothing to explain it.
    /// </summary>
    private static void DropByPolicy(
        ILogger logger, SyncBatchRequest batch, string kind, IEnumerable<Guid> eventIds, List<Guid> accepted)
    {
        var before = accepted.Count;
        accepted.AddRange(eventIds);
        var dropped = accepted.Count - before;

        if (dropped > 0)
        {
            logger.LogInformation(
                "Dropped {Count} {Kind} event(s) from device {DeviceId} ({UserName}): capture is disabled for this device",
                dropped, kind, batch.DeviceId, batch.UserName);
        }
    }

    /// <summary>
    /// Returns null when the caller failed to prove it owns this DeviceId, in which case the batch
    /// must be rejected outright. Trust-on-first-use: the first sync to present a token claims the
    /// device permanently, and every later sync for it must present the same one.
    /// Deliberately tolerant of a device that has no token stored yet, so agents installed before
    /// tokens existed keep syncing during a staged rollout instead of going silent - they simply
    /// claim their token the first time an updated Agent reaches them. The tradeoff is that a device
    /// which has never yet been claimed is still spoofable by anyone holding the shared agent key;
    /// the window closes per device on its next sync from an updated Agent.
    /// </summary>
    private static async Task<Device?> GetOrCreateDeviceAsync(
        TimeTrackerDbContext db, SyncBatchRequest batch, string presentedToken, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == batch.DeviceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (device is null)
        {
            device = new Device
            {
                DeviceId = batch.DeviceId,
                MachineName = batch.MachineName,
                LastUserName = batch.UserName,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                AgentToken = string.IsNullOrEmpty(presentedToken) ? null : presentedToken,
            };
            db.Devices.Add(device);
        }
        else if (!string.IsNullOrEmpty(device.AgentToken))
        {
            if (!FixedTimeEquals(presentedToken, device.AgentToken))
            {
                return null;
            }
        }
        else if (!string.IsNullOrEmpty(presentedToken))
        {
            device.AgentToken = presentedToken;
        }

        device.MachineName = batch.MachineName;
        device.LastUserName = batch.UserName;
        device.LastSeenUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        return device;
    }

    private static async Task AddNewAsync<TDto, TEntity>(
        DbSet<TEntity> set,
        List<TDto> incoming,
        Func<TDto, Guid> getEventId,
        Func<TDto, TEntity> toEntity,
        List<Guid> accepted,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (incoming.Count == 0)
        {
            return;
        }

        var incomingIds = incoming.Select(getEventId).ToList();
        var existingIds = (await set.Select(e => EF.Property<Guid>(e, "EventId"))
            .Where(id => incomingIds.Contains(id))
            .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var dto in incoming)
        {
            var eventId = getEventId(dto);
            if (!existingIds.Contains(eventId))
            {
                set.Add(toEntity(dto));
            }

            accepted.Add(eventId);
        }
    }

    private static async Task<IResult> HandleGetPolicyAsync(
        Guid deviceId, TimeTrackerDbContext db, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
        if (device is null)
        {
            // Unregistered devices get the permissive default until their first sync creates a row.
            return Results.Ok(new DeviceCapturePolicyDto(
                true, true, true, true, true, DeviceCapturePolicyDto.DefaultScreenshotIntervalMinutes));
        }

        return Results.Ok(new DeviceCapturePolicyDto(
            device.CaptureAppUsage,
            device.CaptureUrlVisits,
            device.CaptureIdle,
            device.CaptureSessionBreaks,
            device.CaptureScreenshots,
            device.ScreenshotIntervalMinutes));
    }

    private static async Task<IResult> HandleGetScreenshotAsync(
        Guid eventId, TimeTrackerDbContext db, HttpResponse response, CancellationToken cancellationToken)
    {
        var screenshot = await db.Screenshots
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.ImageBytes })
            .FirstOrDefaultAsync(cancellationToken);

        if (screenshot is null)
        {
            return Results.NotFound();
        }

        // Deliberately serves the hardcoded type rather than the stored column, so rows written
        // before ingest-time validation existed can't be used to serve active content either.
        // nosniff stops the browser second-guessing that type from the bytes.
        response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(screenshot.ImageBytes, ScreenshotContentType);
    }

    private class ApiKeyFilter(string? expectedKey) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            // Fails closed. This previously skipped the check entirely when no key was configured,
            // which meant a deployment that forgot to set Agent:ApiKey exposed ingest to anonymous
            // writes with nothing to indicate it. docker-compose happens to make the value required
            // (AGENT_API_KEY:?), but nothing stops the server being run outside compose.
            if (string.IsNullOrEmpty(expectedKey))
            {
                return Results.Problem(
                    "Agent API key is not configured on the server; agent ingest is disabled.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var provided = context.HttpContext.Request.Headers[ApiKeyHeader].ToString();
            if (!FixedTimeEquals(provided, expectedKey))
            {
                // Logged because a rejected Agent was otherwise completely invisible here: the
                // server said nothing, so an Agent turned away for a bad key looked exactly like
                // one that had never been installed. Reports the length only - never the value,
                // or any prefix of it, which would put a near-miss of the real key in the logs.
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(
                    "Rejected agent request to {Path} from {RemoteIp}: {Reason}. The Agent's "
                    + "AGENTAPIKEY must match the server's AGENT_API_KEY exactly.",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    provided.Length == 0
                        ? "no X-Agent-Key header"
                        : $"X-Agent-Key did not match (received {provided.Length} characters, expected {expectedKey.Length})");

                return Results.Unauthorized();
            }

            return await next(context);
        }
    }
}
