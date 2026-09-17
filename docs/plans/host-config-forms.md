# Host admin: real configuration forms, and no settings on the Overview

Status: plan, 2026-09-18. Branch `feat/host-config-forms` (off `main`). Implementation is handed
to a T3 Code thread; this document is the brief.

## 1. Goal

Two complaints from the first day of use on the Proxmox host:

1. **The Overview tab carries a settings card** (the "Network" card with the default VM
   network mode and the owner-may-switch toggle, added by the direct-network work). The
   Overview is a status page; settings belong on the Configuration tab. Move it.
2. **The Configuration tab is a list of raw JSON text boxes**, one per section. Replace them
   with proper forms: labelled fields with the right input type, units, help text, defaults,
   inline validation that mirrors the server's rules, and a Save per section that keeps the
   optimistic-concurrency stamp. Keep a collapsed "Raw JSON" editor per section for the
   rare key the form does not know yet, so nothing becomes impossible.

Same forms in the Companion (its host administration views mirror the extension), same
parity fixtures.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Config sections and their records (`CapacityConfig`, `MemoryPressureConfig`, `UserDefaultsConfig`, `UserCapsConfig`, `LifecycleConfig`, `MediaConfig`, `NetworkConfig`, `VirtualizationConfig`, `UpdatesConfig`), defaults, server-side validation messages | `service/src/Constructd.Core/Domain/HostConfig.cs`, `Configuration/HostAdminDefaults.cs`, `Api/Infrastructure/HostConfigValidation.cs` |
| Config routes (`GET/PUT /host/config`, section rows with `updatedAt`/`expectedUpdatedAt`, coded problems) and capabilities (`GET /host/capabilities`, platform gating such as direct mode on Proxmox only, nested availability) | `service/src/Constructd.Api/Endpoints/HostAdminEndpoints.cs` |
| Extension model: `CONFIG_SECTIONS`, `toConfigView`, `buildConfigRequest`, `saveConfig`, the Overview's `networkSection`, feature flags | `extension/src/hostadmin.js` |
| Extension renderer: the config tab textareas, the Overview Network card (`hostNetworkCard`, `hostNetworkMode`, `hostNetworkOwner`, `hostNetworkSave`), CSS | `extension/media/hostadmin.{html,js,css}` |
| Tests: model tests, UI smoke (Playwright; `NODE_PATH=/root/repos/omniloop/node_modules`), parity fixtures (`node extension/test/export-parity-fixtures.js`) | `extension/test/hostadmin.test.js`, `hostadmin-ui.test.js`, `ui-smoke.js`, `parity.test.js`, `test/fixtures/companion-parity/*` |
| Companion views and dispatch for the config tab and the network card | `companion/src/Construct.Companion.Core/HostAdmin/HostAdminViews*.cs`, `HostAdminProtocol.cs`, `companion/src/Construct.Companion.Host/Dispatch/HostAdministration*.cs` |
| Docs describing the config tab | `docs/control-panel.md`, `docs/field-test-host-admin.md`, `service/README.md` (config section table with keys, defaults and meaning: use it as the source for labels and help text) |

## 3. Design

### 3.1 Overview

Remove the Network card from the Overview (HTML, renderer, model state `networkSection`, the
Companion equivalent, the smoke checks that target it). The Overview keeps its read-only
lines; if a status line for the network default is wanted, it is text, not a control.

### 3.2 Configuration tab

- One **form schema** in the extension model (`extension/src/hostadmin-config-schema.js`, pure
  data, shared with the Companion through a parity fixture): per section, an ordered list of
  fields `{ key, label, type: bool|int|bytes|seconds|minutes|percent|enum|string|list, unit,
  min, max, options, help, platform?: "proxmox"|"hyperv", nullable? }`. Bytes fields render
  in GiB/MiB with the raw byte value stored; `null` means "no limit" where the record allows
  it (checkbox "no limit" next to the number).
- Sections and the fields to cover (names as in the records): capacity (mode, RAM headroom,
  storage headroom, CPU budget, max vCPUs per VM, reconcile seconds, orphan reservation
  timeout), memoryPressure (enabled, high/low water, swap high water, min seconds between
  saves, cooldown), userDefaults and userCaps (max primaries, allow child creation, retained
  children, CPU/RAM/storage budgets, max child lifetime, allow never, allow sharing),
  lifecycle (graceful timeout, lease tick, lease retry), media (max bytes, items per user,
  chunk bytes, upload TTL, acquire timeout, allow HTTP, unreferenced TTL), network (host
  forwards, direct address reporting, default mode, owner may switch mode; the mode fields
  only on Proxmox), virtualization (nested default, nested selectable; nested default
  disabled with a note when the host cannot nest), updates (whatever `UpdatesConfig` holds).
- **Validation** in the form mirrors `HostConfigValidation` (ranges, low < high, platform
  refusals) so the user sees the problem before saving; the server's coded problems still
  render inline as today.
- **Save** per section, posting only changed sections with their `expectedUpdatedAt` exactly
  as `buildConfigRequest` does now; a conflict shows the server's message and reloads.
- **Raw JSON** stays available per section behind a "Show raw JSON" toggle; editing raw and
  form at once is prevented (raw edits replace the form values on blur).
- Companion: the same schema drives `HostAdminViews.Config` and the dispatch; regenerate the
  parity fixtures.

### 3.3 Docs

`docs/control-panel.md` and `docs/field-test-host-admin.md` describe the forms; the
`service/README.md` table stays the reference for keys.

## 4. Work, in order

1. Remove the Overview Network card everywhere; tests and smoke updated.
2. Schema module + unit tests (every section key of every record is covered by a field or
   explicitly listed as raw-only; a test asserts the schema matches the C# records' property
   names via the parity fixture).
3. Form renderer and model (`toConfigView` produces field views, `buildConfigRequest` builds
   the sections from field values), validation, raw toggle; UI smoke covers one section
   save, one validation error, one conflict.
4. Companion views + fixtures.
5. Docs.

Each step leaves `bash test/run-local-checks.sh` (with `NODE_PATH=/root/repos/omniloop/node_modules`
for the smoke test), the companion tests, the parity fixtures and `dotnet test` green.

## 5. Rules for the implementing agent

- Work on `feat/host-config-forms` in this worktree (`/root/repos/construct-config`). Commit
  in small steps with the repo's configured author; never put any other email in commits or
  files.
- Do not touch `/root/repos/construct`. The human is rewriting `main`'s history in parallel:
  expect to rebase this branch onto the rewritten `main` before it merges; keep commits small
  and self-contained so that is painless.
- Do not run anything against a real host; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the
  deviation in the final report.
