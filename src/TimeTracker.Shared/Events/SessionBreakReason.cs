namespace TimeTracker.Shared.Events;

// Stored and sent as integers: only ever append, never reorder.
public enum SessionBreakReason
{
    /// <summary>
    /// Locked with input just before it (Win+L, Ctrl+Alt+Del). Rows recorded before lock kinds
    /// were told apart also carry this value, with a null IdleSecondsAtStart.
    /// </summary>
    Lock,
    Logoff,
    MachineSleep,
    MachineShutdown,

    /// <summary>Locked by Windows after a stretch with no keyboard or mouse input.</summary>
    AutoLock,
}
