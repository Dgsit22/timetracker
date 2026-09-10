# TimeTracker Agent — Windows installer

Builds an MSI that installs the Agent per-machine and launches it per-user from
the all-users Startup folder (`%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp`).

It's deliberately **not** a Windows Service: a service runs in Session 0, which
has no interactive desktop, so foreground-window detection, idle detection,
and screen capture all silently return nothing there. Running from Startup
gives every user who logs into the machine their own Agent instance with
normal desktop access.

The install also registers a **"TimeTracker Agent Watchdog"** scheduled task
that checks every 5 minutes and relaunches the exe if it isn't running. This
covers what the Startup shortcut alone can't: a crash, a kill from Task
Manager/AV, or an install/repair that stops the running exe (Windows'
Restart Manager does this automatically to release the locked file) with no
one logging back in afterward to re-trigger Startup. It's safe to fire even
when the Agent is already running — the Agent's own single-instance mutex
makes the redundant launch an instant no-op. Uninstalling removes the task.

## Prerequisites (on the build machine)

```
dotnet tool install --global wix --version 5.0.2
```
Use `5.0.2`, not the latest `wix` — WiX v6/v7 require accepting a paid Open
Source Maintenance Fee EULA to build with; v5 doesn't.

## Build

From the repo root:

```powershell
# 1. Publish the Agent self-contained (no .NET runtime needed on target machines)
dotnet publish src/TimeTracker.Agent/TimeTracker.Agent.csproj -c Release -r win-x64 --self-contained true -o publish/agent-win-x64

# 2. Build the MSI
cd installer
wix build Product.wxs -arch x64 -ext WixToolset.UI.wixext -d PublishDir="../publish/agent-win-x64" -o TimeTracker.Agent-Setup.msi
```

`-arch x64` is required — without it WiX defaults to x86 and `ProgramFiles64Folder`
resolves to the wrong (`Program Files (x86)`) location.

## Install (per machine)

```
msiexec /i TimeTracker.Agent-Setup.msi /qn SERVERURL=https://192.168.0.15:5443 AGENTAPIKEY=your-agent-api-key
```

- `SERVERURL` must be the **https** URL on port `5443`, and must match the address in the server's
  certificate — see "TLS" below. It defaults to `http://localhost:5081` (a local-development
  convenience) if omitted, which will not reach a production server.
- `AGENTAPIKEY` must match the server's `Agent:ApiKey` (Docker: the `AGENT_API_KEY`
  in `.env`) — every device shares the same key; there's no per-device secret yet.
- These are written to `%ProgramData%\TimeTracker\agent-settings.json` by the
  installer (not machine environment variables — see below for why). To
  repoint an already-installed machine later (e.g. moving to a cloud server),
  edit that file directly and have the user log off/on, or just relaunch
  `TimeTracker.Agent.exe` from `C:\Program Files\TimeTracker Agent\`.

For a push deployment (GPO, SCCM, Intune), run the same `msiexec` command
per target machine with your real `SERVERURL`/`AGENTAPIKEY`.

## TLS (required — the server has no plaintext port)

The server listens on **HTTPS only** (`5443`). Agents send the shared API key on every sync and
admins send a session cookie, so a plaintext listener would expose both to anyone on the LAN.

**This is entirely internal.** HTTPS here does not mean exposing anything to the internet: no
public DNS, no inbound ports, no third-party CA. The certificate is issued by you, for a machine
only your LAN can reach. Its job is to stop someone on the same network reading the admin session
cookie and agent API key off the wire.

A public CA (Let's Encrypt) **cannot** be used, precisely because the server is private — they will
not validate an internal address. So issue the certificate yourself, one of three ways.

### Best, if the machines are domain-joined: AD Certificate Services

Issue a server certificate for the TimeTracker host from your enterprise CA. Domain members trust
the enterprise root automatically, so there is **no client-side work at all** — no GPO import, no
per-machine step. If you have AD CS, use it.

### No DNS at all — self-signed with an IP SAN

Use this to keep addressing the server by bare IP (`https://192.168.0.15:5443`).

The address must be in the certificate's **IP** SAN field. `-DnsName` will not do it: that writes a
*DNS* SAN, and .NET rejects a DNS SAN when connecting to a literal IP — the connection fails
validation even though the address visibly appears in the certificate. Use `-TextExtension` instead
(verified to produce `IP Address=192.168.0.15`):

```powershell
# On the server. Replace the IP with the server's LAN address.
$cert = New-SelfSignedCertificate `
  -Subject "CN=TimeTracker Server" `
  -TextExtension @("2.5.29.17={text}IPAddress=192.168.0.15&DNS=timetracker") `
  -CertStoreLocation "Cert:\LocalMachine\My" `
  -NotAfter (Get-Date).AddYears(3) `
  -KeyExportPolicy Exportable
