# Saving & restoring config across reinstalls

A reinstall wipes the VM disk, so the installer can save the VM's current agent
configuration to the host and restore it onto the fresh VM. The backup lives in a
git-ignored `.construct-backup/` folder next to the scripts.

## What is saved

For the installed agents, from `root`'s home — never from inside the project repos:

- Instruction files: `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`,
  `~/.config/opencode/AGENTS.md` (+ any other `*.md`).
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
  `environment-id`) and the `state.sqlite*` event-store under `INCLUDE_AUTH`;
  `attachments/` under `INCLUDE_HISTORY`. Everything credential-bearing rides
  the auth gate: `settings.json` can hold provider passwords in plaintext, and
  the sqlite store holds the chat threads **and** the auth sessions / pairing
  state in one database — they can't be split, so a sanitized
  `INCLUDE_AUTH=false` backup drops the t3 threads rather than leak session
  tokens. The export briefly stops `t3code-serve` while copying the sqlite
  files so the snapshot is consistent (db/-wal/-shm copied together). Logs,
  caches, worktrees and the pid-bearing `server-runtime.json` are never
  captured. On restore, `restore-config.sh` stops `t3code-serve` across the
  copy (it holds the DB open), drops the freshly-minted empty DB, and restarts
  the service on the restored store — paired browsers and history come straight
  back. The backup also records that T3 Code was enabled
  (`backup-info.json`), and the restore reinstalls + starts it when the fresh
  provision didn't (e.g. a console-run reinstall that couldn't know the
  preference).
- Global git config + credentials: `~/.gitconfig`, `~/.git-credentials`.
- GitHub CLI login + config: `~/.config/gh/` (`hosts.yml` holds the `gh auth` token).
  The `gh` CLI is installed by default during provisioning.
- **npm registry auth**: `~/.npmrc`, so `npm publish`/installs from private registries keep
  working after a reinstall (it holds the registry `_authToken`). Saved only when auth is
  included — `INCLUDE_AUTH=false` omits it.
- **VS Code serve-web connection token**, so the browser `?tkn=` URL stays the same after a
  reinstall instead of regenerating. Unlike everything else here it lives outside home
  (`/etc/construct/vscode-serve-web.token`), so it rides in the backup at
  `etc/construct/vscode-serve-web.token`. On restore the host threads it into
  `install-vscode.sh` *before* serve-web starts (`restore-config.sh` runs too late — the
  token would already have been regenerated and the service started), and a token already
  on the VM wins on a reprovision. Saved only when auth is included.
- Project profiles: **normally versioned separately.** With git on the host, profile
  persistence goes through the [config sync](config-sync.md) engine — a sync tick runs
  before any wipe, so VM-side edits (including to *existing* profiles) are carried home
  first, and the fresh VM is re-seeded from the host's config repo. This backup/restore
  flow is the **degraded-mode fallback** for profiles: without git on the host (or if the
  sync tick can't run), it captures the VM's stored profiles (`/opt/construct/projects/*.json`,
  which carry your MCP servers and other per-project config), plus a generated profile for
  every cloned repo under `/root/repos` whose remote isn't already covered. On restore it's
  **additive and never overwrites** — the host keeps any profile it already has and adds
  only the rest, then re-provisions them (re-cloning repos and reconfiguring MCP servers).

> ⚠️ The backup contains **plaintext** auth tokens and git credentials. It is git-ignored
> and stays on your host; treat `.construct-backup/` as a secret.

## Triggering it from the installer

From `Auto-Install.ps1`, when the VM already exists:

- **Export config** — saves the current config to `.construct-backup/` and exits without
  reprovisioning or rebooting the VM. (It does briefly upload the repo and write/remove
  temp files on the VM, but leaves the agent setup unchanged.)
- **Reinstall / Redownload** — first scans the repos under `/root/repos` and warns about
  any uncommitted or unpushed work (you can abort), then asks **"Save and auto-restore?"**
  (default yes). If yes, it exports before wiping and restores onto the fresh VM after
  provisioning; the generated project profiles are folded into the selection so their
  repos are re-cloned, using the saved git credentials.

## By hand

The same building blocks run on the VM:

```bash
# export to a tarball (INCLUDE_AUTH=false to omit the auth tokens)
sudo OUT=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/export-config.sh
# restore from one
sudo BACKUP_TGZ=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/restore-config.sh
# scan repos for unsaved work (JSON)
/opt/construct/repo/bin/scan-repos.sh
```
