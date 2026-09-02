using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;

namespace TimeTracker.Agent;

public class DeviceIdentity
{
    public Guid DeviceId { get; }

    public DeviceIdentity(IOptions<AgentOptions> options)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        var idFile = Path.Combine(options.Value.DataDirectory, "device-id.txt");

        if (File.Exists(idFile) && Guid.TryParse(File.ReadAllText(idFile).Trim(), out var existingId))
        {
            DeviceId = existingId;
            return;
        }

        DeviceId = Guid.NewGuid();
        File.WriteAllText(idFile, DeviceId.ToString());
    }
}
