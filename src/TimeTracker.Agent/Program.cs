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
