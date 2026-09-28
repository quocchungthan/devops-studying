# Farm.Sandbox.Cows

Farm.Sandbox.Cows is a fixed-delay Generic Host (every 2 hours by default) that keeps local investigation notes in sync with Azure DevOps. Cloud data (Azure DevOps, Miro) is the source of truth; local notes are a cache that is kept when nothing changed and regenerated otherwise. It exposes no HTTP port and never writes to the tracker or design boards.

## Sync cycle

1. Scope = work items assigned to me (the PAT owner) that are not in `FARM_AZURE_DEVOPS_TERMINAL_STATES`, using the same `[System.AssignedTo] = @Me` WIQL as `sam work-items assigned-to me` plus the terminal-state filter, + their one-hop related work items (parent, child, related, predecessor, successor), deduplicated and capped at `FARM_COWS_MAX_RELATED_ITEMS`. IDs already tracked in state or present as `<notes>/<id>/README.md` but outside the scope are only visited so they can be marked out of scope.
2. Each in-scope item is fetched with the PAT client. The `cow-v2:` fingerprint hashes revision, changed date, state, relations, every linked PR's status, source commit, and close date (a 404/403 PR is recorded as `unavailable`), and the item's comments (id, modified date, text hash).
3. Decision per item:
   - fingerprint unchanged, note hash matches what Cows wrote, and the last successful patch is younger than `FARM_COWS_MAX_NOTE_AGE_HOURS` → **keep** (no Copilot call);
   - new, cloud changed (including comment edits), note missing, note edited locally, or note older than `FARM_COWS_MAX_NOTE_AGE_HOURS` (default 24, `0` disables; refreshes sources without a cheap fingerprint such as Miro) → **patch** (Copilot regenerates; cloud overwrites local edits);
   - deleted or inaccessible (404/403 only; any other error fails the cycle), or `Removed` state → **mark removed** in state; the note is never deleted;
   - no longer assigned to me or related to an assigned item → **mark out of scope** (recorded as removed); the note is kept and not regenerated. When the item comes back into scope the marker is cleared and the normal checks above apply (kept if nothing changed);
   - a linked PR hit a transient error while a note already exists (tracked or hand-written) → **defer** (no rewrite from a degraded snapshot).
4. A patch runs Copilot with the bundled `resources/SKILL.md` (feature-investigation) and `resources/mcp.json`. Copilot may only read the repository mount and call allowlisted read-only MCP tools; it returns the note, and Cows redacts, scans, and writes it atomically (temp + rename) under the notes root. At most `FARM_COWS_MAX_PATCHES_PER_RUN` patch attempts (successful or failed) run per cycle; the rest are deferred.
5. Fingerprints and note hashes live in `cows.db` (`Farm.State.Sqlite`). A failed patch leaves state untouched and retries next cycle. The redacted `status.json` reports target, kept, patched, removed, deferred, and failed counts.

## MCP auth and tools

- Azure DevOps MCP (`mcp.dev.azure.com`): the URL in `mcp.json` keeps the `{organization}` placeholder, filled at load time from the organization segment of `FARM_AZURE_DEVOPS_ORGANIZATION_URL` (`https://dev.azure.com/<org>` or `https://<org>.visualstudio.com`). `Authorization: Basic <PAT>` is sent only when `FARM_COWS_ADO_MCP_USE_PAT=true` (opt-in, default `false`); otherwise the server is omitted and the runner snapshot supplies tracker data. `X-MCP-Readonly: true` is always sent.
- Miro MCP (`mcp.miro.com`): `Authorization: Bearer $FARM_COWS_MIRO_ACCESS_TOKEN`. Without a token the server is omitted and the note records the design source as unavailable.
- Each server gets an explicit read-only tool allowlist (see `CopilotInvestigationPolicy`); only those tools are exposed and approved, regardless of the server's `readOnly` hint. Servers on any other host, stdio, or plain-http servers are skipped.

## Environment

Shared (root `.env`): `FARM_AZURE_DEVOPS_ORGANIZATION_URL`, `FARM_AZURE_DEVOPS_PROJECT`, `FARM_AZURE_DEVOPS_PAT`, optional `FARM_AZURE_DEVOPS_TERMINAL_STATES`, and `FARM_CHICKENS_CACHE_HOST_PATH` / `FARM_CHICKENS_REPOSITORY_HOST_PATH` as fallbacks.

Cows-specific, all optional: `FARM_COWS_SCHEDULE_SECONDS` (7200), `FARM_COWS_RUN_IMMEDIATELY` (true), `FARM_COWS_MAX_PATCHES_PER_RUN` (20), `FARM_COWS_MAX_RELATED_ITEMS` (50, `0` disables related items), `FARM_COWS_MAX_NOTE_AGE_HOURS` (24, `0` disables age-based refresh), `FARM_COWS_COPILOT_MODEL` (auto), `FARM_COWS_ADO_MCP_USE_PAT` (false), `FARM_COWS_MIRO_ACCESS_TOKEN`, `FARM_COWS_SAFE_PROCESS_ENV_JSON` ({}), `FARM_COWS_TIMEZONE`, `FARM_COWS_MEMORY_LIMIT` (2g). Host paths: `FARM_COWS_DATA_HOST_PATH` (default `artifacts/farm-sandbox-cows-data`) or individually `FARM_COWS_NOTES_HOST_PATH`, `FARM_COWS_STATE_HOST_PATH`, `FARM_COWS_CACHE_HOST_PATH`, `FARM_COWS_REPOSITORY_HOST_PATH`, `FARM_COWS_GH_HOSTS_FILE`. In-container paths: `FARM_COWS_NOTES_PATH`, `FARM_COWS_REPOSITORY_PATH`, `FARM_COWS_RESOURCES_PATH`, `FARM_COWS_STATE_CONTAINER_PATH`, `FARM_COWS_STATE_PATH`, `FARM_COWS_STATUS_PATH`, `FARM_COWS_LOCK_PATH` (state paths must stay beneath the state root).

## GitHub auth

Cows reuses the gh login the Chickens containers use: `${FARM_CHICKENS_CACHE_HOST_PATH}/home/.config/gh/hosts.yml` (created by `deployment/farm-sandbox-chickens-authtest.compose.yml`) is bind-mounted read-only at `/workspace/cache/home/.config/gh/hosts.yml`. The token is not copied; Compose fails if the file is missing. On Linux hosts pre-create `<cows cache>/home/.config/gh` owned by `1654:1654` so Docker does not create it as root.

## Build and run

Pull the published image, or build locally (the script also pre-creates the default `artifacts/farm-sandbox-cows-data/{notes,state,cache}` host paths; Compose binds them with `create_host_path: false`, so create any custom `FARM_COWS_*_HOST_PATH` yourself, owned by `1654:1654` on Linux):

```powershell
docker pull ghcr.io/quocchungthan/farm-sandbox-cows:latest
powershell -ExecutionPolicy Bypass -File scripts/Build-FarmSandboxCowsImage.ps1
docker compose -f deployment/farm-sandbox-cows.compose.yml --env-file .env config --quiet
docker compose -f deployment/farm-sandbox-cows.compose.yml --env-file .env up -d
docker compose -f deployment/farm-sandbox-cows.compose.yml --env-file .env logs -f farm-sandbox-cows
```

The container runs as UID/GID `1654:1654` with a read-only root filesystem; the repository and hosts.yml mounts are read-only, notes/state/cache are writable. The Copilot runtime home (`$HOME/.copilot`, where it writes session-state events and logs) is a tmpfs, so prompts and session data never reach the host. Never publish interpolated `docker compose config` output.
