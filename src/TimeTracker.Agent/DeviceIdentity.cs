using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;

namespace TimeTracker.Agent;

public class DeviceIdentity
{
    public Guid DeviceId { get; }

    /// <summary>
    /// Per-device secret sent with every sync, claimed on this machine's first sync and required
    /// by the server on every sync after that. DeviceId alone is not proof of anything - it travels
    /// in the request body, so without this the org-wide agent key would be enough to submit
    /// activity attributed to any other machine.
    /// Machine-wide (alongside device-id.txt) rather than per-user, because DeviceId is itself
    /// machine-wide: every user session on this box shares one device identity.
    /// </summary>
    public string AgentToken { get; }

    public DeviceIdentity(IOptions<AgentOptions> options)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);

        var idFile = Path.Combine(options.Value.DataDirectory, "device-id.txt");
        if (File.Exists(idFile) && Guid.TryParse(File.ReadAllText(idFile).Trim(), out var existingId))
        {
            DeviceId = existingId;
        }
        else
        {
            DeviceId = Guid.NewGuid();
            File.WriteAllText(idFile, DeviceId.ToString());
        }

        var tokenFile = Path.Combine(options.Value.DataDirectory, "device-token.txt");
        var existingToken = File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : "";
        if (!string.IsNullOrEmpty(existingToken))
        {
            AgentToken = existingToken;
        }
        else
        {
            AgentToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(tokenFile, AgentToken);
        }
    }
}
