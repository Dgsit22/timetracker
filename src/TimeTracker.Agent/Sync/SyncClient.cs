using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Shared.Sync;

namespace TimeTracker.Agent.Sync;

/// <summary>
/// Periodically drains the local event outbox and POSTs it to the server's
/// /api/ingest/sync endpoint. Accepted and rejected events are both removed
/// from the outbox: rejected ones are permanently invalid (e.g. malformed),
/// so retrying them would just loop forever.
/// </summary>
public class SyncClient : BackgroundService
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AgentOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncClient> _logger;

    public SyncClient(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        IOptions<AgentOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<SyncClient> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SyncIntervalSeconds));

        do
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync attempt failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var batch = await _store.GetPendingBatchAsync(_options.SyncBatchSize, cancellationToken);
        if (batch.IsEmpty)
        {
            return;
        }

        var request = new SyncBatchRequest(
            _deviceIdentity.DeviceId,
            AgentVersion,
            Environment.UserName,
            Environment.MachineName,
            DateTimeOffset.UtcNow,
            batch.AppUsageEvents,
            batch.IdlePeriods,
            batch.UrlVisits,
            batch.Screenshots.Select(s => s.Dto).ToList(),
            batch.SessionBreaks);

        using var content = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"), "batch" },
        };

        foreach (var screenshot in batch.Screenshots)
        {
            if (!File.Exists(screenshot.FilePath))
            {
                continue;
            }

            var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(screenshot.FilePath, cancellationToken));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(screenshot.Dto.ContentType);
            content.Add(fileContent, screenshot.Dto.EventId.ToString(), $"{screenshot.Dto.EventId}.png");
        }

        var client = _httpClientFactory.CreateClient("TimeTrackerServer");
        using var response = await client.PostAsync("/api/ingest/sync", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sync failed with status {StatusCode}", response.StatusCode);
            return;
        }

        var result = await response.Content.ReadFromJsonAsync<SyncBatchResponse>(cancellationToken: cancellationToken);
        if (result is null)
        {
            return;
        }

        foreach (var rejection in result.Rejected)
        {
            _logger.LogWarning("Server rejected event {EventId}: {Reason}", rejection.EventId, rejection.Reason);
        }

        var toRemove = result.AcceptedEventIds.Concat(result.Rejected.Select(r => r.EventId));
        await _store.RemoveEventsAsync(toRemove, cancellationToken);

        _logger.LogInformation(
            "Synced batch: {Accepted} accepted, {Rejected} rejected",
            result.AcceptedEventIds.Count, result.Rejected.Count);
    }
}
