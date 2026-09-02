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

    public static void MapIngestEndpoints(this WebApplication app, IConfiguration configuration)
    {
        var apiKey = configuration["Agent:ApiKey"];

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

        var device = await GetOrCreateDeviceAsync(db, batch, cancellationToken);

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
            accepted.AddRange(batch.AppUsageEvents.Select(e => e.EventId));
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
            accepted.AddRange(batch.IdlePeriods.Select(e => e.EventId));
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
            accepted.AddRange(batch.UrlVisits.Select(e => e.EventId));
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
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                }, accepted, cancellationToken);
        }
        else
        {
            accepted.AddRange(batch.SessionBreaks.Select(e => e.EventId));
        }

        foreach (var dto in batch.Screenshots)
        {
            if (!device.CaptureScreenshots)
            {
                accepted.Add(dto.EventId);
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

            db.Screenshots.Add(new ScreenshotEvent
            {
                EventId = dto.EventId,
                DeviceId = batch.DeviceId,
                UserName = batch.UserName,
                CapturedAtUtc = dto.CapturedAtUtc,
                MonitorIndex = dto.MonitorIndex,
                WidthPx = dto.WidthPx,
                HeightPx = dto.HeightPx,
                ContentType = dto.ContentType,
                ImageBytes = stream.ToArray(),
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

    private static async Task<Device> GetOrCreateDeviceAsync(
        TimeTrackerDbContext db, SyncBatchRequest batch, CancellationToken cancellationToken)
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
            };
            db.Devices.Add(device);
        }
        else
        {
            device.MachineName = batch.MachineName;
            device.LastUserName = batch.UserName;
            device.LastSeenUtc = now;
        }

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
            return Results.Ok(new DeviceCapturePolicyDto(true, true, true, true, true));
        }

        return Results.Ok(new DeviceCapturePolicyDto(
            device.CaptureAppUsage,
            device.CaptureUrlVisits,
            device.CaptureIdle,
            device.CaptureSessionBreaks,
            device.CaptureScreenshots));
    }

    private static async Task<IResult> HandleGetScreenshotAsync(
        Guid eventId, TimeTrackerDbContext db, CancellationToken cancellationToken)
    {
        var screenshot = await db.Screenshots
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.ImageBytes, s.ContentType })
            .FirstOrDefaultAsync(cancellationToken);

        return screenshot is null
            ? Results.NotFound()
            : Results.File(screenshot.ImageBytes, screenshot.ContentType);
    }

    private class ApiKeyFilter(string? expectedKey) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!string.IsNullOrEmpty(expectedKey))
            {
                var provided = context.HttpContext.Request.Headers[ApiKeyHeader].ToString();
                if (!string.Equals(provided, expectedKey, StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }
            }

            return await next(context);
        }
    }
}