```

If the server's IP ever changes, the certificate must be reissued — that is the tradeoff for
skipping DNS. A hosts entry (below) avoids it.

### With a name instead — self-signed with a DNS SAN

A hostname needs no DNS *server*: a `hosts` entry on each endpoint
(`192.168.0.15  timetracker`, pushable by GPO) is enough, and survives the server changing IP.
Swap the `-TextExtension` line above for `-DnsName "timetracker"`.

### Then, for either self-signed route

```powershell
$pw = ConvertTo-SecureString -String "<CERT_PASSWORD from .env>" -Force -AsPlainText
New-Item -ItemType Directory -Force .\certs | Out-Null
Export-PfxCertificate -Cert $cert -FilePath .\certs\server.pfx -Password $pw

# Public half only - this is what endpoints must trust. Contains no private key.
Export-Certificate -Cert $cert -FilePath .\timetracker-root.cer
```

Then set `CERT_PASSWORD` in `.env`, and make sure `certs/server.pfx` is readable by the container's
non-root user (`chmod 644 certs/server.pfx` — it is password-protected).

**Distribute `timetracker-root.cer` to every monitored machine**, into
`Local Computer\Trusted Root Certification Authorities`. Via GPO:
*Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies →
Trusted Root Certification Authorities → Import*. Without this, every agent's sync fails TLS
validation and the Test Connection shortcut reports a certificate error.

`certs/` and `*.pfx` are gitignored — never commit the private key.

### Pointing agents at HTTPS

`SERVERURL` must use `https`, port `5443`, and **exactly** the address in the certificate — the IP
if you used an IP SAN, the hostname if you used a DNS SAN. Mixing them (certificate issued for the
hostname, agents pointed at the IP) fails validation:

```
msiexec /i TimeTracker.Agent-Setup.msi /qn SERVERURL=https://192.168.0.15:5443 AGENTAPIKEY=...
```

Already-deployed agents can be repointed from the Start Menu's **TimeTracker Agent Settings**
entry (requires administrator approval) without reinstalling.

## Updating a machine that already has the Agent

Use the **same plain `/i`** command — do **not** add `REINSTALL=ALL REINSTALLMODE=amus`.

Every `wix build` generates a new ProductCode, so a freshly built MSI is a *different*
product that shares this one's UpgradeCode. Plain `/i` is therefore the correct and
intended path: `MajorUpgrade` installs the new product and `RemoveExistingProducts`
retires the old one.

`REINSTALL=ALL` means "reinstall the already-installed features **of this product**" —
and since the newly built ProductCode has never been installed, there is nothing to
reinstall. MSI then registers the product, runs the custom actions, and reports
**"Installation completed successfully" while copying zero files**, leaving the old
binaries in place. It's a genuinely silent failure; the only tell is `Action: Null` on
every component in an `/l*v` log. It also sets `REMOVE=ALL` (a reinstall is modelled as
removing and re-adding every feature), which flips the install/uninstall conditions in
`Product.wxs` the wrong way round.

To confirm an update actually landed, check `TimeTracker.Agent.dll` — **not** the `.exe`.
In a self-contained publish the `.exe` is just the native apphost stub and is
byte-identical across builds, so its hash/timestamp will match even when nothing was
updated. The managed code lives in the DLL.

## Troubleshooting

The install adds a **Test Connection** shortcut under the "TimeTracker Agent"
Start Menu folder. It re-reads the installed `agent-settings.json`, makes the
same authenticated request `SyncClient` makes (`GET /api/devices/{id}/policy`
with the `X-Agent-Key` header), and shows a pass/fail message box - success
means the current Server URL and API key both work, without needing to wait
for real activity to sync or dig through Event Viewer. Run it any time you're
unsure whether an Agent is actually reaching the server (e.g. after
repointing it, or after a server redeploy).

It can also be run directly, e.g. to test values before installing:
```
TimeTracker.Agent.exe --test-connection "http://your-server:5081|your-agent-api-key"
```

## Uninstall

```
msiexec /x TimeTracker.Agent-Setup.msi /qn
```

Or, if the installed product's ProductCode no longer matches this specific
`.msi` file (e.g. after rebuilding), find it first:
```powershell
Get-Package -Name "TimeTracker Agent" | Uninstall-Package
```

## Why a config file instead of environment variables

The first version of this installer set `Agent__ServerBaseUrl` /
`Agent__ApiKey` as machine environment variables via WiX's `<Environment>`
element. That doesn't work reliably: a process only inherits environment
variables that existed when its parent process (for the original
service-based design, the Service Control Manager) started — SCM snapshots
its environment at boot and doesn't refresh from a machine env var change
until a reboot, so a freshly-installed service/app couldn't see a variable
set during that same install. Writing a JSON file that's read by the Agent
directly at startup sidesteps this — no reboot required, and it's the same
mechanism used to update the config later.

## Known limitations

- No auto-restart if the Agent process crashes (a Startup-folder app doesn't
  get the "restart on failure" behavior a Windows Service or a properly
  configured Scheduled Task would). Acceptable for now; worth revisiting if
  crashes turn out to be common in practice.
- The API key is shared across all devices — a compromised Agent machine can
  impersonate any other device's sync traffic. Fine for a trusted internal
  network; would need per-device credentials before exposing the server
  publicly.
