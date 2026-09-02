using Microsoft.Extensions.Options;
using TimeTracker.Agent;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Agent.Tracking;

if (InstallTimeConfig.TryHandleCommandLine(args))
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
host.Run();
