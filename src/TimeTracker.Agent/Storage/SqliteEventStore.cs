using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Storage;

/// <summary>
/// Local outbox: every captured event is written here first so nothing is lost
/// if the sync client can't reach the server. The sync client drains and deletes rows.
/// </summary>
public class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;
    private readonly string _screenshotDirectory;

    public SqliteEventStore(IOptions<AgentOptions> options)
    {
        var dataDirectory = options.Value.DataDirectory;
        Directory.CreateDirectory(dataDirectory);

        _screenshotDirectory = Path.Combine(dataDirectory, "Screenshots");
        Directory.CreateDirectory(_screenshotDirectory);

        var dbPath = Path.Combine(dataDirectory, "agent.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS OutboxEvents (
                EventId TEXT PRIMARY KEY,
                EventType TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public Task AddAppUsageEventAsync(AppUsageEventDto evt, CancellationToken cancellationToken) =>
        InsertAsync(evt.EventId, "AppUsage", evt, cancellationToken);

    public Task AddIdlePeriodAsync(IdlePeriodEventDto evt, CancellationToken cancellationToken) =>
        InsertAsync(evt.EventId, "IdlePeriod", evt, cancellationToken);

    public Task AddUrlVisitAsync(UrlVisitEventDto evt, CancellationToken cancellationToken) =>
        InsertAsync(evt.EventId, "UrlVisit", evt, cancellationToken);

    public Task AddSessionBreakAsync(SessionBreakEventDto evt, CancellationToken cancellationToken) =>
        InsertAsync(evt.EventId, "SessionBreak", evt, cancellationToken);

    public async Task AddScreenshotAsync(ScreenshotEventDto evt, byte[] imageBytes, CancellationToken cancellationToken)
    {
        var imagePath = Path.Combine(_screenshotDirectory, $"{evt.EventId}.png");
        await File.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken);
        await InsertAsync(evt.EventId, "Screenshot", evt, cancellationToken);
    }

    public async Task<PendingBatch> GetPendingBatchAsync(int maxItems, CancellationToken cancellationToken)
    {
        var appUsageEvents = new List<AppUsageEventDto>();
        var idlePeriods = new List<IdlePeriodEventDto>();
        var urlVisits = new List<UrlVisitEventDto>();
        var sessionBreaks = new List<SessionBreakEventDto>();
        var screenshots = new List<PendingScreenshot>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, EventType, PayloadJson FROM OutboxEvents
            ORDER BY CreatedAtUtc ASC
            LIMIT $maxItems;
            """;
        command.Parameters.AddWithValue("$maxItems", maxItems);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = Guid.Parse(reader.GetString(0));
            var eventType = reader.GetString(1);
            var payloadJson = reader.GetString(2);

            switch (eventType)
            {
                case "AppUsage":
                    appUsageEvents.Add(JsonSerializer.Deserialize<AppUsageEventDto>(payloadJson)!);
                    break;
                case "IdlePeriod":
                    idlePeriods.Add(JsonSerializer.Deserialize<IdlePeriodEventDto>(payloadJson)!);
                    break;
                case "UrlVisit":
                    urlVisits.Add(JsonSerializer.Deserialize<UrlVisitEventDto>(payloadJson)!);
                    break;
                case "SessionBreak":
                    sessionBreaks.Add(JsonSerializer.Deserialize<SessionBreakEventDto>(payloadJson)!);
                    break;
                case "Screenshot":
                    var dto = JsonSerializer.Deserialize<ScreenshotEventDto>(payloadJson)!;
                    screenshots.Add(new PendingScreenshot(dto, Path.Combine(_screenshotDirectory, $"{eventId}.png")));
                    break;
            }
        }

        return new PendingBatch(appUsageEvents, idlePeriods, urlVisits, sessionBreaks, screenshots);
    }

    public async Task RemoveEventsAsync(IEnumerable<Guid> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM OutboxEvents WHERE EventId = $eventId;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$eventId";
            command.Parameters.Add(parameter);

            foreach (var id in ids)
            {
                parameter.Value = id.ToString();
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var id in ids)
        {
            var path = Path.Combine(_screenshotDirectory, $"{id}.png");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private async Task InsertAsync<T>(Guid eventId, string eventType, T payload, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OutboxEvents (EventId, EventType, PayloadJson, CreatedAtUtc)
            VALUES ($eventId, $eventType, $payloadJson, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
