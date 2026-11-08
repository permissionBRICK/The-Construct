# Config backups & restore

A VM's **config** is everything that is yours and not reproducible by a reprovision: agent
auth, memory, skills, chat history, `~/.secrets`, T3 Code state, git and CLI credentials
(the full list is under [What a backup contains](#what-a-backup-contains)). The rest of a VM
is disposable by design, and project profiles travel through [config sync](config-sync.md).

There are three ways a config is kept:

| | Where | Encrypted | Used for |
|---|---|---|---|
| **Host backups** | on the VM's host service | yes, only your PCs can open them | remote VMs: automatic backups, the default reinstall |
| **Backup files** | a `.cbk` file on this PC | yes, same format | any VM, by hand: keep a copy, move a config to another VM or host |
| **The local export** | `.construct-backup/` next to the scripts | **no** (plaintext) | reinstalling a local Hyper-V VM, as before |

## Backups on the host (remote VMs)

A VM on a host service that offers config backups (`/health` lists the `config-backups`
feature) is backed up **to its host**:

- **on a schedule** — the host decides when one is due (by default every 24 hours) and asks
  the VM through its activity heartbeat; nothing listens inside the VM;
- **on demand** — **Back up now** in the control panel's Backups module;
- **before a reinstall** — see [Reinstalling a remote VM](#reinstalling-a-remote-vm).

The host keeps a small rotation per VM (by default 4: the newest ones plus one from before the
start of the current week and one from before the start of the current month). The backup
taken before a reinstall is pinned (by default for 14 days) until a restore from it succeeded.
Backups belong to the VM's owner and name: a reinstalled VM sees its predecessor's backups.

**The host never sees your config.** The VM packs, compresses and encrypts the config before
the first byte leaves it:

```
per backup:  a fresh age key pair E; the payload is age-encrypted to E
per PC:      an RSA-3072 key pair; its private half never leaves the PC
keys file:   E's private key, RSA-OAEP-SHA256-wrapped to every enrolled PC key
```

The VM forgets E as soon as it has wrapped it, so it can create backups unattended but cannot
read old ones. To restore, a PC unwraps E with its own key and hands it to the VM over SSH
(on standard input, never on a command line or a disk); the VM then downloads and decrypts the
backup itself.

**What it needs:**

- a host service that offers the feature (an older host: the VM's `construct backup create`
  exits 4 and everything works as before);
- a VM provisioned by a Construct version that has `construct backup` (an older VM: the next
  reprovision installs it);
- at least one PC key **enrolled** on the VM. Every provision enrols the key of the PC that
  runs it, on local and remote VMs alike; a failure there is a warning, not a provisioning
  error. Until some PC is enrolled, scheduled backups fail with `no-recipients`.

**Status and warnings.** The Backups module shows the last backup and, while backups fail,
a warning; the Companion raises one Windows notification per failure streak. The reasons a
backup is skipped or fails: `vm-disk-space` (the VM keeps a reserve of max(2 GiB, 5 % of its
disk) free and skips a backup that would cut into it), `no-recipients`, `export-failed`,
`upload-failed`, `host-refused` (backups disabled, the host's free space or quota, a backup
already running), `verify-failed`. A run that fails removes everything it wrote.

The host side (storage directory, settings, the API) is described in
[remote-host.md](remote-host.md) and [service/README.md](../service/README.md).

## This PC's backup key

Created on first need, per Windows user, in `%LOCALAPPDATA%\The-Construct\backup\`:

| File | Content |
|---|---|
| `pc-key.dpapi` | the private key, DPAPI-protected for your Windows account |
| `pc-key.pub.pem` | the public key that is enrolled on VMs |
| `pc-key.json` | fingerprint (SHA-256 of the public key), label (the computer name), `createdAt`, `exportedAt` |
| `pc-key.<fingerprint>.dpapi` | a key this PC used before an import replaced it |

> ⚠️ **Export the key.** Host backups and backup files can only be opened with a PC key they
> were made for. If this PC and its Windows profile are lost and no other enrolled PC exists,
> every backup is unreadable. The control panel warns while the key was never exported.

```powershell
# write the key, protected by a passphrase you type twice (at least 8 characters)
.\Provision-AgentVM.ps1 -Action backup-key-export -BackupKeyFile D:\safe\construct-backup-key.json
# install it on another PC (or this one after a reinstall of Windows)
.\Provision-AgentVM.ps1 -Action backup-key-import -BackupKeyFile D:\safe\construct-backup-key.json
```

The export file is the private key encrypted with your passphrase (PBKDF2-SHA256 with 600000
iterations, AES-256-CBC, HMAC-SHA256). Keep the file and the passphrase apart. An import with a
wrong passphrase changes nothing. If the PC already has a *different* key, the import asks
before it replaces it, and keeps the old key as `pc-key.<fingerprint>.dpapi`: backups made for
the old key stay readable, because unwrapping tries every key in the folder. A newly imported
key is enrolled on a VM by its next reprovision, or right away with
`Provision-AgentVM.ps1 -Action backup-enroll -InstanceName <name>`.

Several PCs can be enrolled on one VM; each backup is then wrapped for all of them. A VM
never accepts a key from its host, only from a PC over SSH: a host that could add a key could
read every later backup.

## Reinstalling a remote VM

`Auto-Install.ps1` (or **Reinstall** in the control panel) first scans the VM's repos for
unsaved work, then decides what the rebuilt VM restores. With a host that offers config
backups and a usable key on this PC:

| `-BackupMode` | What happens |
|---|---|
| `save` (the default; "Yes" in the console) | The VM backs itself up to the host now (`pre-reinstall`). If that fails, or the VM is not reachable: the newest host backup this PC can open — offered in the console, taken without a question from the panel. If there is none: the config is exported to this PC as before. |
| `existing` | The newest host backup this PC can open; when there is none, the backup saved on this PC. |
| `wipe` | Nothing is saved or restored. |

The chosen backup goes to provisioning as `-RestoreHostBackupId <id>`. After `provision.sh`,
the provisioner unwraps the backup's key with this PC's key, the VM downloads and decrypts the
backup (`construct backup fetch`) and `restore-config.sh` restores it. The VM then restores
the backup's project profiles (only the ones it does not have), its serve-web token (the
browser `?tkn=` URL stays the same) and its enrolled backup keys. The project checkout and
project commands run afterwards, exactly as with a backup from this PC, and finally the VM
tells the host that the backup was restored, which unpins it.

If the restore fails, the VM is still provisioned and the backup stays on the host; the error
names the command that retries it:

```powershell
.\Provision-AgentVM.ps1 -Action backup-restore -InstanceName work-vm -BackupId <id>
```

**Git credentials.** A fresh pre-reinstall backup reports the host names its git credential
store covers (never the credentials). When they cover every repo host of the selected
profiles, the credential prompt is skipped and the VM clones with its restored store; with an
older host backup the prompt behaves as for a fresh install.

**Project profiles** the backup generated for loose repos come back as stored profiles on the
VM but are not added to the project selection (the PC never sees the backup's content); select
them in the panel and reprovision to clone their repos.

When the host lacks the feature, the VM lacks `construct backup`, or this PC has no usable key,
the reinstall falls back to the [local export](#local-vms-and-the-plaintext-export) with a
warning.

## Backup files on this PC

The same encrypted format, as a file (`.cbk`: line 1 `construct-backup/1`, line 2 the keys
file, then the encrypted payload). Works for local and remote VMs; the data streams through SSH
and never through a PowerShell pipeline.

```powershell
# a fresh backup of the VM, straight into a file (no host involved)
.\Provision-AgentVM.ps1 -Action backup-save -InstanceName work-vm -BackupFile D:\backups\work-vm.cbk
# a copy of one of the host's backups (remote VMs)
.\Provision-AgentVM.ps1 -Action backup-save -InstanceName work-vm -BackupId <id> -BackupFile D:\backups\work-vm.cbk
# restore a file onto any VM -- another VM, another host, a local VM
.\Provision-AgentVM.ps1 -Action backup-restore -InstanceName other-vm -BackupFile D:\backups\work-vm.cbk
```

`backup-save` enrols this PC's key first, so the file is always one this PC can open. A
restore replaces the VM's agent config (like a reinstall's restore) and adds the backup's
project profiles, serve-web token and backup keys. The file can only be restored by a PC whose
key it was made for. The control panel offers the same actions (**Save to this PC**,
**Restore…**, **Restore from file…**, **Export backup key…**, **Import backup key…**); they
run in a console window because the passphrases are typed there.

All backup actions accept the same targeting as `-Action export` (`-InstanceName`, or
`-VmHost`/`-HostAlias`/`-SshPort`/`-LocalKeyName`) and never provision, upload the repo or
reboot. `-Action backup-enroll` only enrols this PC's key; `-Action backup-create` backs a VM up
to its host over SSH (the reinstall uses it with `-BackupTrigger pre-reinstall`).

## Local VMs and the plaintext export

A local Hyper-V VM (no host service) keeps today's reinstall flow: the config is exported to a
git-ignored `.construct-backup/` folder next to the scripts and restored onto the fresh VM.
Backup files work for local VMs as well.

From `Auto-Install.ps1`, when the VM already exists:

- **Export config** — saves the current config to `.construct-backup/` and exits without
  reprovisioning or rebooting the VM.
- **Reinstall / Redownload** — first scans the repos under `/root/repos` and warns about
  any uncommitted or unpushed work (you can abort), then asks **"Save and auto-restore?"**
  (default yes). If yes, it exports before wiping and restores onto the fresh VM after
  provisioning; the generated project profiles are folded into the selection so their
  repos are re-cloned, using the saved git credentials.

The same export is the fallback of a remote reinstall (see above), and a new VM (local or
remote) is offered the export saved on this PC.

> ⚠️ The export contains **plaintext** auth tokens and git credentials. It is git-ignored
> and stays on your PC; treat `.construct-backup/` as a secret.

> **With several VMs:** `.construct-backup/` is **one folder next to the scripts**, not one
> per instance, and an export overwrites what's there. Export one VM at a time and copy the
> folder somewhere else if you need to keep two, or use backup files, which are per file.
>
> Project profiles are usually the exception: **once a git-backed sync tick has succeeded**,
> they live on that instance's own branch in the host config repo (`vm-<instance>`, see
> [config sync](config-sync.md)) and are not carried by this backup at all. But that is
> exactly the path that degrades — with no git on the host, or when the tick cannot run, the
> profiles fall back into this same singleton backup and *are* overwritten by the next VM's
> export. If you are running several VMs without git on the host, treat profiles as being
> as at-risk as everything else here.
>
> The host-side files that describe *the VM itself* are not part of any backup, and do not
> need to be: the instance registry (`%LOCALAPPDATA%\The-Construct\instances.json`) and each
> VM's own settings (`%LOCALAPPDATA%\The-Construct\instances\<name>.json`, or the top level of
> `.construct-settings.json` for the default `agent-vm`) survive a reinstall untouched — they
> live outside the scripts checkout that a Construct update replaces. Copy them yourself when
> you move to a **new PC**, together with an export of the backup key.

## What a backup contains

The same content in all three forms (`bin/export-config.sh`), from `root`'s home — never from
inside the project repos:

- Machine-local agent instructions: `~/construct-custom-system-prompt.md`. The agents'
  instruction files (`~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`,
  `~/.config/opencode/AGENTS.md`) are *managed*: every provision rewrites them, for all
  agents alike, from the shipped [`config/systemprompt.md`](../config/systemprompt.md)
  with this custom file appended below it. So the custom file is what carries your
  own rules across reprovisions and reinstalls; edits made to the managed files
  themselves do not survive. After changing the custom file run `construct systemprompt`
  to regenerate the managed files right away. On restore, the custom file comes back
  after provisioning rendered the bare template, so `restore-config.sh` renders again.
- The managed instruction files themselves (+ any other `*.md` beside them) are still
  captured, so a restore is complete even on a Construct version without the renderer.
- User-level memory and skills: `~/.claude/projects/<slug>/{memory,MEMORY.md}`,
  `~/.codex/{memories,memories_*.sqlite*}`, `~/.codex/skills` (minus the bundled system
  skills), `~/.claude/skills`.
- Agent settings: `~/.claude/settings.json`, `~/.codex/config.toml`,
  `~/.config/opencode/opencode.json`.
- Chat history (`INCLUDE_HISTORY=true`, the default): Claude's per-project session
  transcripts (`~/.claude/projects/<slug>/`) and `~/.claude/history.jsonl`; Codex's
  `~/.codex/{sessions,archived_sessions}` rollouts + indexes; Opencode's
  `~/.local/share/opencode/{storage,project,opencode.db*}`. Codex-specific wrinkle:
  modern Codex lists threads from a sqlite index it backfills **once** on first start,
  and the app-server starts before the restore lands — so the restore deletes the
  fresh, empty index (`~/.codex/state_*.sqlite`) and restarts `codex-app-server`,
  making Codex re-index the restored rollouts. Titles come back from the transcripts;
  index-only metadata (archived flags) does not.
  `HISTORY_RETENTION_DAYS` (default `30`, `0` keeps everything; the control panel's
  **Settings → VM resources → Chat history kept in backups**, passed as
  `-HistoryRetentionDays` to `Auto-Install.ps1` / `Provision-AgentVM.ps1 -Action export`)
  limits how much of it the export carries: Claude session transcripts and per-session
  directories, and Codex rollouts, whose newest file is older than that many days are
  left out (the live VM is never touched). Memory (`memory/`,
  `MEMORY.md`), `~/.claude/history.jsonl`, the Codex indexes and the Opencode / T3
  databases are always kept whole. The value is recorded as `historyRetentionDays` in
  `backup-info.json`.
- **Subscription auth**, so you don't re-authenticate after a reinstall:
  `~/.claude/.credentials.json`, `~/.claude.json`, `~/.codex/auth.json`,
  `~/.local/share/opencode/auth.json`.
- **MCP server auth**, so connected MCP servers stay logged in across a reinstall:
  the OAuth tokens each agent saves after authenticating to a remote MCP server.
  Claude keeps them inside `~/.claude/.credentials.json` (saved above) plus an
  `~/.claude/mcp-needs-auth-cache.json` state cache; Codex in `~/.codex/.credentials.json`;
  Opencode in `~/.local/share/opencode/mcp-auth.json`. (`claude.ai` connectors are
  authenticated server-side against your Anthropic account, so there is nothing local
  to save for them.)
- **T3 Code** (when the web GUI is enabled or installed): everything it needs to
  keep working after a reinstall, from `~/.t3/userdata/` — `keybindings.json`
  always; `settings.json`, the secret key material (`secrets/`,
  `environment-id`) and the `statev2.sqlite*` event-store under `INCLUDE_AUTH`
  (`state.sqlite*` on T3 Code before 0.1.0; 0.1.0 leaves that file behind unused);
  `attachments/` under `INCLUDE_HISTORY`. Everything credential-bearing rides
  the auth gate: `settings.json` can hold provider passwords in plaintext, and
  the sqlite store holds the chat threads **and** the auth sessions / pairing
  state in one database — they can't be split, so a sanitized
  `INCLUDE_AUTH=false` backup drops the t3 threads rather than leak session
  tokens. Logs, caches, worktrees and the pid-bearing `server-runtime.json` are never
  captured. A backup for a reinstall briefly stops `t3code-serve` while copying the sqlite
  files so the snapshot is consistent, and writes the busy threads to `t3-resume.json` at
  the top of the backup; after its final reboot, the reinstalled VM tells those threads
  what happened (see
  [Resuming interrupted T3 Code threads](provisioning.md#resuming-interrupted-t3-code-threads)).
  A scheduled or on-demand host backup leaves T3 Code running and copies every SQLite
  database it captures (T3, OpenCode, Codex memories) with SQLite's online backup instead.
  On restore, `restore-config.sh` stops `t3code-serve` across the
  copy (it holds the DB open), drops the freshly-minted empty DB (both file
  names), and restarts the service on the restored store — paired browsers and
  history come straight back. A `state.sqlite` from a backup taken before T3
  Code 0.1.0 is imported into a new `statev2.sqlite` on that start. The backup also
  records that T3 Code was enabled (`backup-info.json`), and the restore reinstalls +
  starts it when the fresh provision didn't (e.g. a console-run reinstall that couldn't
  know the preference).
- Global git config + credentials: `~/.gitconfig`, `~/.git-credentials`.
- GitHub CLI login + config: `~/.config/gh/` (`hosts.yml` holds the `gh auth` token).
  Saved regardless of `INCLUDE_AUTH`. The `gh` CLI is installed by default during
  provisioning.
- **GitLab CLI login + config**: `~/.config/glab-cli/` (`config.yml` holds host
  tokens alongside settings). Saved only when auth is included —
  `INCLUDE_AUTH=false` omits the entire directory. Restore overlays it into the
  user's home, preserving the saved file permissions.
- **npm registry auth**: `~/.npmrc`, so `npm publish`/installs from private registries keep
  working after a reinstall (it holds the registry `_authToken`). Saved only when auth is
  included — `INCLUDE_AUTH=false` omits it.
- **User secrets store**: everything under `~/.secrets` — a free-form directory for
  arbitrary secret material (API keys, tokens, `.env` files, …). Captured verbatim and
  restored with tightened permissions (directories `700`, files `600`). Saved only when
  auth is included — `INCLUDE_AUTH=false` omits it entirely.
- **VS Code serve-web connection token**, so the browser `?tkn=` URL stays the same after a
  reinstall instead of regenerating. Unlike everything else here it lives outside home
  (`/etc/construct/vscode-serve-web.token`), so it rides in the backup at
  `etc/construct/vscode-serve-web.token`. With the local export the PC threads it into
  `install-vscode.sh` *before* serve-web starts, and a token already on the VM wins on a
  reprovision; with an encrypted backup the VM writes it back itself and restarts serve-web.
  Saved only when auth is included.
- **The enrolled backup keys** (`/etc/construct/backup-recipients/`, public keys only),
  restored additively, so the PCs that could open the VM's backups before a reinstall still
  can afterwards.
- Project profiles: **normally versioned separately.** With git on the host, profile
  persistence goes through the [config sync](config-sync.md) engine — a sync tick runs
  before any wipe, so VM-side edits (including to *existing* profiles) are carried home
  first, and the fresh VM is re-seeded from the host's config repo. The backup is the
  **degraded-mode fallback** for profiles: without git on the host (or if the
  sync tick can't run), it captures the VM's stored profiles (`/opt/construct/projects/*.json`,
  which carry your MCP servers and other per-project config), plus a generated profile for
  every cloned repo under `/root/repos` whose remote isn't already covered. On restore it's
  **additive and never overwrites**: a profile that already exists is kept.

## By hand (on the VM)

```bash
# encrypted backups (root; progress on stderr, documented output on stdout)
sudo construct backup status                     # the host's view: last backup, failures, next due
sudo construct backup list                       # the host's backups of this VM
sudo construct backup create                     # back up to the host now
sudo construct backup create --stdout > x.cbk    # a backup file, no host involved
sudo construct backup recipients list            # the enrolled PC keys
sudo construct backup estimate                   # the space a backup would need

# the plaintext export (INCLUDE_AUTH=false to omit the auth tokens)
sudo OUT=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/export-config.sh
# restore from one
sudo BACKUP_TGZ=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/restore-config.sh
# scan repos for unsaved work (JSON)
/opt/construct/repo/bin/scan-repos.sh
```

A restore of an encrypted backup needs its key, which only a PC can unwrap: use
`Provision-AgentVM.ps1 -Action backup-restore` for that.
