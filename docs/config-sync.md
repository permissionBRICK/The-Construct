# Project config sync — design spec (v2)

> **Status: implemented.** This document is the design record for config sync v2 and
> remains accurate as a description of the shipped architecture. For user-facing
> documentation see [Project profiles & configuration](projects.md) (profile contents,
> resolution, the `construct project set` CLI), [Backup & restore](backup-restore.md)
> (the degraded-mode fallback), [Installation](installation.md) (the `add-config`
> entrypoint and one-liner sharing), and [Control panel](control-panel.md) (the Projects
> tab's sync status, remote config repos, and conflict UI). The **Current state** section
> below is kept as-is for historical context — it describes the pre-v2 behaviour this
> design replaced.
>
> v2 incorporates the design-review findings: git now lives **only on the
> host** (the VM store is a plain folder mirrored into a `vm` branch), the host
> config repo moves **outside the zip checkout**, JSON merges are gated by
> canonical formatting + schema validation, upstream provenance stores its merge
> bases, name collisions have a deterministic policy, and `default` is a
> reserved read-only seed.

## 1. Goal

Let the coding agents running on a VM **register and change a project's
requirements** — its repos, SDK/runtime versions, one-time setup steps, and MCP
servers — and have those changes **survive a VM reinstall**, without a human
having to re-enter them.

Today a *new* project round-trips (the reinstall save/restore and the
control-panel import both regenerate it), but a *change to an existing profile*
made on the VM is silently lost on the next reprovision or restore. Closing that
gap is the point of this design.

### Constraints that shaped the design

1. **Config must be able to live outside the code repo.** Some project repos are
   delivered to customers; their build/AI tooling config must not be committed
   into them. So "put the config in the project's own git repo" is not an option
   for those projects.
2. **One config, many repos.** A single project profile can drive several repos.
3. **Survives reinstall** — a wiped VM must come back with the same requirements.
4. **Agents can self-serve** — an agent can record a requirement change on the VM
   without a human in the loop for the common case.
5. **Deterministic reconciliation** — auto-merge when possible; when not,
   surface a real conflict rather than silently picking a side.

The key realisation: the *current profile structure already satisfies (1) and
(2)* — profiles live in Construct's own folder (outside the code) and already
carry a `repos[]` array. The only thing missing is **durable sync of VM-side
edits**. The design keeps that structure and fixes only the sync, using **git as
the reconciliation engine — on the host only**.

## 2. Current state (how it works today)

There are three copies of every profile, and the precedence between them is
hardcoded in two *opposite* directions — which is why VM edits leak away.

| Tier | Location | Lifetime today |
|------|----------|----------------|
| Host folder | `projects/*.json` in the host checkout | **Persistent** — survives VM reinstall (reinstall wipes only the VM) and Construct self-update (`Expand-Archive -Force` overwrites repo-shipped files but leaves user-added `<name>.json` in place) |
| VM upload copy | `/opt/construct/repo/projects` (a tar of the host folder) | **Ephemeral** — re-uploaded/replaced every provision |
| VM store | `/opt/construct/projects` (`PROJECTS_STORE`) | Survives reprovision (only `.../repo` is wiped); **gone on a full reinstall** |

- On **provision (host → VM)**, [`bin/generate-runtime-config.sh`](../bin/generate-runtime-config.sh)
  copies the repo copy over the store and *prefers the repo copy* — "repo wins".
- On **restore (VM → host)**, [`Provision-AgentVM.ps1`](../Provision-AgentVM.ps1)
  merges saved profiles but **never overwrites an existing host profile of the
  same name** — "host wins".

Put together: an agent edit to an existing, host-known profile on the VM is
overwritten by the repo copy on the next reprovision, and discarded on restore
because the host already has that name. Only *new* names survive.

## 3. Design overview — three tiers, git on the host only

```
   COMPANY UPSTREAM config repo(s)   ── shared baseline, real remotes (0..N)
        │  ▲
 import │  │  push-back   ★ MANUAL only (VS Code button / PowerShell)
 (pick  │  │
 files, ▼  │
 auto-merge)
   HOST config repo  (%LOCALAPPDATA%\The-Construct\config)
        branch `main` ── host truth; survives reinstall AND self-update
        branch `vm`   ── snapshots of the VM's files, committed by each sync tick
        │  ▲
  write │  │  read files over SSH → commit onto `vm` → git merge vm
  files ▼  │  (the whole engine runs on the host)
   VM  /opt/construct/projects   ── PLAIN FOLDER, single source of truth on the VM
        ▲                           no git, no working-tree semantics
   agent edits (directly, or via `construct project set`)
```

**Ownership model.** The upstream repos are the *shared company baseline*. The
host repo is *this developer's authority* for what runs on *this* VM. Local
changes (including agent changes synced up from the VM) win locally until the
developer chooses to contribute them back upstream — a fork/PR-style model.

**Why git on the host only.** An earlier draft made the VM store a git checkout
sharing history with the host. Review showed that all the failure-prone
complexity lived exactly there: dual-repo bootstrap and history-sharing, dirty
working trees defeating `receive.denyCurrentBranch=updateInstead`, and
push-into-unborn-branch edge cases after a reinstall. None of it was
requirement-driven — the VM only needs *a folder agents edit*; history and
merging only ever matter on the host, where the durable repo lives and where a
human resolves conflicts. Representing the VM's state as a **branch inside the
host repo** keeps every benefit (true 3-way ancestry, history/audit of both
sides, VS Code's native merge editor lighting up on a real git conflict) while
deleting the VM-side git entirely. Agents editing files directly — which they
realistically will, regardless of instructions — becomes first-class instead of
a failure mode.

## 4. The host config repo

- **Location: outside the zip checkout**, at a dedicated path —
  `%LOCALAPPDATA%\The-Construct\config` — with profiles in `config\projects\`,
  upstream provenance in `config\manifest\`, and stored merge bases in
  `config\bases\`. One config dir per machine, **shared across installed
  repo/ref slugs** (there is one VM and one config, regardless of which fork the
  checkout came from).
- **Why outside:** Construct self-updates `Expand-Archive -Force` over the
  checkout. A live git repo inside a folder that update tooling rewrites is
  fragile, and user data should not live inside an app install dir. With the
  dedicated location, self-updates never touch live config.
- **The shipped `projects/` folder is never a live source on the host.** (The
  original pre-v2 migration that copied its profiles into the config dir was
  removed: it re-ran on every tick and re-copied any profile missing from the
  config dir, so a profile deleted by sync was silently resurrected from the
  stale shipped copy and the two versions fought each other forever.)
- **Lazy initialisation:** the first time the sync engine runs with git
  available and finds no repo, it runs `git init` in the config dir and commits
  the current contents as the initial commit.
- **Cross-process lock:** every VS Code window runs its own sync engine
  (extensionKind `ui`), and the PowerShell engine runs during provisions — so a
  whole tick (read store → commit → merge → write-back) holds
  `<configDir>\.sync.lock` (atomic create; a lock older than 5 minutes counts
  as abandoned and is broken). Acquire returns an ownership token and release
  only deletes the file while it still carries that token — so a holder that
  outlived the stale threshold and was broken cannot delete its successor's
  lock on the way out. A busy extension engine skips its tick and lets
  the next timer retry; the PowerShell engine waits up to 90 s, because its
  pre-reinstall tick should run rather than silently skip. Without the lock,
  two concurrent ticks interleave and the one holding a stale store read
  commits spurious deletions — observed in the field as profiles mass-deleted
  and re-added within the same minute.
- **`default` is reserved and read-only.** `default.json` (and
  `project.schema.json`) are **not tracked** in the config repo; they ship with
  Construct and serve as today: the nothing-selected fallback and the schema.
  The panel and `construct project set` refuse the names `default` and
  `project.schema`. A customized baseline is a named profile (e.g. `base.json`)
  that the user selects. This keeps exactly one source per name — no
  shipped-vs-config precedence rules.

## 5. Single source of truth on the VM

- The **only** live config location on the VM is `/opt/construct/projects` — a
  **plain folder**, no git. Agents edit exactly one path; there is no second
  folder to be confused by.
- [`bin/generate-runtime-config.sh`](../bin/generate-runtime-config.sh) resolves
  every profile name from that store, with one exception: the reserved name
  `default` always resolves to the shipped copy in `/opt/construct/repo/projects`
  (§4). The old "repo copy wins / store as fallback" precedence is deleted.
- The `projects/` folder in the uploaded Construct repo is **seed material
  only**: provisioning seeds the store from the host config repo (§9), never
  reads the upload copy as a live source.

## 6. Host ↔ VM sync engine (automatic)

The engine runs entirely on the host (extension poll tick, the pre-reinstall
step, or the PowerShell path) over the SSH channel already used by the panel.
No git, jq, or other tooling is needed on the VM beyond what exists.

Each **sync tick**:

1. **Read** the VM store's profiles over the existing SSH runner (the same
   no-dependency pattern as `probe.js` / the scan script).
2. **Validate** each file against `project.schema.json`. An invalid file (e.g.
   a half-written agent edit) is **skipped with a panel warning** and never
   enters the repo — the branch stays always-valid.
3. **Commit** changes (including deletions) onto the **`vm` branch**, which
   always forks from the last agreed sync point — so 3-way ancestry is
   guaranteed by construction.
4. **Merge** `vm` into `main`. Non-overlapping edits merge silently; a true
   clash produces a real git conflict handled by the shared resolver (§8).
5. **Post-merge validation gate:** every touched profile must re-validate
   against the schema. A file that line-merges "cleanly" into invalid JSON is
   *treated as a conflict* and sent to the same resolver. Provisioning never
   proceeds from an invalid profile.
6. **Write back** the merged state to the VM store, then fast-forward `vm` to
   `main` — both sides identical, the new common base.

**Write-back guard (the race window):** a host→VM write only overwrites a VM
file whose current content still equals the `vm`-branch tip (unchanged since it
was read); anything else is left in place and reconciles through the merge path
on the next tick. Same guard for deletions.

**Mass-deletion guard:** when a read of an *existing* store yields **zero valid
profiles** while the `vm` branch still has some, the tick refuses to treat that
as "the user deleted everything" — it skips the VM side with a warning instead.
That state is indistinguishable from a half-provisioned store or one whose
files all failed validation, and propagating it would wipe `main` (observed in
the field). Individual deletions (down to one remaining profile) still
propagate; deleting the *last* profile is a host-side/panel action.

**Canonical JSON everywhere.** Every writer — the VM helper, the extension's
profile editor, the import path, the merge write-back — serialises profiles in
one canonical form: the fixed key order `sanitizeProfile` already produces,
2-space indent, one array element per line, trailing newline. Diffs are always
semantic, which keeps git's line-based merge honest; the §6.5 validation gate
catches the rare bad line-merge. A structural JSON merge driver is explicitly
**out of scope** — that is the bespoke-merge trap git was chosen to avoid.

**Triggers.** The extension's existing poll is the main tick; the host
`projects` config dir is watched so a **drag-and-dropped** `.json` is committed
and written to the VM on the next tick; and the reinstall flow runs a tick
**before** the wipe (§9).

**Accepted risk:** between ticks (e.g. VS Code closed), agent edits exist only
on the VM. The reinstall path always syncs first, so planned wipes lose
nothing; a catastrophic VM loss can lose config edits made since the last tick.

## 7. Upstream company config repos (optional)

A third tier: 0..N *shared* config repos on the company's git host, for
developers spanning several projects whose configs live in different repos.

- **Multiple remotes** are kept as **N staging clones** in a background cache
  (one per linked remote), fetched in the background. The host config repo
  stays a single clean repo. Staging clones are a **pure cache** — safe to
  delete at any time (see provenance below).
- **Selective import (not blanket).** A picker lists the config files found
  across the linked repos, grouped by repo, **none ticked by default**. This
  mirrors the existing VM-repo import UX in
  [`extension/src/projects.js`](../extension/src/projects.js) (`planImport`).
- **Provenance manifest, with stored bases.** Each imported file records
  `{ remoteUrl, ref, pathInRemote, importedAs, baseCommit, baseBlobSha }` **plus
  a stored copy of the base content** in `config\bases\`. Profiles are tiny, so
  merges must never depend on a staging clone surviving (shallow, GC'd, moved,
  deleted). The manifest and bases live inside the host config repo, so they are
  versioned and survive reinstalls with everything else.
- **Auto-merge on re-sync.** When upstream is refreshed, each tracked file is
  3-way merged (`git merge-file`): base = the stored base, *ours* = the local
  version, *theirs* = the new upstream version. Overlaps go to the shared
  resolver (§8); results pass the same canonical-form + validation gate as §6.
- **Rename/delete policy:** upstream file deleted → keep local, mark
  "orphaned: upstream removed", notify; upstream rename → treated as delete +
  new (user re-imports); local delete of an imported file → drop its manifest
  entry and base.
- **Name collisions — deterministic and boring.** The host namespace is flat,
  so: **an import never silently overwrites a profile with different
  provenance.** Same provenance (same remote + path) → it is an update, merged
  as above. Different provenance, or an existing local/VM-discovered file of the
  same name → UI: rename prompt (suggest `<name>-2`); CLI (`-ImportConfigs`):
  hard error naming the collision. This generalises the existing `planImport`
  rule (which already refuses name overwrites).
- **Push-back is MANUAL, always.** A "Push config to upstream" action (VS Code
  button, and a PowerShell command) commits the local versions of tracked files
  to a branch/PR on their origin remote. Never automatic — shared config affects
  other people's VMs and delivered baselines.

## 8. Conflict resolution

Two merge points — **host ↔ VM** (§6) and **local ↔ upstream** (§7) — share
**one resolver**. All conflicts materialise on the host, in the config repo.

- **In VS Code (extension path):** a failed auto-merge leaves the repo in git's
  normal conflicted state — and because `git merge vm` is a *real* merge of a
  real branch, VS Code's built-in 3-way **merge editor** lights up natively.
  The sync/provision **blocks**, watches git state, and **resumes once the merge
  is resolved and committed**.
- **In PowerShell (no extension):** the script detects the conflict
  (`git merge` non-zero / `git ls-files -u` non-empty) and **stops rather than
  provisioning a half-merged config**. Default: leave the conflict markers in
  place, open the folder/file, print "resolve, commit, then re-run" — a re-run
  sees the merge resolved and continues. Optional `-AutoResolve <ours|theirs>`
  lets an unattended run pick a side deterministically. (No `newer` option —
  timestamp ordering is unreliable and git-unnatural.)

## 9. Provisioning flow (revised)

The order becomes **back up → merge → provision**:

| Step | What it is | Mechanics |
|------|------------|-----------|
| 1. Back up current config | Bring un-synced VM edits home **before** any wipe | A full sync tick (§6): read VM files → `vm` branch → merge |
| 2. Config merge | Reconcile on the host (auto or resolver §8) | `git merge vm` on the host; conflict gate here |
| 3. Provision | Wipe (reinstall), then seed the fresh VM's store | **Write files from `main`** into the empty `/opt/construct/projects`; reset `vm` to `main` |

Seeding is plain file writes through the existing upload/scp channel — no git
plumbing on the VM, no push-into-empty-repo edge cases. The existing
auth/history backup ([`bin/export-config.sh`](../bin/export-config.sh)) still
runs for the non-profile data (subscription auth, chat history, git creds, …);
the profile portion moves to the sync tick above.

> Because the VM store is wiped on a full reinstall, step 1 is not optional — it
> is the only thing that carries an agent edit made since the last tick.

## 10. Git on the host — never required, strictly an upgrade

The design needs `git.exe` on the Windows host for the sync engine and remote
clones, but the one-liner install path must keep working without it.

| Moment | Needs host git? | Behaviour without git |
|---|---|---|
| Default install (no config params) | No | Unchanged — never prompts |
| Install with `-ConfigRepo` | **Yes, at install time** | Interactive: prompt to install (winget `Git.Git`). Unattended `-Auto`: attempt winget silently; on failure **abort loudly** — the user explicitly asked for something git requires |
| Install with `-ConfigDir` / zip bundle | No | Plain file copy into the config dir; versioned later, when git exists |
| Steady state: host↔VM sync | **Yes** — this *is* the merge engine | Degraded mode = today's behaviour: additive import of *new* VM profiles only (no edits sync, no merging). The Projects tab shows a persistent "install git to enable config sync/versioning" notice with one-click install |
| Reinstall backup-merge | Only if present | Falls back to today's export/restore (tarball profiles, additive, never overwrite) |

**Lazy init** (§4) makes the upgrade seamless: whenever git first becomes
available, the next sync tick initialises the repo and commits the current
folder contents. No migration step for the user.

## 11. The `add-config` entrypoint

A single, state-aware action on [`Auto-Install.ps1`](../Auto-Install.ps1) for
"add project configuration". It is a thin layer over machinery that already
exists.

**Behaviour:**

- **Construct not installed** → the **full install** path (build ISO, create VM,
  provision), with the imported configs already selected.
- **Construct already installed** → a **reprovision only** (keeps all data, no
  reinstall) that is **additive**: link the source, import the chosen configs,
  provision. Only *new* repos clone — [`bin/checkout-projects.sh`](../bin/checkout-projects.sh)
  already skips a checkout whose `.git` exists — so existing repos are
  untouched.

**Parameters (all forwardable through `install.ps1`, see §12):**

| Param | Meaning |
|-------|---------|
| `-Action add-config` | Trigger this mode; auto-pick full-install vs reprovision by install state |
| `-ConfigRepo <url>` | **Remote** source: clone to staging, import selected files. Single URL (see §13 one-repo rule) — also keeps it a scalar across the self-elevation relaunch |
| `-ConfigDir <path>` | **Local** source: import config files from `<path>\projects\*.json` (the local twin of `-ConfigRepo`) |
| `-ImportConfigs <a,b>` | Which config files to select; narrows a `-ConfigRepo`/`-ConfigDir` source. Omitted with `-ConfigDir` → import everything in the folder. Collisions follow §7 (CLI: hard error) |

**The one genuinely new bit of logic:** today a reprovision *replaces* the
project selection. `add-config` must instead compute **`PROJECTS = current-on-VM
∪ imported`** (read the VM's current `PROJECTS` from `config.env`, union the new
names) so existing selections are preserved. Everything else —
[`-Action`](../Auto-Install.ps1) menu-skip, `-Projects` picker-skip, idempotent
checkout, reprovision-keeps-data — already exists.

## 12. Parameterised install & generic passthrough

The main way to add configs is running `Auto-Install.ps1` directly from a local
checkout; the `irm … | iex` one-liner stays for the first bootstrap of
Construct itself. To make a **single shareable command** work, parameters must
flow through `install.ps1` → `Auto-Install.ps1` → (self-elevation) → provision.

- **`install.ps1` forwards generically, without declaring the params.** Drop
  `[CmdletBinding()]` so the automatic `$args` is populated, and splat it onto
  Auto-Install:

  ```powershell
  $fwd = @()
  if ($PSBoundParameters.ContainsKey('Repo')) { $fwd += '-Repo', $Repo }
  if ($PSBoundParameters.ContainsKey('Ref'))  { $fwd += '-Ref',  $Ref }
  $fwd += $args                 # generic: any other arg passes straight through
  & $auto @fwd
  ```

  `install.ps1` never learns the config param names — new Auto-Install params
  work through it for free, keeping this hot-path file tiny. Auto-Install is the
  sole authority that declares them (unknown params correctly error there).

- **Self-elevation already forwards.** [`Auto-Install.ps1`](../Auto-Install.ps1)
  rebuilds its elevated relaunch arg list from `$PSBoundParameters`, so any bound
  param survives the admin hop. `-ConfigRepo` is a single string (one repo per
  share, §13), so no `[string[]]` serialisation crosses that boundary.

- **Shareable one-liner** (piping to `iex` cannot carry args, so use the
  scriptblock idiom):

  ```powershell
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1))) `
      -ConfigRepo https://git.company.com/vm-config.git -ImportConfigs customer-portal,billing-api -Action add-config
  ```

