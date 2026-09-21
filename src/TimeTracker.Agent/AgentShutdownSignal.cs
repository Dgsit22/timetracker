namespace TimeTracker.Agent;

/// <summary>
/// Lets one process ask the running Agent to shut down cleanly.
///
/// The Settings window's "Save and restart" needs the tracking instance to exit so the watchdog's
/// relaunch picks up the new configuration. From the tray that instance is the same process, so
/// exiting its message loop was enough - but the Start Menu shortcut runs Settings as a separate,
/// UI-only process (see the --settings branch in Program.cs), where exiting the local message loop
/// does nothing at all. The tracking process kept running, kept holding the single-instance mutex,
/// and so the relaunch the button had just scheduled exited immediately as a duplicate. The saved
/// settings then sat unused until the next real restart, with nothing on screen to say so.
///
/// A named event covers both cases identically: whoever is doing the tracking listens, whoever
/// saved the settings signals, and the tray-hosted case simply signals itself.
///
/// "Local\" rather than "Global\" to match the single-instance mutex: one Agent per logged-in
/// user, so a shutdown request must reach that user's instance and not another session's.
/// </summary>
public static class AgentShutdownSignal
{
    private const string EventName = @"Local\TimeTrackerAgent-Shutdown";

    /// <summary>
    /// Created once by the tracking instance. Manual reset: the point is a one-way "time to go",
    /// and nothing should race to consume it.
    /// </summary>
    public static EventWaitHandle CreateListener() =>
        new(initialState: false, mode: EventResetMode.ManualReset, name: EventName);

    /// <summary>
    /// Returns false when no Agent is listening - a Settings window opened while the Agent is not
    /// running is a perfectly ordinary case, and the caller decides what to say about it.
    /// </summary>
    public static bool Request()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                return false;
            }

            using (handle)
            {
                handle.Set();
                return true;
            }
        }
        catch (Exception)
        {
            // Nothing here is worth failing a save over: the watchdog's own next tick restarts
            // the Agent within five minutes regardless.
            return false;
        }
    }
}
