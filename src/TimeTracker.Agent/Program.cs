using TimeTracker.Agent;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Tracking;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<DeviceIdentity>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();

builder.Services.AddHostedService<ActivityTracker>();
builder.Services.AddHostedService<IdleTracker>();
builder.Services.AddHostedService<SessionBreakTracker>();
builder.Services.AddHostedService<ScreenshotCapturer>();

var host = builder.Build();
host.Run();
