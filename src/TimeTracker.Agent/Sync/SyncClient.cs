using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Diagnostics;
using TimeTracker.Agent.Storage;
using TimeTracker.Shared.Diagnostics;
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

    // A quiet machine can legitimately have no app switches, idle transitions, screenshots, or
    // session breaks for a long time. Still check in periodically so the server can distinguish
    // a healthy-but-quiet Agent from one that is offline. This is a device-health signal only;
    // an empty batch never creates activity records.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);

    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AgentOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncClient> _logger;
    private readonly AgentHealth _health;
    private DateTimeOffset? _lastHeartbeatUtc;

    public SyncClient(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        IOptions<AgentOptions> options,
        IHttpClientFactory httpClientFactory,
        AgentHealth health,
        ILogger<SyncClient> logger)
    {
        _health = health;
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

                if (ex is HttpRequestException or TaskCanceledException)
                {
                    // Connectivity, which on a laptop is usually a closed lid - see
                    // AgentHealth.ReportConnectionFailureAsync for why that is gated.
                    await _health.ReportConnectionFailureAsync("SyncClient", ex.Message, stoppingToken);
                }
                else
                {
                    await _health.ReportAsync(
                        AgentLogLevel.Error, "SyncClient", "Sync attempt failed", ex.Message, stoppingToken);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var batch = await _store.GetPendingBatchAsync(_options.SyncBatchSize, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var isHeartbeatDue = _lastHeartbeatUtc is null || now - _lastHeartbeatUtc >= HeartbeatInterval;

        if (batch.IsEmpty && !isHeartbeatDue)
        {
            return;
        }

        var batchRequest = new SyncBatchRequest(
            _deviceIdentity.DeviceId,
            AgentVersion,
            Environment.UserName,
            Environment.MachineName,
            now,
            batch.AppUsageEvents,
            batch.IdlePeriods,
            batch.UrlVisits,
            batch.Screenshots.Select(s => s.Dto).ToList(),
            batch.SessionBreaks,
            batch.Diagnostics);

        using var content = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(batchRequest), Encoding.UTF8, "application/json"), "batch" },
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

        // Per-request rather than on the named client's default headers, because the token is
        // device state resolved at runtime, not deployment config like the shared agent key.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/sync") { Content = content };
        httpRequest.Headers.Add("X-Device-Token", _deviceIdentity.AgentToken);

        using var response = await client.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sync failed with status {StatusCode}", response.StatusCode);

            var message = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "Server refused this Agent's API key",
                System.Net.HttpStatusCode.Forbidden =>
                    "Server rejected this device's token",
                System.Net.HttpStatusCode.ServiceUnavailable =>
                    "Server has no agent API key configured",
                _ => $"Server returned {(int)response.StatusCode} {response.StatusCode}",
            };

            var detail = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                ? "AGENTAPIKEY on this machine must match AGENT_API_KEY on the server exactly. Events stay queued locally until it does."
                : $"POST /api/ingest/sync returned {(int)response.StatusCode}.";

            // Not marked unreachable: the server answered. This is a configuration fault, and
            // calling it "offline" in the tray would send someone to check the network instead.
            await _health.ReportAsync(
                AgentLogLevel.Error, "SyncClient", message, detail, cancellationToken);
            return;
        }

        // The endpoint updates Device.LastSeenUtc before processing the batch. Record the
        // successful check-in only after a 2xx response, so a failed heartbeat retries on the
        // normal sync cadence instead of leaving the server stale for another hour.
        _lastHeartbeatUtc = now;

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

        _health.RecordSyncSuccess(await _store.CountPendingAsync(cancellationToken));

        if (batch.IsEmpty)
        {
            _logger.LogInformation("Sent hourly agent heartbeat");
        }
        else
        {
            _logger.LogInformation(
                "Synced batch: {Accepted} accepted, {Rejected} rejected",
                result.AcceptedEventIds.Count, result.Rejected.Count);
        }
    }
}
