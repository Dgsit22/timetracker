using Microsoft.Win32;
using TimeTracker.Agent.Storage;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Listens for workstation lock/unlock, logon/logoff, and sleep/wake and emits a
/// completed SessionBreakEventDto once a break closes. Breaks left open by an
/// agent restart or unclean shutdown are dropped rather than reported half-formed.
/// </summary>
public class SessionBreakTracker : BackgroundService
{
    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly ILogger<SessionBreakTracker> _logger;

    private (SessionBreakReason Reason, DateTimeOffset StartUtc)? _openBreak;

    public SessionBreakTracker(IEventStore store, DeviceIdentity deviceIdentity, ILogger<SessionBreakTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                OpenBreak(SessionBreakReason.Lock);
                break;
            case SessionSwitchReason.SessionLogoff:
                OpenBreak(SessionBreakReason.Logoff);
                break;
            case SessionSwitchReason.SessionUnlock:
                CloseBreak(SessionBreakEndReason.Unlock);
                break;
            case SessionSwitchReason.SessionLogon:
                CloseBreak(SessionBreakEndReason.Logon);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            OpenBreak(SessionBreakReason.MachineSleep);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            CloseBreak(SessionBreakEndReason.MachineWake);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // Best effort: catches machine shutdown, which fires no dedicated event here.
        OpenBreak(SessionBreakReason.MachineShutdown);
    }

    private void OpenBreak(SessionBreakReason reason)
    {
        _openBreak ??= (reason, DateTimeOffset.UtcNow);
    }

    private void CloseBreak(SessionBreakEndReason endReason)
    {
        if (_openBreak is not { } openBreak)
        {
            return;
        }

        _openBreak = null;
        var endUtc = DateTimeOffset.UtcNow;

        try
        {
            _store.AddSessionBreakAsync(
                new SessionBreakEventDto(
                    Guid.NewGuid(),
                    _deviceIdentity.DeviceId,
                    openBreak.StartUtc,
                    endUtc,
                    openBreak.Reason,
                    endReason),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store session break ({Reason} -> {EndReason})", openBreak.Reason, endReason);
        }
    }
}
