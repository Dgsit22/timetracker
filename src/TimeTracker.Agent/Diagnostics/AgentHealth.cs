using System.Net.NetworkInformation;
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

    /// <summary>
    /// How long a connection has to stay broken before the console hears about it. Measured
    /// against this machine's own logs: nearly every failure here is a single 30-second tick -
    /// a Wi-Fi roam or a DHCP renewal - and reporting those would bury the real faults.
    /// </summary>
    public static readonly TimeSpan ReportConnectionFailureAfter = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly IEventStore _store;
    private readonly ILogger<AgentHealth> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _isNetworkAvailable;

    private DateTimeOffset? _connectionFailingSince;
    private bool _reportedThisOutage;

    private sealed class Pending
    {
        public DateTimeOffset FirstSeenUtc;
        public DateTimeOffset LastReportedUtc;
        public int SuppressedCount;
    }

    /// <param name="now">Injectable so the timing rules can be tested without waiting them out.</param>
    /// <param name="isNetworkAvailable">
    /// Injectable for the same reason. Defaults to asking Windows, which reports no network while
    /// a laptop sits in Modern Standby - the state behind almost every outage on this fleet.
    /// </param>
    public AgentHealth(
        IEventStore store,
        ILogger<AgentHealth> logger,
        Func<DateTimeOffset>? now = null,
        Func<bool>? isNetworkAvailable = null)
    {
        _store = store;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _isNetworkAvailable = isNetworkAvailable ?? NetworkInterface.GetIsNetworkAvailable;
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
        bool announceRecovery;

        lock (_gate)
        {
            IsServerReachable = true;
            LastSuccessfulSyncUtc = _now();
            QueuedEventCount = queued;
            LastErrorMessage = null;

            // Only worth announcing if the outage was reported in the first place. A blip that
            // never reached the console would otherwise produce a "Reconnected" line with nothing
            // before it, which reads like a fault whose cause is missing.
            announceRecovery = _reportedThisOutage;
            _connectionFailingSince = null;
            _reportedThisOutage = false;
        }

        if (announceRecovery)
        {
            // Closes the gap the console would otherwise show as an unexplained silence, and says
            // when delivery resumed.
            _ = ReportAsync(AgentLogLevel.Info, "SyncClient", "Reconnected to the server", null, CancellationToken.None);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A failed attempt to reach the server, which is not the same kind of event as a fault in the
    /// Agent and must not be reported like one.
    ///
    /// These machines are laptops. Windows disconnects the network during Modern Standby by power
    /// policy, so a closed lid produces a failure every sync interval for as long as it stays
    /// closed - one overnight standby on this machine logged 196 of them. Reporting each, or even
    /// each coalesced group, would fill the console with sleeping laptops and bury the faults an
    /// admin can act on.
    ///
    /// So two gates. No network at all is never reported: that is a lid, not a fault, and the
    /// person at the machine can already see it in the tray. A network that exists but cannot
    /// reach the server is reported only once it has stayed that way, which drops the
    /// single-tick blips that make up nearly every failure in this fleet's history.
    /// </summary>
    public async Task ReportConnectionFailureAsync(
        string source, string detail, CancellationToken cancellationToken)
    {
        var now = _now();
        var hasNetwork = SafeIsNetworkAvailable();
        var message = hasNetwork ? "Cannot reach the server" : "No network connection";
        bool shouldReport;

        lock (_gate)
        {
            _connectionFailingSince ??= now;
            IsServerReachable = false;
            LastErrorMessage = message;
            LastErrorUtc = now;

            shouldReport = hasNetwork
                && now - _connectionFailingSince >= ReportConnectionFailureAfter;

            if (shouldReport)
            {
                _reportedThisOutage = true;
            }
        }

        // The tray updates either way: whoever is sitting at the machine should see the state
        // immediately, even when the console is deliberately not being told.
        Changed?.Invoke();

        if (!shouldReport)
        {
            return;
        }

        await ReportAsync(
            AgentLogLevel.Warning, source, message, detail, cancellationToken, serverUnreachable: true);
    }

    private bool SafeIsNetworkAvailable()
    {
        try
        {
            return _isNetworkAvailable();
        }
        catch (Exception)
        {
            // Unable to tell; assume there is a network so a real fault is still reported.
            return true;
        }
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
        var now = _now();
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
