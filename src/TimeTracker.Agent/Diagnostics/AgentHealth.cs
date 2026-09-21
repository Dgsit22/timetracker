using Microsoft.Extensions.Logging;
using TimeTracker.Agent.Storage;
using TimeTracker.Shared.Diagnostics;

namespace TimeTracker.Agent.Diagnostics;

/// <summary>
/// What the Agent knows about its own condition, and the one place faults are reported from.
///
/// Two jobs, deliberately together. It queues a report for the admin console, and it holds the
/// current state for the tray icon - because the failure this exists for is the Agent being
/// unable to reach the server, and in exactly that case the console cannot be told anything. The
/// person sitting at the machine has to be able to see it locally, so the state is kept here and
/// the report waits in the outbox until the connection returns.
/// </summary>
public class AgentHealth
{
    /// <summary>
    /// A connection failure recurs every sync interval. Reporting each one would fill the outbox
    /// with thousands of copies of the same sentence, which then have to be uploaded and stored,
    /// so identical faults are coalesced into one report carrying a count.
    /// </summary>
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly IEventStore _store;
    private readonly ILogger<AgentHealth> _logger;

    private sealed class Pending
    {
        public DateTimeOffset FirstSeenUtc;
        public DateTimeOffset LastReportedUtc;
        public int SuppressedCount;
    }

    public AgentHealth(IEventStore store, ILogger<AgentHealth> logger)
    {
        _store = store;
        _logger = logger;
    }

    public DateTimeOffset? LastSuccessfulSyncUtc { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset? LastErrorUtc { get; private set; }

    /// <summary>
    /// False once a sync has failed and not yet succeeded. Starts true so a freshly started Agent
    /// does not claim to be broken before it has tried anything.
    /// </summary>
    public bool IsServerReachable { get; private set; } = true;

    public int QueuedEventCount { get; private set; }

    public event Action? Changed;

    public void RecordSyncSuccess(int queued)
    {
        var wasUnreachable = !IsServerReachable;

        lock (_gate)
        {
            IsServerReachable = true;
            LastSuccessfulSyncUtc = DateTimeOffset.UtcNow;
            QueuedEventCount = queued;
            LastErrorMessage = null;
        }

        if (wasUnreachable)
        {
            // Worth a line of its own: it closes the gap the console would otherwise show as an
            // unexplained silence, and tells the reader when delivery resumed.
            _ = ReportAsync(AgentLogLevel.Info, "SyncClient", "Reconnected to the server", null, CancellationToken.None);
        }

        Changed?.Invoke();
    }

    public void RecordQueueDepth(int queued)
    {
        lock (_gate)
        {
            QueuedEventCount = queued;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Records a fault, updates the local state the tray shows, and queues a report for the
    /// console unless the identical fault was already reported recently.
    /// </summary>
    public async Task ReportAsync(
        AgentLogLevel level,
        string source,
        string message,
        string? detail,
        CancellationToken cancellationToken,
        bool serverUnreachable = false)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{source}|{message}";
        bool shouldSend;
        int suppressed = 0;
        DateTimeOffset firstSeen = now;

        lock (_gate)
        {
            if (level >= AgentLogLevel.Warning)
            {
                LastErrorMessage = message;
                LastErrorUtc = now;
            }

            if (serverUnreachable)
            {
                IsServerReachable = false;
            }

            if (_pending.TryGetValue(key, out var seen))
            {
                if (now - seen.LastReportedUtc < RepeatAfter)
                {
                    seen.SuppressedCount++;
                    shouldSend = false;
                }
                else
                {
                    suppressed = seen.SuppressedCount + 1;
                    firstSeen = seen.FirstSeenUtc;
                    seen.LastReportedUtc = now;
                    seen.SuppressedCount = 0;
                    seen.FirstSeenUtc = now;
                    shouldSend = true;
                }
            }
            else
            {
                _pending[key] = new Pending { FirstSeenUtc = now, LastReportedUtc = now };
                suppressed = 1;
                shouldSend = true;
            }
        }

        Changed?.Invoke();

        if (!shouldSend)
        {
            return;
        }

        try
        {
            await _store.AddDiagnosticAsync(
                new AgentLogDto(Guid.NewGuid(), firstSeen, level, source, message, Truncate(detail), suppressed),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The outbox itself is broken. Nothing useful is left to do but say so in the event
            // log - trying to queue a report about failing to queue reports would recurse.
            _logger.LogError(ex, "Could not queue a diagnostic report");
        }
    }

    /// <summary>
    /// Stack traces are worth keeping but not worth uploading whole: the first frames carry the
    /// cause, and the console shows this in a table cell.
    /// </summary>
    private static string? Truncate(string? detail) =>
        detail is { Length: > 2000 } ? detail[..2000] + "..." : detail;
}
