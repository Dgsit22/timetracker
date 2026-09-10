using Microsoft.Extensions.Options;
using TimeTracker.Agent;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Agent.Tracking;
using System.Windows.Forms;

if (InstallTimeConfig.TryHandleCommandLine(args))
{
    return;
}

if (ConnectionTest.TryHandleCommandLine(args))
{
    return;
}

// `--settings` opens just the Settings window and exits, for the Start Menu shortcut. The Agent
// has no main window (it's a background tracker), so without this the only way in is the tray
// icon's right-click menu - easy to miss entirely when the icon is tucked behind the taskbar's
// overflow chevron. Deliberately ahead of the single-instance guard below: this is a short-lived
// UI-only invocation, and it has to work while the real tracking instance is running and holding
// the mutex. Own STA thread for the same reason the tray icon gets one - see the note further down.
if (args.Length > 0 && args[0] == "--settings")
{
    var settingsThread = new Thread(() => new System.Windows.Application().Run(new SettingsWindow()));
    settingsThread.SetApartmentState(ApartmentState.STA);
    settingsThread.Start();
    settingsThread.Join();
    return;
}

// Tracking in session 0 produces confidently wrong data, so refuse to start there at all.
// A per-machine install run by SYSTEM (an RMM push, or msiexec from a SYSTEM shell) launches
// this exe from session 0, which has no interactive desktop. Nothing errors - it is worse than
// that. GetForegroundWindow finds no window, so app usage is silently empty; GetLastInputInfo
// reports a session that by definition never receives input, so the machine looks permanently
// idle and the outbox fills with fabricated idle periods; and SessionSwitch never fires, because
// session 0 is never locked or unlocked. Observed in the field as a device reporting 0m active,
// 37m of idle nobody actually took, and no lock/unlock events at all.
// Exiting leaves startup to the two paths that do land in a real user session: the all-users
// Startup shortcut at logon, and the watchdog task (/RU Users /IT). Deliberately after the CLI
// modes above - "--write-config" in particular *is* invoked as SYSTEM by the installer and must
// keep working.
if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
{
    try
    {
        System.Diagnostics.EventLog.WriteEntry(
            InstallTimeConfig.EventSourceName,
            "Not starting: session 0 has no interactive desktop, so tracking there would record "
            + "empty app usage and fabricated idle time. The Startup shortcut or the watchdog task "
            + "will start the Agent inside the user's session instead.",
            System.Diagnostics.EventLogEntryType.Warning);
    }
    catch (Exception)
    {
        // Event source may not exist yet on a first install; the exit matters, the log doesn't.
    }

    return;
}

// Guards only the long-running tracking mode below, not the CLI modes above: a stray
// double-click of the exe (it has no window, so it's easy to not notice one's already
// running) would otherwise spawn a second set of trackers writing to the same local
// SQLite outbox concurrently. Deliberately session-scoped ("Local\", the default for an
// unprefixed name under Terminal Services), not machine-wide ("Global\"): this app is
// designed to run one instance *per logged-in user*, so two different users
// legitimately each get their own instance under RDP/fast user switching.
using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\TimeTrackerAgent-SingleInstance", createdNew: out var isFirstInstance);
if (!isFirstInstance)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Runs per-user from the Startup folder, not as a LocalSystem service: a Windows
// Service runs in Session 0, which has no interactive desktop, so GetForegroundWindow,
// GetLastInputInfo, and screen capture all silently return nothing useful there.

// Written by the MSI installer (and editable later without a reinstall) at a path
// outside Program Files; layered after appsettings.json so it takes precedence.
builder.Configuration.AddJsonFile(InstallTimeConfig.GetPath(), optional: true, reloadOnChange: false);

// No console window (WinExe), so this is the only place diagnostics are visible.
// The event source is pre-created at install time, since creating one needs admin rights.
builder.Logging.AddEventLog(settings => settings.SourceName = InstallTimeConfig.EventSourceName);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<DeviceIdentity>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<DevicePolicyCache>();

builder.Services.AddHttpClient("TimeTrackerServer", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    client.BaseAddress = new Uri(options.ServerBaseUrl);
    if (!string.IsNullOrEmpty(options.AgentApiKey))
    {
        client.DefaultRequestHeaders.Add("X-Agent-Key", options.AgentApiKey);
    }
});

builder.Services.AddHostedService<ActivityTracker>();
builder.Services.AddHostedService<IdleTracker>();
builder.Services.AddHostedService<SessionBreakTracker>();
builder.Services.AddHostedService<ScreenshotCapturer>();
builder.Services.AddHostedService<PolicySyncService>();
builder.Services.AddHostedService<SyncClient>();

var host = builder.Build();
await host.StartAsync();

// The Agent's only visible UI is a tray icon (see TrayIcon.cs) - it needs a WinForms
// message loop, which the Generic Host doesn't provide on its own. Run that loop on its
// own explicitly-created STA thread rather than assuming the top-level Main's own
// apartment state, so this doesn't depend on SDK-inferred [STAThread] behavior.
var serverUrl = ConnectionTest.GetConfiguredServerUrl();
var trayThread = new Thread(() =>
{
    using var trayIcon = new AgentTrayIcon(serverUrl);
    Application.Run();
});
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();
trayThread.Join();

await host.StopAsync();
