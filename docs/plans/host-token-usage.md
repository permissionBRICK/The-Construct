# Token usage on the host: per VM, per user, per host

Status: plan, 2026-09-17. Branch `feat/host-token-usage` (off `main`). Implementation is handed
to a T3 Code thread; this document is the brief.

## 1. Goal

The host admin panel shows agent token usage and estimated cost per VM, per user and for the
whole host, for today, this month and all time, without anyone opening each VM's control
panel. Guests report their usage to the host on a timer through the same token-authenticated
channel as the idle heartbeat; the host stores daily totals and aggregates them. Both host
platforms, no Proxmox-specific code.

Decisions already taken: **push from the guest, not pull.** The host holds no credentials into
guests, the collector (`ccusage`) already runs inside the VM for the control panel and can
take minutes on first run, and the heartbeat channel exists. A "collect now" button on the
host is therefore not possible; the panel shows when each VM last reported, and the interval
is short enough (default 15 min) that a button would add little. Daily granularity per agent
tool is the unit of storage: re-reports overwrite the same day, so nothing double-counts.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Guest collector: `ccusage <claude|codex|opencode> daily|monthly --json`, bootstrap, output `{generatedAt, vmHost, report, window, tools:{…}}` | `extension/vm/usage.sh` (templated via `extension/src/guest-scripts.js`) |
| What the control panel keeps of it (only `totals.totalTokens` and `totals.totalCost`/`costUSD`), caching, export | `extension/src/usage.js`, `extension/media/panel.{html,js}` (~line 179-196, 1054-1071), companion mirror `companion/src/Construct.Companion.Core/State/UsageParser.cs`, fixtures `test/fixtures/companion-parity/usage-*.json`, tests `extension/test/usage.test.js` |
| Guest → host heartbeat: timer unit, config lookup, VM token via header file, `POST /vms/{name}/activity` | `bin/construct-idle-report.sh`, `systemd/construct-idle-report.{service,timer}`, `bin/provision.sh` (~line 1032, 1092-1100), `service/src/Constructd.Api/Endpoints/IdleEndpoints.cs` (`PostActivityAsync`, `Policies.VmScoped`, `Audited(..., auditSuccess:false)`), `Auth/TokenAuthenticationHandlers.cs` (VM token) |
| Persistence patterns: migrations (`ISqliteMigration`, highest id 820), retention precedents (`SqliteOperationKeyStore`, source cache LRU, `MediaCleanupService` 24 h timer) | `service/src/Constructd.Sqlite/Migrations/`, `SqliteDatabase.cs`, `Api/Hosting/MediaCleanupService.cs` |
| Host admin panel: tabs and feature gating, VM rows and the live usage column, Users tab rows | `extension/src/hostadmin.js` (`FEATURE_NAMES`, tabs ~line 40-50, `toVmRow`, `toUserRow`), `extension/media/hostadmin.{html,js}`, `service/src/Constructd.Api/Endpoints/HostAdminEndpoints.cs`, `VmInventoryProjection.cs`, `Contracts/Responses.cs`, companion `HostAdminViews*.cs` + parity fixtures (`node extension/test/export-parity-fixtures.js` regenerates) |
| Docs | `docs/control-panel.md` (~line 430-445, the ccusage section), `docs/expose.md` (activity heartbeat section, VM token scope), `service/README.md` |

## 3. Design

### 3.1 Guest reporter

`bin/construct-usage-report.sh` + `systemd/construct-usage-report.{service,timer}`, installed
by `bin/provision.sh` next to the idle timer, only when `CONSTRUCT_SERVICE_URL` is set. Every
`CONSTRUCT_USAGE_REPORT_INTERVAL_MIN` (default 15, min 5) it runs the existing collector logic
(`ccusage <tool> daily --json --since <today minus 2 days>`) for the three tools, and posts:

```
POST /api/v1/vms/{instance}/usage        Authorization: VmToken <secret> (header file, never argv)
{ "generatedAt": "...", "days": [
    { "day": "2026-09-17", "tool": "claude", "inputTokens": n, "outputTokens": n,
      "cacheCreateTokens": n, "cacheReadTokens": n, "totalTokens": n, "costUsd": 1.23,
      "models": { "claude-opus-5": { "totalTokens": n, "costUsd": x }, ... } }, ... ] }
```

Two days back covers a VM that was off at midnight or a clock that drifted. On the first run
after provisioning (or when `CONSTRUCT_USAGE_BACKFILL=1`) it sends `monthly` for the current
and previous month too, so a host that starts collecting mid-month is not blind to earlier
days: those arrive as month rows (`"day": "2026-09"`), stored with the same key shape. Failures
are logged to the journal and retried next tick; ccusage bootstrap follows `usage.sh`.
`CONSTRUCT_USAGE_REPORT_ENABLED=false` in `config.env` turns it off per VM. Reuse the
config-lookup and header-file patterns from the idle reporter verbatim; share the ccusage
invocation with `usage.sh` rather than copying it (extract a `bin/lib/usage-collect.sh` both
source, or have `usage.sh` call the new script).

