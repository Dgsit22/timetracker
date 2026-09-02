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
