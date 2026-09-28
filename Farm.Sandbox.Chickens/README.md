# Farm.Sandbox.Chickens

Farm.Sandbox.Chickens is a fixed-delay Generic Host that finds the current user's Azure DevOps pull-request feedback, prepares an isolated private Git workspace, asks Copilot to address unresolved external comments, validates the result, and optionally publishes it. It never resolves review threads itself. It exposes no HTTP port and is intended to run as one Compose replica sharing state and lock storage.

## Boundaries

- `Farm.Azure` reads current identity, pull requests, work-item context, feedback anchors, and iteration or commit context.
- `Farm.Git` treats the mounted repository as a read-only seed and owns the private clone, worktree, rebase, validation, commit, and exact force-with-lease push.
- `Farm.Copilot` runs the Copilot SDK in the private worktree. Immutable safety purpose is always prepended; mounted prompts and agents are supplemental.
- `Farm.State.Sqlite` stores renewable leases and successful-completion suppression keyed by organization, project, repository, PR, head SHA, PR-thread feedback fingerprint, and a versioned (`wi-v1:`) work item fingerprint, plus per-work-item content hashes for change detection.
- `Farm.Console` and `sam` are not invoked.

A candidate is an active PR authored by the authenticated user with unresolved external comments whose newest external comment is older than the quiet period. Ineligible candidates are deferred and remain retryable.

A completed PR is picked up again when its head SHA changes, its unresolved external PR thread comments change, or the traversed work item set changes: an item enters or leaves the set, its title, description, repro steps, or acceptance criteria change, or an external comment is added or edited. State, assignee, iteration, tag, and board changes do not trigger a re-review, and comments by the authenticated user are ignored. Work item content is hashed after redaction. External work item comments and edits to changed items also count toward the quiet period. When a related item or its comments cannot be read, its previous hash is kept (and on such degraded fetches, items missing from the set keep theirs), so flapping access does not retrigger reviews. On first run after upgrade, a PR already completed under the PR-thread-only key adopts the current work item state as its baseline (`attempt_baseline_adopted`) instead of being re-reviewed.

## Environment

Required application variables:

- `FARM_AZURE_DEVOPS_ORGANIZATION_URL`, `FARM_AZURE_DEVOPS_PROJECT`, `FARM_AZURE_DEVOPS_PAT`
- `FARM_CHICKENS_REPOSITORY_PATH`, `FARM_CHICKENS_CACHE_PATH`, `FARM_CHICKENS_WORKTREES_PATH`, `FARM_CHICKENS_ARTIFACTS_PATH`
- `FARM_CHICKENS_GIT_USER_NAME`, `FARM_CHICKENS_GIT_USER_EMAIL`, `FARM_CHICKENS_BASE_BRANCH`

Optional variables include `FARM_CHICKENS_STATE_CONTAINER_PATH`, `FARM_CHICKENS_STATE_PATH`, `FARM_CHICKENS_STATUS_PATH`, `FARM_CHICKENS_LOCK_PATH`, `FARM_CHICKENS_SCHEDULE_SECONDS`, `FARM_CHICKENS_QUIET_PERIOD_SECONDS`, `FARM_CHICKENS_LEASE_SECONDS`, `FARM_CHICKENS_RUN_IMMEDIATELY`, `FARM_CHICKENS_ENABLE_PUSH`, `FARM_CHICKENS_GIT_AUTH_TOKEN`, `FARM_CHICKENS_GIT_AUTH_USER`, `FARM_CHICKENS_VALIDATION_COMMANDS_JSON`, `FARM_CHICKENS_SAFE_PROCESS_ENV_JSON`, `FARM_CHICKENS_COPILOT_MODEL`, `FARM_CHICKENS_COPILOT_RESOURCES_PATH`, `FARM_CHICKENS_COPILOT_PROMPT_PATH`, `FARM_CHICKENS_COPILOT_AGENT`, `FARM_CHICKENS_WORK_ITEM_MAX_DEPTH` (default `3`, `0` disables related-item traversal), and `FARM_CHICKENS_WORK_ITEM_MAX_COUNT` (default `50`).

Review context starts from the PR-linked work items and follows parent/child, related, and dependency links breadth-first up to the configured depth and total item cap. Each item carries its type, state, description, repro steps, acceptance criteria, recent comments, depth, and relation path back to the PR. Related items or comments that cannot be read are skipped and logged; failures on PR-linked items still fail the attempt.

Push defaults to disabled. Direct runs must opt in with `FARM_CHICKENS_ENABLE_PUSH=true`; the canonical Compose file requires an explicit value. When no dedicated Git token is set, Azure PAT credentials are used for Git operations.

## Compose and build

