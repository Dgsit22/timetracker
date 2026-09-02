using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Events;
using TimeTracker.Shared.Sync;

namespace TimeTracker.Server.Ingest;

public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/ingest/sync", HandleSyncAsync);
        app.MapGet("/api/screenshots/{eventId:guid}", HandleGetScreenshotAsync);
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

        var accepted = new List<Guid>();
        var rejected = new List<SyncErrorDto>();

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

        foreach (var dto in batch.Screenshots)
        {
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
}
