# The Construct

This... is the Construct. Our loading program.

We can load anything — Claude Code, Codex, even Opencode — bypass mode, root access, anything we need.

The Construct is a single-command script that delivers a sandboxed vibecoding Ubuntu VM: unattended root access with no permission prompts, isolated inside Hyper-V on Windows, at minimal risk to the host PC.

Supported tools, zero configuration:
- VS Code Remote with the Claude Code extension
- Codex Remote via SSH
- Opencode Remote
- Direct SSH

The script wires up your local config for these when it finishes — just hit connect.

## Install (Windows, one line)

Open **PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

This downloads the latest repo and launches the guided installer (`Auto-Install.ps1`): it
elevates to Administrator (required for Hyper-V), builds the Ubuntu autoinstall ISO, then
creates and provisions the VM. You answer a few questions up front (RAM, disk size,
projects); everything after that runs unattended. If the VM already exists, you get a
menu — **reprovision** re-runs the config but keeps your data; **reinstall / redownload**
wipe the VM and reinstall it from ISO (offering first to save the current agent config and
auto-restore it afterwards, and warning about any uncommitted/unpushed work in the repos);
**export config** saves the current agent config to the host without reprovisioning or
rebooting the VM. See
[Saving & restoring config](#saving--restoring-config-across-reinstalls).

> Installs from `permissionBRICK/The-Construct`; pass `-Repo owner/name` for a fork.
> Requires WSL with a Linux distro for the ISO build — if it's missing, the installer tells
> you to run `wsl --install -d Ubuntu`, reboot, and re-run. The one-liner sets execution
> policy `Bypass` for its own process only.

## Quick Start (Windows host)

On a Windows 10/11 machine with Hyper-V available, one script builds the VM, installs
Ubuntu unattended, provisions the agent stack, and wires up your host for SSH + VS Code —
no manual VM interaction. Pick the option matching what you have.

### Option A — from scratch (`Auto-Install.ps1`)

The one-liner above, run from a local checkout. Downloads Ubuntu Server, builds the
autoinstall ISO, then creates and provisions the VM:

```powershell
.\Auto-Install.ps1
```

Override the release or supply your own source ISO with `-UbuntuRelease 24.04`,
`-IsoPath …`, or `-IsoUrl …`; add `-SkipCreateVm` to only build the ISO. Same WSL
requirement and reprovision/reset menu as the one-liner above.

### Option B — full bundle (repo + ISO together)

The distributed bundle is this repo with an autoinstall ISO (`agent-vm-autoinstall.iso`)
included in the zip.

1. Extract anywhere.
2. Right-click **`Create-AgentVM.ps1`** → **Run with PowerShell** (it self-elevates).

The script runs the whole flow unattended (~5 minutes for the install, plus provisioning).

> The host needs Hyper-V and the OpenSSH client; `Create-AgentVM.ps1` checks for both and
> installs them if missing (enabling Hyper-V may need one reboot — re-run afterwards).

### Option C — repo and ISO downloaded separately

Run `Create-AgentVM.ps1` and pick an ISO in the file dialog. An `*autoinstall*.iso`
triggers the unattended end-to-end flow; a plain Ubuntu Server ISO does a normal manual
install instead (then run `Provision-AgentVM.ps1` yourself once the VM is up).

### Option D — no admin access

If you'd rather not give the script admin rights (fair enough), set up the VM yourself and
run the provisioner directly. You need an Ubuntu Server VM with at least 4 GB RAM and
dynamic memory **disabled** (it breaks something in the kernel). For a manual install from
a plain Ubuntu ISO, configure:

- Install type: minimal
- Hostname: `agent-vm`
- User / password: `agent` / `agent`
- OpenSSH enabled

Then run `Provision-AgentVM.ps1` — it needs no admin access.

### What the automated flow does

| Step | Script | Action |
|------|--------|--------|
| 1 | `Create-AgentVM.ps1` | Ensures OpenSSH + Hyper-V, creates a Gen-2 VM (half host RAM ≤ 24 GB, Secure Boot off), boots the autoinstall ISO, waits for SSH, then calls `Provision-AgentVM.ps1`. |
| 2 | autoinstall ISO (built by `bin/build-autoinstall-iso.sh`) | Installs a blank **minimized** Ubuntu unattended: user/host preset, SSH on, the committed bootstrap key authorized, and a console hint to run the provisioner. |
| 3 | `Provision-AgentVM.ps1` | Connects (re-using the saved root key when re-provisioning an existing VM, otherwise the bootstrap key), uploads the repo, runs `bin/provision.sh`, obtains the VM's root key, removes the bootstrap key, configures the host's `~\.ssh\` + VS Code, and reboots the VM. |
| 4 | `bin/provision.sh` (on the VM) | `bootstrap.sh` → write `config.env` → root SSH key → install AI tools → generate runtime config → install selected projects' runtimes → start the `construct` service → install the VS Code CLI/server (and, when selected, deploy + register the `code tunnel`). |

Defaults line up across all of these: user `agent`, password `agent`, hostname `agent-vm`
(→ `agent-vm.mshome.net` on Hyper-V NAT), and `root` as the VS Code connection user.

---

The sections below document the individual components — for rebuilding the ISO, running
provisioning by hand, or setting up a VM without the Windows scripts.

## Build a Bootable Autoinstall ISO

`bin/build-autoinstall-iso.sh` repacks a stock Ubuntu live-server ISO into an
**autoinstall** ISO that installs a *blank* Ubuntu base completely unattended, with the
username, password, and hostname preconfigured, SSH enabled, the bootstrap public key
authorized, and a console banner telling you to finish setup from your Windows host. It
deliberately does **not** install the agent stack — that happens afterwards via
`Provision-AgentVM.ps1`.

Build it on a Linux box (needs `xorriso` and `mkpasswd`/`openssl`):

```bash
sudo apt-get install -y xorriso whois
# auto-detects /opt/construct/ubuntu-*-live-server-*.iso
bash bin/build-autoinstall-iso.sh

# or be explicit, and override identity via env:
VM_USER=agent VM_PASS=agent VM_HOST=agent-vm \
  bash bin/build-autoinstall-iso.sh /path/to/ubuntu-live-server.iso /path/to/out.iso
```

On **Windows** there's no native `xorriso` — don't run this directly. Use `Auto-Install.ps1`
(Option A), which runs this exact script inside WSL and installs the dependencies for you.
The build requires the committed bootstrap public key at `keys/bootstrap_ed25519.pub`. The
output is `<source-dir>/<hostname>-autoinstall.iso`.

What the generated ISO does on first boot:

- GRUB defaults (5 s timeout) to an "Autoinstall The Construct VM" entry that boots the
  installer with `autoinstall ds=nocloud;s=/cdrom/nocloud/`; the original menu entries are
  kept for manual installs.
- Installs Ubuntu unattended using the **minimized** server source (`ubuntu-server-minimal`),
  whole-disk `direct` layout (**it wipes the target disk**), creates the user/host, enables
  SSH (password + the bootstrap key in `authorized_keys`). Override with
  `SOURCE_ID=ubuntu-server` for the standard curated install.
- Writes the setup hint to `/etc/issue.d/construct.issue`, so the console login screen shows
  "run `Provision-AgentVM.ps1`". The provisioner later overwrites this file with live service
  info, so it's replaced automatically once setup completes.

> The built `*.iso` is git-ignored (large) and not committed; only the build script is.
> Re-run the script to regenerate it.

## Provision from a Windows Host

`Provision-AgentVM.ps1` provisions a *running* VM and configures your host. It's called
automatically by `Create-AgentVM.ps1`, but you can also run it standalone against any
reachable autoinstall VM.

What it does:

1. Packs this repo folder into a `tar.gz` (excludes `.git`, `*.iso`, the host-only
   `.construct-settings.json`, and the secret-bearing `.construct-backup/`).
2. Waits for the VM on port 22, re-prompting for the Hyper-V hostname if it can't connect.
3. Picks how to connect:
   - **Re-provision fast path** — if the root key from a previous run is saved on this host
     (`~\.ssh\agent_vm_ed25519`) and still authorizes `root` on the VM, it's used for the whole
     run. Every command then runs directly as `root` (no bootstrap key, no agent password, no
     `sudo`) and the VM's root key is left untouched (`SETUP_ROOT_SSH_KEY=false`, not regenerated).
   - **Bootstrap path (fallback)** — otherwise connect as `agent` with the committed bootstrap key
     (`keys/bootstrap_ed25519`), which the autoinstall ISO authorized; if it isn't authorized yet
     (hand-installed or freshly recreated VM), it's installed via the seed password, falling back
     to PuTTY instructions. The seed password is used for `sudo` on this path.
4. Uploads the archive to `/opt/construct/repo` and runs `bin/provision.sh` (directly as `root`
   on the fast path, otherwise via `sudo`).
5. Obtains the root SSH private key — reuses the saved copy on the fast path, otherwise retrieves
   the one the VM generated.
6. Removes the bootstrap public key from the `agent` user's `authorized_keys` (the fast path never
   installs it, but still strips any leftover copy from a failed/manual prior run).
7. Configures the Windows host: `~\.ssh\` (private key, `known_hosts`, and a `Host` entry in
   `~\.ssh\config`) and sets `remote.SSH.remotePlatform` in VS Code so Remote-SSH connects to
   `agent-vm` as `root` without prompts; then reboots the VM.

From a checkout of this repo on Windows:

```powershell
# defaults: VmHost=agent-vm.mshome.net, HostAlias=agent-vm, root as the VS Code user
.\Provision-AgentVM.ps1

# or override anything:
.\Provision-AgentVM.ps1 -VmHost agent-vm.mshome.net -HostAlias agent-vm `
    -AiTools "opencode,claude-code" -Projects "default"
```

Requires the Windows 10/11 OpenSSH client (`ssh`, `scp`, `ssh-keyscan`, `ssh-keygen`) and
`tar.exe` — all bundled with current Windows; no Posh-SSH dependency. After it finishes,
connect with `ssh agent-vm` or via the VS Code Remote Explorer.

> SECURITY: the bootstrap key in `keys/` is intentionally committed so a fresh autoinstall VM
> can be provisioned unattended. Treat it as burned — it's removed from the VM's
> `authorized_keys` at the end of provisioning, but anyone with the repo can log into an
> un-provisioned VM as `agent`. The retrieved root private key grants full VM access and is
> written to `~\.ssh\`.

### Non-interactive provisioner (`bin/provision.sh`)

`bin/provision.sh` is the VM-side, no-prompt counterpart to `ui-setup.sh`. It's what the
PowerShell script invokes, and you can also run it directly over SSH or in cloud-init. It
reads all inputs from environment variables (with sensible defaults) and runs the full chain:
`bootstrap.sh` → write `config.env` → root SSH key → `install-ai-tools.sh` →
`generate-runtime-config.sh` → `install-sdks.sh` → check out repos →
`run-provision-commands.sh` → start the service.

```bash
sudo env \
  AI_TOOLS=opencode,claude-code,codex \
  PROJECTS=names,separated,by,comma \
  AGENT_NAME=agent-vm-01 \
  CLAUDE_USER=root \
  bash /opt/construct/repo/bin/provision.sh
```

Recognized variables: `AGENT_NAME`, `PROJECTS`, `SSH_USER`, `AI_TOOLS` (default
`opencode,claude-code,codex`), `ALLOW_HOST_PACKAGES`, `WORKSPACE_ROOT` (default `/root/repos`),
`CLAUDE_USER` (default `root` — the user Claude Code's CLI and VS Code extension settings are
written for), `SETUP_ROOT_SSH_KEY` (default `true`), `INSTALL_SDKS` (default `true`),
`CHECKOUT_PROJECTS` (default `false`), `START_SERVICE` (default `true`), `VSCODE_SERVER`
(default `true` — install the VS Code CLI / server for Remote-SSH), `VSCODE_SERVE_WEB`
(default `true` — autostart browser-based `code serve-web`), `VSCODE_TUNNEL`
(default `false` — opt in to also set up + register a `code tunnel`).

### Project profiles & runtimes

Project profiles live in `projects/*.json` (see **Project Configs**). Selecting a profile
pulls in its declared repos and `sdks`. When `INSTALL_SDKS=true`, `bin/install-sdks.sh`
installs the merged runtimes on the VM host: `node` (via NodeSource), `python` (apt python3
toolchain), and `dotnet` (via Microsoft's `dotnet-install.sh`).

`Provision-AgentVM.ps1` (and `Auto-Install.ps1`) shows an interactive checkbox menu of the
`projects/*.json` profiles — Up/Down to move, Space to toggle, Enter to activate a row — with
every loaded profile selected by default, plus rows to open the `projects/` folder in Explorer
and to continue. The list refreshes in place if you drop a new profile into the folder while
the menu is open. With no profiles present it still shows, noting the VM will be built from the
default blank config. Pass `-Projects` to skip the menu. The non-interactive `provision.sh`
takes them via `PROJECTS=`.

## Manual Setup on a Blank Ubuntu VM

The ordered procedure to take a freshly installed **Ubuntu Server (minimal, headless)** VM to
the ready state by hand — no Windows scripts. Run everything as a sudo-capable user (or `root`).

### 0. Prerequisites

A minimal Ubuntu image often ships without SSH, sudo, git, or curl:

```bash
sudo apt-get update
sudo apt-get install -y openssh-server sudo git curl ca-certificates
sudo systemctl enable --now ssh
```

On Hyper-V NAT this template assumes `<hostname>.mshome.net` (e.g. `agent-vm.mshome.net`);
otherwise use the IP. `bootstrap.sh` installs the rest (jq, ripgrep, unzip, gnupg, Docker, …).

### 1. Put this repo at `/opt/construct/repo`

```bash
sudo mkdir -p /opt/construct
sudo chown -R "$USER:$USER" /opt/construct
# from a zip:
unzip /path/to/construct-repo.zip -d /opt/construct/repo
# or from Git:
git clone <CONSTRUCT_ENV_REPO_URL> /opt/construct/repo
```

The repo must live exactly at `/opt/construct/repo` — the scripts and systemd units hard-code
that path.

### 2. Bootstrap the host

```bash
sudo bash /opt/construct/repo/bootstrap.sh
```

Installs base packages and Docker, creates `/opt/construct`, `/root/repos`, and
`/etc/construct`, writes a default `/etc/construct/config.env`, installs the systemd units,
and — on an interactive terminal — launches the AI tool setup workflow. If Docker group
membership changed for your user, log out and back in.

### 3. Select and install AI tools

If the bootstrap ran interactively this already happened. Otherwise:

```bash
sudo /opt/construct/repo/bin/ui-setup.sh
```

The workflow optionally generates a root SSH key, lets you pick tools, records `AI_TOOLS=`, and
runs `bin/install-ai-tools.sh`. When `claude-code` is selected, it **automatically applies the
sandbox bypass defaults** for the user that runs it — no manual config:

- `~/.claude/settings.json` (CLI): `env.IS_SANDBOX="1"`,
  `permissions.defaultMode="bypassPermissions"`, `skipDangerousModePermissionPrompt=true`, and
  `attribution.commit=""` / `attribution.pr=""` (see attribution note below).
- `~/.vscode-server/data/Machine/settings.json` (VS Code extension, machine scope):
  `claudeCode.allowDangerouslySkipPermissions=true`,
  `claudeCode.initialPermissionMode="bypassPermissions"`.

Both are merged with `jq`, preserving existing settings, and re-applied on every run.

> **No AI attribution.** Provisioning also turns off AI attribution by default so commits and PRs
> read as authored solely by you:
>
> - Claude Code (`~/.claude/settings.json`): `attribution.commit=""` and `attribution.pr=""` — empty
>   strings suppress the `Co-Authored-By: Claude …` commit trailer and the "Generated with Claude
>   Code" PR footer. (`attribution` is the current key; the older `includeCoAuthoredBy` is deprecated.)
> - Codex (`~/.codex/config.toml`): `commit_attribution = ""` — suppresses the
>   `Co-authored-by: Codex <noreply@openai.com>` commit trailer.

> Bypass mode runs agent tools without permission prompts. It's appropriate for a disposable,
> isolated sandbox VM (which `IS_SANDBOX=1` flags) and risky anywhere holding real credentials
> or data.

### 4–7. Configure, generate, check out, start

```bash
sudo nano /etc/construct/config.env                       # 4. set AGENT_NAME, PROJECTS, SSH_USER, AI_TOOLS
sudo /opt/construct/repo/bin/generate-runtime-config.sh   # 5. merge project profiles
/opt/construct/repo/bin/checkout-projects.sh              # 6. (optional) clone project repos
sudo systemctl start construct                            # 7. start the service
docker ps
```

### 8. Connect from VS Code (Remote-SSH)

Add the VM as a Remote-SSH host and connect. Provisioning pre-seeds the Remote-SSH server
(CLI + REH build) and the agent extensions under `~/.vscode-server/`, so even the first
connect skips VS Code's usual server download/unpack wait — it's pinned to the desktop
client's commit when `Provision-AgentVM.ps1` can detect it (`code` on the host PATH),
otherwise to latest stable; on a version mismatch VS Code simply downloads its own build
on first connect as before. Because step 3 already seeded the machine-scope settings,
the Claude Code extension comes up in bypass mode automatically.

## Target Host

- Ubuntu Server, preferably 24.04 LTS
- SSH and sudo access
- Docker / Docker Compose
- Git, ripgrep
- `/opt/construct` for agent environment files
- `/root/repos` for project checkouts

## Local Config

Host-specific config lives outside Git at `/etc/construct/config.env`:

```env
AGENT_NAME=agent-vm-01
PROJECTS=default,your-name-here
AGENT_HOME=/opt/construct
WORKSPACE_ROOT=/root/repos
SSH_USER=agent
```

Don't put long-lived secrets here. Prefer SSH keys, short-lived tokens, or a secret manager.

## AI Tool Setup

Run the interactive setup workflow:

```bash
sudo /opt/construct/repo/bin/ui-setup.sh
```

It can generate a root SSH key for Codex App or other remote clients — it configures OpenSSH to
allow root login by public key only, authorizes the key, restarts SSH, and prints the private
key once so you can save it on the host. Run just that step directly with:

```bash
sudo /opt/construct/repo/bin/setup-root-ssh-key.sh
```

Currently supported selections:

- `opencode`: installs the CLI and autostarts `opencode serve --hostname 0.0.0.0 --port 4096`
  as root via `opencode-serve.service`.
- `claude-code`: installs the Claude Code CLI and prints the SSH connection target.
- `codex`: installs the Codex CLI, supports Codex App SSH remote connections, and can start the
  experimental Codex app-server on `0.0.0.0:4500` via `codex-app-server.service`.
- `pi`: records the selection only; installer/runtime not implemented yet.

Selections are stored in `/etc/construct/config.env`:

```env
AI_TOOLS=opencode,claude-code
OPENCODE_HOST=0.0.0.0
OPENCODE_PORT=4096
CODEX_HOST=0.0.0.0
CODEX_PORT=4500
CODEX_TOKEN_FILE=/etc/construct/codex-app-server.token
VSCODE_SERVER=true
VSCODE_SERVE_WEB=true
VSCODE_SERVE_WEB_HOST=0.0.0.0
VSCODE_SERVE_WEB_PORT=8000
VSCODE_SERVE_WEB_TOKEN_FILE=/etc/construct/vscode-serve-web.token
VSCODE_TUNNEL=false
VSCODE_TUNNEL_NAME=
```

### VS Code server & remote access

Independent of `AI_TOOLS`, provisioning installs the standalone VS Code CLI ("VS Code Server") to
`/usr/local/bin/code` **by default** (`VSCODE_SERVER=true`) so VS Code Remote-SSH works out of the
box and `code serve-web` / `code tunnel` are available. Two browser-reachable front ends sit on top
of it:

**`code serve-web`** — browser-based VS Code served directly over HTTP, **on by default**
(`VSCODE_SERVE_WEB=true`) via `code-serve-web.service`. It binds `0.0.0.0:8000`
(`VSCODE_SERVE_WEB_HOST`/`PORT`) and is reachable at `http://<dns>:<port>/?tkn=<token>`. There is no
account sign-in; access is gated by a **connection token** generated into
`VSCODE_SERVE_WEB_TOKEN_FILE`. Note this is a root-level IDE (terminal + filesystem) — keep it on
trusted VM networks. (To require an SSH tunnel instead of network exposure, set
`VSCODE_SERVE_WEB_HOST=127.0.0.1` and reach it via `ssh -L 8000:127.0.0.1:8000`.)

**`code tunnel`** (reachable through `https://vscode.dev/tunnel/<name>` with **no inbound port**)
is opt-in — enable it with `VSCODE_TUNNEL=true` (a config-file line or `-VsCodeTunnel true` on the
host script). `VSCODE_TUNNEL_NAME` is the tunnel identifier; left blank it is derived from the
hostname (lowercased, `[a-z0-9-]`). The CLI data dir and sign-in token live at
`/var/lib/vscode-tunnel`, so registration survives restarts and re-provisions.

- **First-time registration** needs a **one-time** browser sign-in (GitHub/Microsoft). When you
  select the tunnel, `code-tunnel.service` starts and emits a device-login link; the host
  provisioner (`Provision-AgentVM.ps1`) reads it back and **pauses before the reboot** so you can
  sign in against a still-valid code, then press Enter to continue. (Running headless? Read the
  link with `journalctl -u code-tunnel -n 50`.)
- **Re-provisioning** always re-deploys the `code-tunnel` service when it was previously deployed
  or is still registered — so a registered VM keeps autostarting the tunnel even with
  `VSCODE_TUNNEL=false`. The interactive sign-in is only re-run when `VSCODE_TUNNEL=true` **and**
  the VM isn't already registered.

The VM writes connection info to `/etc/issue.d/construct.issue` via
`construct-console-info.service`, so getty shows it on the physical console before the login
prompt. On Hyper-V NAT, the banner uses `<hostname>.mshome.net` (e.g. `agent-vm.mshome.net`)
and prints the current IP only as a fallback.

For Codex, prefer the supported SSH host workflow in Codex App: configure the VM as an SSH host,
ensure `codex` is on the remote PATH, and let Codex App start the remote app-server through SSH.
The managed `codex-app-server.service` is for experimental WebSocket app-server usage and
defaults to `0.0.0.0` for NAT-only VM setups.

## Project Configs

Project profiles live in `projects/*.json`; the schema is documented in
`projects/project.schema.json`. Each selected project may declare:

- repos to clone
- SDK versions needed by project containers
- MCP servers (see below)
- optional host packages (disabled by default)
- custom provisioning commands run on every provision (see below)
- test commands and notes

Selected projects are read from `PROJECTS` in `/etc/construct/config.env`. Requirements are
merged across all selected projects and deduplicated by:

```bash
sudo /opt/construct/repo/bin/generate-runtime-config.sh
```

The generated files are written to `/opt/construct/runtime/generated.json` and
`/opt/construct/runtime/generated.env`.

### MCP servers

The `mcp` array takes two kinds of entry:

- A **string** (`"filesystem"`, `"browser"`, `"github"`) — a docker-compose MCP
  container profile (the original mechanism; see `docker-compose.yaml`).
- An **object** — an MCP server written directly into each coding agent's own
  config (Claude Code, Codex, Opencode) by `bin/configure-mcp.sh` during
  provisioning. Two transports:

  ```jsonc
  // stdio (e.g. an npx server)
  { "name": "context7", "type": "stdio", "command": "npx",
    "args": ["-y", "@upstash/context7-mcp"], "env": { "KEY": "val" } }

  // http
  { "name": "sentry", "type": "http", "url": "https://mcp.sentry.dev/mcp",
    "headers": { "Authorization": "Bearer ..." }, "bearerTokenEnvVar": "SENTRY_MCP_TOKEN" }
  ```

  Optional on either form:
  - `"agents"`: subset of `["claude", "claude-code", "codex", "opencode"]` — only
    configure the server into those agents (default: all). List the same `name`
    twice with different `agents` to give an agent a different config.
  - `"enabled"`: set `false` to add the server **flagged disabled** (default
    true) so you can toggle it on in the agent UI. Opencode (`enabled: false`)
    and Codex (`enabled = false`) store a present-but-disabled entry; Claude has
    no global disable, so it is disabled per directory
    (`projects.<dir>.disabledMcpServers`) for the workspace and every repo dir.

  Servers are written **globally** (user scope) for all agents. Notes: Codex http
  supports only the URL plus an optional bearer-token env var — arbitrary
  `headers` are applied to Claude/Opencode only. Servers you list are upserted by
  name; unrelated/hand-added servers are left untouched.

Because MCP servers are declared in the project JSON, they are preserved across a
reinstall: the [save/restore](#saving--restoring-config-across-reinstalls) flow
backs up the VM's stored project profiles and restores any the host doesn't
already have.

### Provisioning commands

A profile may declare `provisionCommands` — a list of bash commands run on **every**
provision, as the project's "every-time" setup hook (build steps, fetching deps,
seeding a local `.env`, …):

```jsonc
{
  "name": "customer-portal",
  "repos": [{ "url": "git@github.com:acme/customer-portal.git", "directory": "customer-portal" }],
  "provisionCommands": [
    "npm ci",
    "cp -n .env.example .env || true"
  ]
}
```

Behaviour:

- **When** — `bin/run-provision-commands.sh` runs late in the provision, **after** the
  profile's repos are checked out **and** after the SDKs/runtimes (`node`, `python`,
  `dotnet`) are installed, so build steps find both their source and their toolchains.
- **Order** — commands run top-to-bottom; across several selected profiles they run in
  profile order.
- **Working directory** — each command runs from the profile's **first repo checkout**
  (`/root/repos/<directory>`), so `npm ci` / `dotnet restore` just work. Profiles with no
  repo (or whose repo isn't on disk — e.g. `CHECKOUT_PROJECTS=false` or a failed clone)
  run from the workspace root instead.
- **Idempotency** — they run every time, so they must be safe to re-run. Prefer
  idempotent forms (`npm ci`, `cp -n`, `… || true`).
- **Failure** — a command that exits non-zero is logged but does **not** abort the
  provision or the remaining commands (same as repo checkout and MCP setup).
- **Environment** — runs as root with `config.env` sourced and the merged `AGENT_*`
  vars derived from `generated.json`, so `WORKSPACE_ROOT`, `AGENT_PROJECTS`,
  `AGENT_REPOS_JSON` (valid JSON), `AGENT_SDKS_JSON`, `AGENT_MCP`, etc. are available.

Run them by hand with `sudo /opt/construct/repo/bin/run-provision-commands.sh`.

## Checkout Projects

When the selected projects declare repos, the provisioner checks them out
automatically during setup (it passes `CHECKOUT_PROJECTS=true`). To run it by hand:

```bash
/opt/construct/repo/bin/checkout-projects.sh
```

Repos are cloned under `/root/repos`.

**Credentials for private repos.** If any selected project's repos use `https://`
URLs, the installer asks **once** up front for a git username + token (press Enter to
skip if the repos are public). The credentials are written to a temporary file used as a
one-shot `store --file=` credential helper for the checkout, so all repos clone without
re-prompting. They are persisted into `~/.git-credentials` only if you also opted into
"store git credentials" — otherwise they are used for the checkout and discarded. (SSH
`git@…` URLs don't trigger the prompt — a username/token can't authenticate them; they
rely on whatever SSH auth is already configured on the VM.)

## Saving & restoring config across reinstalls

A reinstall wipes the VM disk, so the installer can save the VM's current agent
configuration to the host and restore it onto the fresh VM. The backup lives in a
git-ignored `.construct-backup/` folder next to the scripts.

**What is saved** (for the installed agents, from `root`'s home — never from inside the
project repos):

- Instruction files: `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`,
  `~/.config/opencode/AGENTS.md` (+ any other `*.md`).
- User-level memory and skills: `~/.claude/projects/<slug>/{memory,MEMORY.md}`,
  `~/.codex/{memories,memories_*.sqlite*}`, `~/.codex/skills` (minus the bundled system
  skills), `~/.claude/skills`.
- Agent settings: `~/.claude/settings.json`, `~/.codex/config.toml`,
  `~/.config/opencode/opencode.json`.
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
- Project profiles: the VM's stored profiles (`/opt/construct/projects/*.json`,
  which carry your MCP servers and other per-project config), plus a generated
  profile for every cloned repo under `/root/repos` whose remote isn't already
  covered. On restore the host keeps any profile it already has and adds the rest,
  then re-provisions them (re-cloning repos and reconfiguring MCP servers).

> ⚠️ The backup contains **plaintext** auth tokens and git credentials. It is git-ignored
> and stays on your host; treat `.construct-backup/` as a secret.

**Triggering it from the installer** (`Auto-Install.ps1`, when the VM already exists):

- **Export config** — saves the current config to `.construct-backup/` and exits without
  reprovisioning or rebooting the VM. (It does briefly upload the repo and write/remove
  temp files on the VM, but leaves the agent setup unchanged.)
- **Reinstall / Redownload** — first scans the repos under `/root/repos` and warns about
  any uncommitted or unpushed work (you can abort), then asks **"Save and auto-restore?"**
  (default yes). If yes, it exports before wiping and restores onto the fresh VM after
  provisioning; the generated project profiles are folded into the selection so their
  repos are re-cloned, using the saved git credentials.

**By hand**, the same building blocks run on the VM:

```bash
# export to a tarball (INCLUDE_AUTH=false to omit the auth tokens)
sudo OUT=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/export-config.sh
# restore from one
sudo BACKUP_TGZ=/tmp/construct-config-backup.tar.gz /opt/construct/repo/bin/restore-config.sh
# scan repos for unsaved work (JSON)
/opt/construct/repo/bin/scan-repos.sh
```

## Agent Runtime

The template includes a minimal local runtime in `agent-runtime/` so the VM can start before a
real construct image exists. It prints the merged project requirements and then stays alive.
Replace `agent-runtime/entrypoint.sh` or set `AGENT_IMAGE` to a registry image when the real
agent runtime is available.

## Service Lifecycle

```bash
sudo systemctl start|stop|restart|status construct
```

Provisioning also manages these units (when their tools are selected): `opencode-serve`,
`codex-app-server`, `code-serve-web` (browser VS Code), and `code-tunnel` (the VS Code remote
tunnel). Inspect any of them with `systemctl status <unit>` / `journalctl -u <unit>`.

Container logs:

```bash
docker logs construct
journalctl -u construct -n 100 --no-pager
```

Update from Git:

```bash
cd /opt/construct/repo
git pull
sudo /opt/construct/repo/bin/generate-runtime-config.sh
sudo systemctl restart construct
```

## Principles

- Keep Ubuntu minimal.
- Use Docker for project-specific tools.
- Keep setup logic in Git.
- Use project profiles instead of manual setup notes.
- Avoid global SDK installs unless unavoidable.
- Avoid storing secrets in VM images.
- Make VMs disposable and setup repeatable.