The canonical definition is `deployment/farm-sandbox-chickens.compose.yml`. It publishes no ports. Set the host paths for the read-only repository seed and agent overrides plus writable artifacts, state, cache, and worktrees. The state host mount persists `chickens.db`, the lock, and the heartbeat under `FARM_CHICKENS_STATE_CONTAINER_PATH` (default `/workspace/state`). The state, status, and lock paths default beneath that root; custom values must remain beneath the mounted state root and writable by UID/GID `1654`.

The container runs as UID/GID `1654:1654`. Writable host directories must be owned or writable by that identity. The repository seed and agent override mounts are read-only.

```bash
docker compose -f deployment/farm-sandbox-chickens.compose.yml config --quiet
docker compose -f deployment/farm-sandbox-chickens.compose.yml logs -f farm-sandbox-chickens
```

Build from Windows with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-FarmSandboxChickensImage.ps1
```

The script restores and validates first, publishes `linux-x64` into `artifacts/farm-sandbox-chickens/linux-x64/`, then runs Docker build. It uses a unique staging directory and atomically promotes the publish output only after success. `-SkipDockerBuild` stops after publish; `-ImageTag <name:tag>` selects the image tag. Docker copies the publish directory and does not restore, build, test, or publish the application. The image uses the .NET SDK because configured validation can run `dotnet build` and `dotnet test`.

Run locally with `dotnet run --project Farm.Sandbox.Chickens`.

## Git and Copilot behavior

The mounted repository is never mutated. The service clones or reuses a private writable cache, fetches the target base from `origin`, fetches the PR source through `chickens-source` including forks, and creates an isolated PR/head worktree. It rebases with `--autostash --committer-date-is-author-date`. A conflict stays in the private worktree for Copilot to resolve. A missing or unwritable source is deferred. Publication uses a deterministic commit and `--force-with-lease=<source-ref>:<original-head-sha>`; a moved branch or rejected push remains failed and retryable.

The immutable Copilot purpose requires conflict resolution first, focused feedback changes or a reviewer explanation, and forbids resolving threads, committing, or pushing. Mounted prompts, resources, and a selected mounted agent can add guidance but cannot replace that purpose. Permissions are deny-by-default and confined to the worktree; shell commands are limited to approved `dotnet` and read-only or explicitly controlled `git` operations.

## Safety and artifacts

Credentials are available to the broker process but are not passed to validation or Copilot children. Child processes receive a strict non-secret environment allowlist. Never publish `docker inspect`, interpolated `docker compose config`, `env`, `printenv`, shell tracing, prompts, or tokens.

Context, transcript, patch, validation, explanation, publication, result, and exception content is redacted and scanned before persistence. Secret, token, JWT, private-key, password, connection-string, credentialed-URL, and unsafe artifact paths are rejected. Attempts are stored under `<artifacts>/<repository>/pr-<id>/<fingerprint>/`.

Structured Docker logs use stable event names including `startup_configuration_summary`, `cycle_started`, `cycle_completed`, `cycle_failed`, `candidate_discovered`, `candidate_deferred`, `candidate_skipped`, `candidate_failed`, `feedback_changed_work_items`, `attempt_baseline_adopted`, `lease_acquired`, `lease_released`, `lease_renewed`, `context_fetched`, `context_work_item_skipped`, `cache_repository_reset`, `rebase_completed`, `rebase_conflict`, `copilot_started`, `copilot_completed`, `validation_completed`, `push_completed`, and `push_rejected`. Fields contain identifiers, counts, durations, paths, and redacted summaries, never credentials or prompts.

The redacted status JSON is atomically replaced and contains service status, paths, schedule, push-enabled state, last-updated and heartbeat timestamps, cycle timestamps, last success/failure, current PR/attempt, counts, and an error summary. Graceful cancellation records `stopping` and `stopped`; startup marks an old `running` snapshot as degraded/stale before proceeding. A hard kill can leave the last status stale because perfect crash detection requires an external supervisor; consumers should evaluate the heartbeat timestamp.

## Troubleshooting

- Validate Compose with `docker compose -f deployment/farm-sandbox-chickens.compose.yml config --quiet` without printing resolved secrets.
- Follow normal service output with `docker compose -f deployment/farm-sandbox-chickens.compose.yml logs -f farm-sandbox-chickens`.
- If the container cannot write state, artifacts, cache, worktrees, or status, fix host ownership for UID/GID `1654:1654`.
- If the image build cannot find publish output, run `Build-FarmSandboxChickensImage.ps1` first; Docker does not compile the project.
- If a candidate is deferred, inspect the redacted status JSON and logs for quiet-period, lease, source-access, or push-permission reasons.
- If a rebase conflict or validation failure occurs, the attempt remains retryable and the private worktree is cleaned after the attempt.

Focused validation:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Invoke-FarmValidation.ps1 -Scope Chickens
```