## 13. Sharing from the extension (Projects tab)

The Projects tab gains a "Remote config repos" section: add/remove/list linked
remotes, an aggregated file checklist (grouped by repo, none ticked by
default), an **Import** action (does the real pull → merge → sync), and a
**Share** action.

**Share is per source, one repo at a time.** A share artifact never bundles
multiple repos — that keeps the param model flat (`-ImportConfigs name,name`
needs only file names, unique within one repo) and needs no `repo:name`
qualifier. Multiple repos are reproduced by running the command/bundle once per
repo (`add-config` is additive).

Two carriers, chosen by where the selected files live:

- **Remote-backed selection → share command.** Emits the §12 one-liner with a
  single `-ConfigRepo <url> -ImportConfigs <names> -Action add-config`. The
  extension builds a **fork-correct** install URL from the update marker
  (`Repo`/`Ref`). Copied to the clipboard.
- **Local-only selection → zip bundle.** The files have no URL to point at, so
  ship the bytes. The zip contains a generated `deploy.ps1` and a `projects/`
  folder with the selected profiles:

  ```
  my-config-bundle.zip
  ├─ deploy.ps1
  └─ projects/
     ├─ customer-portal.json
     └─ billing-api.json
  ```

  `deploy.ps1` is tiny and keeps `install.ps1` out of the bundle (so the
  always-downloaded installer stays minimal):

  ```powershell
  # Bootstraps Construct and imports the config files bundled next to this script.
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1))) `
      -ConfigDir "$PSScriptRoot" -Action add-config
  ```

  `$PSScriptRoot` resolves to the extracted-zip folder (which contains
  `projects/`), so it works wherever the user saves it. The extension generates
  `deploy.ps1` per bundle, baking in the fork-correct install URL / `Repo` /
  `Ref`.

**Code placement (matches the existing architecture).** The command builder is a
**pure function** in [`extension/src/projects.js`](../extension/src/projects.js)
(alongside `buildScanScript` / `planImport` / `sanitizeProfile`), unit-tested in
[`extension/test/projects.test.js`](../extension/test/projects.test.js). The zip
packer is the one new bit of IO in `extension.js` (bundle `deploy.ps1` +
selected profiles → user-chosen path).

## 14. First-run linking

First run offers two ways into the same import, then hands off to the extension
for steady state.

- **Parameter path:** `-ConfigRepo <url>` (and/or `-ConfigDir`) on
  Auto-Install/Install. `-ImportConfigs a,b` cherry-picks named files; **omitting
  it imports every config file discovered in the source** (both `-ConfigRepo` and
  `-ConfigDir`) — no screens either way. `-ConfigRepo` triggers the git
  check/prompt (§10). (As shipped this parameter path is non-interactive; the
  none-selected-by-default *picker* lives in the prompt path below and in the
  extension Projects tab.)
- **Prompt path (no params):** folded into the existing
  [`Select-ProjectProfiles`](../lib/AgentVm.Common.ps1) checkbox menu, which
  already has "open the folder" and "Continue" rows. Add a **"Link a remote
  config repo…"** row (prompts for a URL — and for git, if missing — clones to
  staging, refreshes the picker). The three options map to menu rows: link a
  remote / open the config folder / Continue with nothing (import none).
- **Steady state → the extension Projects tab** owns adding/removing remotes,
  importing more files, conflict resolution, and the manual push-back. The
  PowerShell first-run exists only so a brand-new VM can pull company config
  before the extension is even connected. The manifest persists what was linked
  and imported, so re-runs remember.

## 15. Agent-facing surface

- **Where agents write:** the single VM store `/opt/construct/projects/<name>.json`.
  Two equally supported paths:
  - `construct project set <name> …` — a small helper that **validates**
    (reusing `sanitizeProfile` from
    [`extension/src/projects.js`](../extension/src/projects.js); the repo,
    including `extension/`, is uploaded to `/opt/construct/repo`, and node is
    installed) and **writes canonical JSON**. It does *not* commit — there is no
    repo on the VM.
  - **Direct file edits.** First-class by design: the sync tick picks up any
    change, validates it, and journals it on the host (§6). An invalid file is
    skipped with a warning, never propagated.
  - The helper refuses the reserved names `default` and `project.schema` (§4).
- **How agents learn this:** add guidance to
  [`config/systemprompt.md`](../config/systemprompt.md), which seeds all three
  agents' instruction files on every fresh VM (`install_agent_system_prompt` in
  [`bin/install-ai-tools.sh`](../bin/install-ai-tools.sh)). It must say: record a
  project's repos/SDKs/setup in `/opt/construct/projects/<name>.json`
  (preferably via `construct project set`) or it is lost on reinstall;
  `provisionCommands` run on **every** provision and must be idempotent.

## 16. Security notes

- **Share commands never carry secrets.** A `-ConfigRepo` URL in a shared command
  is the clean URL; private repos rely on the recipient's own git auth (same as
  project-repo checkout today). MCP server auth stays as `bearerTokenEnvVar`
  references in the profile, never the token itself.
- **Provision commands run as root** on the VM, now driven by whatever is
  committed to a config source. Acceptable for a disposable, isolated sandbox VM
  with your own/company config; a conscious trust decision, not an accident.
- The existing auth/history backup still holds **plaintext** tokens on the host
  (`.construct-backup/`); unchanged by this design.

## 17. Decisions settled

| Decision | Resolution |
|----------|------------|
| Where git runs | **Host only.** The VM store is a plain folder; its state is a `vm` branch inside the host repo. No VM-side repo, bootstrap, or dirty-tree semantics |
| Authority when the same profile diverges on host and VM | `git merge vm` on the host; auto when possible, real git conflict surfaced otherwise |
| How VM edits reach the host | Host-driven sync tick (extension poll, pre-reinstall, PowerShell) — read → validate → commit on `vm` → merge → write back |
| Host repo location | `%LOCALAPPDATA%\The-Construct\config` — **outside** the zip checkout; shipped `projects/` is seed-only everywhere |
| Config-in-code-repo? | **No** — would ship to customers and lose multi-repo. Config lives outside the code, git-versioned separately |
| `default.json` | **Reserved, read-only seed** — not tracked in the config repo; editing refused; baseline customization = a named profile |
| JSON merge safety | Canonical serialization by every writer + mandatory post-merge schema validation; invalid results are conflicts. No structural merge driver |
| Upstream provenance | Manifest per imported file incl. `baseCommit`/`baseBlobSha` **and stored base content**; staging clones are disposable cache |
| Name collisions | Never silently overwrite across provenance; UI renames, CLI errors |
| Share scope | One remote repo per share artifact; multiple repos = multiple commands/bundles |
| Remote vs local share carrier | Remote → share command; local-only → zip bundle with `deploy.ps1` |
| Upstream push-back | Manual only (VS Code button / PowerShell command) |
| Keep `install.ps1` small | Yes — generic `$args` passthrough; bundle logic lives in the generated `deploy.ps1` |
| Git on the Windows host | Never required; strictly upgrades (§10). Installer prompts only when `-ConfigRepo` demands it; the extension offers one-click install for sync; everything degrades to today's behaviour |
| `-AutoResolve` | `ours` / `theirs` only; no timestamp-based `newer` |

## 18. Implementation touch points

The touch points that were built for this feature (kept for reference):

- **Host config repo** — dedicated dir, lazy `git init`,
  `main`/`vm` branch scheme. (The pre-v2 migration from the shipped
  `projects/` folder was later removed — see §4.)
- **Sync engine (host)** — the §6 tick: cross-process lock, SSH read,
  validation, `vm` commits, merge, post-merge validation gate, guarded
  write-back, mass-deletion guard; shared by the extension and the PowerShell
  path.
- [`bin/generate-runtime-config.sh`](../bin/generate-runtime-config.sh) — resolve
  from the VM store only (reserved `default` from the shipped copy); delete the
  repo-wins/store-fallback precedence and the `cp -f` refresh.
- [`bin/provision.sh`](../bin/provision.sh) / provisioning order — pre-wipe sync
  tick; seed the fresh store by writing files from `main`.
- **`construct project set` helper** on the VM — validate + write canonical
  JSON; refuse reserved names; no commit.
- [`Auto-Install.ps1`](../Auto-Install.ps1) — `-Action add-config`, `-ConfigRepo`
  (scalar), `-ConfigDir`, `-ImportConfigs`; union-not-replace `PROJECTS`;
  git check/prompt on `-ConfigRepo`; `-AutoResolve ours|theirs`.
- [`install.ps1`](../install.ps1) — drop `[CmdletBinding()]`; forward `$args`.
- **Repoint host readers** of `projects/` to the config dir (with fallback):
  `Select-ProjectProfiles` / `Get-ProjectRepoUrls` in
  [`lib/AgentVm.Common.ps1`](../lib/AgentVm.Common.ps1), the extension's
  `host.js`, and the provisioning upload/seed path.
- [`extension/src/projects.js`](../extension/src/projects.js) — remote-repo
  staging + manifest/bases, picker plan, collision policy, `buildShareCommand`
  (pure), canonical serializer, conflict-state detection.
- `extension.js` / Projects tab UI — remote-repo section, import action, share
  (command vs zip), `deploy.ps1` generation + zip packing, merge-editor gating,
  "install git" notice.
- [`config/systemprompt.md`](../config/systemprompt.md) — "register requirements
  here" guidance.
- Docs — fold the model into [`docs/projects.md`](projects.md) and
  [`docs/backup-restore.md`](backup-restore.md), cross-link this spec.
