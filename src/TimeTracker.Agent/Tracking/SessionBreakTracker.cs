using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Interop;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Records the time a user is away: screen locks (telling a user's own lock from an automatic
/// one), sign-outs, sleep and shutdown, each reported once it closes.
/// The Agent runs inside the user's session, so signing out or shutting down kills it mid-break.
/// Keeping the open break only in memory lost exactly those - the longest absences of all - so
/// it is written to a per-user state file the moment it opens, together with a heartbeat. The
/// next start closes it at the real Windows sign-in time, and a heartbeat gap spanning a sign-in
/// or reboot with no break on file (power loss, a killed process) still becomes one.
/// </summary>
public class SessionBreakTracker : BackgroundService
{
    // Win+L and Ctrl+Alt+Del are themselves input, so a user's own lock arrives with ~0s idle.
    // Windows' inactivity lock needs at least a minute of nothing. 30s sits clear of both.
    private static readonly TimeSpan AutoLockIdleThreshold = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    // Shorter gaps between the last heartbeat and a start are just the Agent restarting.
    private static readonly TimeSpan MinOfflineGap = TimeSpan.FromMinutes(2);

    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DevicePolicyCache _policyCache;
    private readonly ILogger<SessionBreakTracker> _logger;
    private readonly string _statePath;
    private readonly object _gate = new();

    private OpenBreak? _openBreak;
    private bool _persistFailureLogged;

    private sealed record OpenBreak(SessionBreakReason Reason, DateTimeOffset StartUtc, double? IdleSecondsAtStart);

    // StoppedCleanly: the Agent was shut down on purpose (tray Exit, upgrade) with no break open,
    // so a gap after it is time the user may well have spent working untracked, not away.
    private sealed record PersistedState(OpenBreak? OpenBreak, DateTimeOffset LastAliveUtc, bool StoppedCleanly = false);

    public SessionBreakTracker(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        DevicePolicyCache policyCache,
        IOptions<AgentOptions> options,
        ILogger<SessionBreakTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _policyCache = policyCache;
        _logger = logger;

        // Per user: the data directory is machine-wide, but each signed-in user runs their own
        // Agent with their own breaks.
        var safeUser = string.Concat(Environment.UserName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        _statePath = Path.Combine(options.Value.DataDirectory, $"session-state-{safeUser}.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            RecoverFromPreviousRun();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recover the session break left by the previous run");
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnded += OnSessionEnded;

        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            do
            {
                lock (_gate)
                {
                    Persist();
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;

            lock (_gate)
            {
                Persist(stoppedCleanly: true);
            }
        }
    }

    private void RecoverFromPreviousRun()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath));
        if (state is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var bootUtc = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var session = NativeMethods.GetSessionInfo();

        lock (_gate)
        {
            if (state.OpenBreak is { } previous)
            {
                var rebooted = bootUtc > previous.StartUtc;
                var signedInSince = session is { } s && s.LogonUtc > previous.StartUtc;

                if (!rebooted && !signedInSince && session is { IsLocked: true })
                {
                    // Restarted mid-break (watchdog, upgrade) while still locked: the unlock is
                    // still to come, so carry the break on rather than closing it early.
                    _openBreak = previous;
                    Persist();
                    return;
                }

                var endReason = rebooted || signedInSince
                    ? SessionBreakEndReason.Logon
                    : previous.Reason == SessionBreakReason.MachineSleep ? SessionBreakEndReason.MachineWake : SessionBreakEndReason.Unlock;

                Emit(previous, signedInSince ? session!.Value.LogonUtc : now, endReason);
            }
            else if (!state.StoppedCleanly && now - state.LastAliveUtc >= MinOfflineGap)
            {
                var rebooted = bootUtc > state.LastAliveUtc;
                var signedInSince = session is { } s && s.LogonUtc > state.LastAliveUtc;

                // Nothing was on file, so the session ended without a chance to say so: power
                // loss, a crash, or a forced sign-out. If neither a reboot nor a sign-in happened,
                // the Agent was merely not running while the user stayed signed in - untracked
                // time, not an absence, so it is deliberately not invented as one.
                if (rebooted || signedInSince)
                {
                    Emit(
                        new OpenBreak(rebooted ? SessionBreakReason.MachineShutdown : SessionBreakReason.Logoff, state.LastAliveUtc, null),
                        signedInSince ? session!.Value.LogonUtc : now,
                        SessionBreakEndReason.Logon);
                }
            }

            Persist();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                var idleSeconds = NativeMethods.GetIdleMilliseconds() / 1000.0;
                Open(idleSeconds >= AutoLockIdleThreshold.TotalSeconds ? SessionBreakReason.AutoLock : SessionBreakReason.Lock, idleSeconds);
                break;
            case SessionSwitchReason.SessionLogoff:
                Open(SessionBreakReason.Logoff, null);
                break;
            case SessionSwitchReason.SessionUnlock:
                Close(SessionBreakEndReason.Unlock);
                break;
            case SessionSwitchReason.SessionLogon:
                Close(SessionBreakEndReason.Logon);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Open(SessionBreakReason.MachineSleep, null);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Close(SessionBreakEndReason.MachineWake);
        }
    }

    // SessionEnded rather than SessionEnding: the latter can still be cancelled by another app
    // refusing to close, which would leave a break open with no event ever coming to end it.
    // An existing break (locked, then shut down from the lock screen) keeps its original start.
    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) =>
        Open(e.Reason == SessionEndReasons.SystemShutdown ? SessionBreakReason.MachineShutdown : SessionBreakReason.Logoff, null);

    private void Open(SessionBreakReason reason, double? idleSecondsAtStart)
    {
        lock (_gate)
        {
            if (_openBreak is not null)
            {
                return;
            }

            _openBreak = new OpenBreak(reason, DateTimeOffset.UtcNow, idleSecondsAtStart);

            // Immediately: sign-out and shutdown may not leave time for the next heartbeat.
            Persist();
        }
    }

    private void Close(SessionBreakEndReason endReason)
    {
        lock (_gate)
        {
            if (_openBreak is not { } openBreak)
            {
                return;
            }

            _openBreak = null;
            Persist();
            Emit(openBreak, DateTimeOffset.UtcNow, endReason);
        }
    }

    private void Emit(OpenBreak openBreak, DateTimeOffset endUtc, SessionBreakEndReason endReason)
    {
        if (!_policyCache.Current.CaptureSessionBreaks || endUtc <= openBreak.StartUtc)
        {
            return;
        }

        try
        {
            _store.AddSessionBreakAsync(
                new SessionBreakEventDto(
                    Guid.NewGuid(),
                    _deviceIdentity.DeviceId,
                    openBreak.StartUtc,
                    endUtc,
                    openBreak.Reason,
                    endReason,
                    openBreak.IdleSecondsAtStart),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store session break ({Reason} -> {EndReason})", openBreak.Reason, endReason);
        }
    }

    // Callers hold _gate. Written to a temp file and moved into place, so a session killed
    // mid-write leaves the previous state intact rather than a truncated file.
    private void Persist(bool stoppedCleanly = false)
    {
        try
        {
            var tempPath = _statePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new PersistedState(_openBreak, DateTimeOffset.UtcNow, stoppedCleanly)));
            File.Move(tempPath, _statePath, overwrite: true);
            _persistFailureLogged = false;
        }
        catch (Exception ex)
        {
            if (!_persistFailureLogged)
            {
                _logger.LogWarning(ex, "Failed to save session break state to {Path}; a sign-out or shutdown now would go unrecorded", _statePath);
                _persistFailureLogged = true;
            }
        }
    }
}