### 3.2 Host storage and API

- Migration `M830_TokenUsage`: table `token_usage(vm_name, owner, tool, day, input_tokens,
  output_tokens, cache_create_tokens, cache_read_tokens, total_tokens, cost_usd_micros
  INTEGER, models_json, reported_at, PRIMARY KEY(vm_name COLLATE NOCASE, tool, day))`. Cost in
  micro-dollars as an integer. `owner` is the VM's owner at report time; rows survive VM
  deletion (usage belongs to the user), a deleted VM's rows keep the name and get
  `vm_deleted_at` on delete.
- `POST /vms/{name}/usage`: `Policies.VmScoped`, resolve the VM like the activity route, cap
  at 200 day rows and 64 models per row, validate numbers non-negative, day as `YYYY-MM-DD`
  or `YYYY-MM`, tool in the known set; upsert; 204; `Audited("vm.usage", auditSuccess:false)`.
- Aggregation service (pure `TokenUsageMath` + a store): windows `today`, `month`, `all`;
  group by VM, by user, by tool; month rows (`YYYY-MM`) count for that month only when no day
  rows exist for that VM/tool/month (backfill precedence rule; test it).
- Routes: `GET /host/usage?window=today|month|all` (admin: everything; user: own VMs only,
  same `Policies.User` + owner filter as the VM list) returning `{ window, generatedAt,
  totals:{tokens,costUsd}, byUser:[{user,tokens,costUsd,vms}], byVm:[{vm,user,deleted,tokens,
  costUsd,lastReportedAt,tools:[…]}] }`; `GET /vms/{name}/usage?window=` for one VM
  (owner/admin).
- Retention: `usage.retentionDays` in host config (default 400), pruned by a daily
  `BackgroundService` like `MediaCleanupService`.
- Feature flag `usage` in `ReleaseInfo.ApiFeatures` on both platforms.

### 3.3 Panel

- New host admin tab **Usage** (feature `usage`): window selector (today / month / all),
  totals line, a by-user table (admin) and a by-VM table (admin: all; user: own), each row with
  tokens, estimated cost, last report time, per-tool split in a tooltip or expandable row.
  Deleted VMs are shown greyed with their name. "Cost is an estimate from the collector's
  price table" note, as in the control panel.
- VM row in the VMs tab gets a small "tokens today / month" figure next to live usage.
- Users tab row gets tokens this month.
- Companion parity: the same views in `HostAdminViews*.cs`; regenerate fixtures.

### 3.4 Docs

`docs/control-panel.md`: the host-side view and the reporter; `docs/expose.md`: the new
guest → host route and the VM token scope now covering usage; `service/README.md`: config
rows, routes, retention; `docs/proxmox-host.md` and `docs/remote-host.md`: one paragraph each.

## 4. Work, in order

1. Migration, store, `TokenUsageMath` with tests (upsert idempotence, backfill precedence,
   windows, per-user grouping across deleted VMs, retention).
2. Route + validation + tests (`Policies.VmScoped`, caps, bad input, deleting-VM fence like the
   activity route, secret hygiene).
3. Guest reporter script + units + provisioning + a bash test in `test/` mirroring
   `test/idle-report.test.sh` (stubbed `ccusage` and `curl`, header-file token, no token in
   argv, disabled flag, backfill).
4. Aggregation routes + feature flag + panel tab, VM and user row figures; extension tests and
   UI smoke; companion views + parity fixtures.
5. Docs.

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
(UI smoke needs `NODE_PATH=/root/repos/omniloop/node_modules`), the companion tests and the
extension tests green.

## 5. Acceptance

- A guest posting two days of usage twice shows the same totals on the host both times.
- The Usage tab shows per-user and per-VM totals for today, this month and all time; a user
  sees only their VMs; a deleted VM's usage still counts for its former owner.
- Rows older than the retention window disappear on the next prune.
- Field test (a human): a Proxmox-hosted and a Hyper-V-hosted VM both report within one
  interval of provisioning and the panel agrees with the VM's own control panel figures.

## 6. Rules for the implementing agent

- Work on `feat/host-token-usage` in this worktree (`/root/repos/construct-usage`), off
  `main`. Commit in small steps with the repo's configured author; never put any other email
  in commits or files.
- Do not touch `/root/repos/construct` or `/root/repos/construct-console` (another thread).
  Do not run anything against a real host; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the
  deviation in the final report.
