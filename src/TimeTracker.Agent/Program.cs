using Microsoft.Extensions.Options;
using TimeTracker.Agent;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Agent.Tracking;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "TimeTrackerAgent");

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
