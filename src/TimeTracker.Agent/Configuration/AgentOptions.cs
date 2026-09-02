namespace TimeTracker.Agent.Configuration;

public class AgentOptions
{
    public const string SectionName = "Agent";

    public int ActivePollIntervalSeconds { get; set; } = 2;

    public int IdlePollIntervalSeconds { get; set; } = 5;

    public int IdleThresholdSeconds { get; set; } = 300;

    public int ScreenshotIntervalSeconds { get; set; } = 600;

    public string ServerBaseUrl { get; set; } = "http://localhost:5081";

    public int SyncIntervalSeconds { get; set; } = 30;

    public int SyncBatchSize { get; set; } = 200;

    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TimeTracker");
}
