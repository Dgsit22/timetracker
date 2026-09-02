# TimeTracker Agent — Windows installer

Builds an MSI that installs the Agent per-machine and launches it per-user from
the all-users Startup folder (`%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp`).

It's deliberately **not** a Windows Service: a service runs in Session 0, which
has no interactive desktop, so foreground-window detection, idle detection,
and screen capture all silently return nothing there. Running from Startup
gives every user who logs into the machine their own Agent instance with
normal desktop access.

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
wix build Product.wxs -arch x64 -d PublishDir="../publish/agent-win-x64" -o TimeTracker.Agent-Setup.msi
```

`-arch x64` is required — without it WiX defaults to x86 and `ProgramFiles64Folder`
resolves to the wrong (`Program Files (x86)`) location.

## Install (per machine)

```
msiexec /i TimeTracker.Agent-Setup.msi /qn SERVERURL=http://your-server:5081 AGENTAPIKEY=your-agent-api-key
```

- `SERVERURL` defaults to `http://localhost:5081` if omitted.
- `AGENTAPIKEY` must match the server's `Agent:ApiKey` (Docker: the `AGENT_API_KEY`
  in `.env`) — every device shares the same key; there's no per-device secret yet.
- These are written to `%ProgramData%\TimeTracker\agent-settings.json` by the
  installer (not machine environment variables — see below for why). To
  repoint an already-installed machine later (e.g. moving to a cloud server),
  edit that file directly and have the user log off/on, or just relaunch
  `TimeTracker.Agent.exe` from `C:\Program Files\TimeTracker Agent\`.

For a push deployment (GPO, SCCM, Intune), run the same `msiexec` command
per target machine with your real `SERVERURL`/`AGENTAPIKEY`.

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
