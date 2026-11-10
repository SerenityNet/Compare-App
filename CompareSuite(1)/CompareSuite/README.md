# CompareSuite

Compare a preset set of files and folders across 2 or more servers (text and binary).

```
Browser -> Compare.Web (Blazor + Radzen, :5100) -> Compare.Gateway (:5200, YARP inside) -> Compare.Agent on each server (:5300)
```

| Project | Role |
|---|---|
| Compare.Contracts | Shared DTOs and API-key middleware |
| Compare.Agent | Runs on every server. `POST /manifest` (hashes only), `GET /file` (one file, on demand) |
| Compare.Gateway | Holds servers and comparison sets, queries agents in parallel, pivots results, builds diffs, and hosts a YARP reverse proxy to the agents |
| Compare.Web | Blazor Web App (.NET 10, interactive server) built with Radzen.Blazor components |

## Configure

1. **Agent** (`appsettings.json` on each server): set `Agent:ApiKey` and `Agent:Roots`
   (alias -> physical folder). Roots can differ per server; the alias is what is compared.
2. **Gateway**: set `Gateway:ApiKey`, list each server under `Servers` (agent URL and key),
   and define `Sets`. A watched path is `alias` or `alias/sub/path`, a file or a folder.
   `Include`/`Exclude` are wildcards matched against file names. Server ids should be letters,
   digits, `-` or `_`. `Servers` and `Sets` are read at startup, so changes need a gateway restart.
   Each server also needs a route and a cluster under `ReverseProxy` (see below).
3. **Web**: set `Gateway:Url` and `Gateway:ApiKey`.

Use long random keys and HTTPS between tiers in production.

## Run

```
dotnet run --project src/Compare.Agent      # on each server
dotnet run --project src/Compare.Gateway
dotnet run --project src/Compare.Web        # http://localhost:5100
```

Agent and Gateway call `UseWindowsService()` and `UseSystemd()`, so they also run as a Windows
service (`sc create`) or a systemd unit after `dotnet publish`.

## Files and folders

- A watched path can be a file or a folder. For a folder, the agent reports every subfolder
  (empty ones too) as well as every file. Set `"CheckFolders": false` on a watched path to compare
  files only.
- Files are compared by SHA-256; folders by presence. A path that is a file on one server and a
  folder on another is flagged.
- The results table is a collapsible tree. Each folder shows how many items inside need attention.

## Comparing many servers

- Pick any 2 or more servers from a set. Unreachable servers are shown and left out of the result.
- **All servers (majority):** the most common hash is treated as correct; disagreeing badges are ringed.
- **Compare against one server:** choose a baseline in the page. Every other server is judged
  against it, and files or folders it lacks or has extra are flagged. No re-run is needed to switch.

## Reverse proxy (YARP, inside the gateway)

All YARP configuration is in the gateway's `appsettings.json` under `ReverseProxy`. For each agent there
is a route and a cluster, both named `agent-<serverId>`:

- **Route:** matches `/agents/<serverId>/{**rest}`, removes that prefix, and sets the agent's own
  `X-Api-Key` header, so callers only need the gateway key. Example: `GET /agents/srv1/health`.
- **Cluster:** the agent's address plus an active health check on its `/health` every 15 seconds
  (3 consecutive failures mark it unhealthy) and a 2-minute activity timeout.

To add a server, add it under `Gateway:Servers` and copy one route and one cluster under `ReverseProxy`,
changing the id, address and key. The agent address and key appear in both places; keep them in step.
YARP reloads its config when `appsettings.json` changes, but `Servers` still needs a restart.

`GET /health/agents` (gateway key required) shows `Healthy`, `Unhealthy` or `Unknown` per agent, using the
`agent-<serverId>` cluster names. The gateway's own API (`/sets`, `/servers`, `/runs`, `/diff`) is handled
before the proxy routes, and manifests and diffs still go through `AgentClient`, which calls agents directly.

## Notes

- The UI uses Radzen.Blazor (`RadzenDataGrid`, `RadzenDropDown`, `RadzenCheckBoxList`, `RadzenCard`,
  `RadzenBadge`, `RadzenAppearanceToggle`, and more). `Compare.Web.csproj` references `Radzen.Blazor` as
  `8.*`; pin it to the exact version you restore.
- Results are classified per file: identical, differs, or missing on at least one server.
- The agent caches hashes by path, size and modified time, so repeat runs are fast.
- Runs are kept in memory (latest 50). The Web page polls once a second while a run is active.
- Diffs are limited to files and servers that took part in a run, and agents only serve files under
  their configured roots.
