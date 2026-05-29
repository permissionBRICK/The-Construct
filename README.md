# The Construct

This... is the Construct. It's our loading program.

We can load anything, from Claude Code, to Codex, and even Opencode... bypass mode, root-access, anything we need.

The Construct is a single-command script that delivers a sandboxed vibecoding Ubuntu VM setup for unattended root access inside a VM in without any permission prompts, isolated completely inside Hyper-V on Windows, at minimal risk to the host pc.

Supports the following Tools with zero configuration required:
- VS Code Remote with Claude Code Extension
- Codex Remote via SSH
- Opencode Remote
- Direct SSH

The script automatically sets up your local config for these tools when it is done, so all you have to do is hit connect.

## Install (Windows, one line)

Open **PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

That downloads the latest version of this repo and launches the guided installer
(`Auto-Install.ps1`): it elevates to Administrator (required to interact with Hyper-V), builds the Ubuntu autoinstall
ISO, then creates + provisions the Hyper-V agent VM. You're asked a few questions
up front (RAM, disk size, projects); everything after that runs unattended. If
the VM already exists you'll get a reprovision / reinstall / quit menu.

> Installs from `permissionBRICK/The-Construct` by default; pass `-Repo owner/name`
> to install from a fork. Requires WSL with a Linux
> distro for the ISO build — if it's missing, the installer tells you to run
> `wsl --install -d Ubuntu`, reboot, and re-run. The one-liner needs no special
> execution policy (it sets `Bypass` for its own process only).

## Quick Start (Windows host, fully automated)

The fastest path. On a Windows 10/11 machine with Hyper-V available, **one
script** builds the VM, installs Ubuntu unattended, provisions the agent stack,
and wires up your host for SSH + VS Code — with no manual VM interaction.

### Option A — from scratch, no ISO needed (`Auto-Install.ps1`)

If you only have this repo (no ISO), one script does **everything**: downloads
Ubuntu Server, builds the autoinstall ISO, and creates + provisions the VM.

```powershell
.\Auto-Install.ps1
```

It will:
1. Download the latest Ubuntu Server LTS ISO and verify its SHA256.
2. Build `agent-vm-autoinstall.iso` next to the script — the ISO remaster runs
   inside **WSL** (it needs `xorriso`, which is Linux-only; the script installs
   `xorriso`/`whois` into WSL automatically).
3. Hand off to `Create-AgentVM.ps1` for the unattended install + provisioning.

> Requires WSL with a Linux distro. If it's missing, the script tells you to run
> `wsl --install -d Ubuntu`, reboot, then re-run. Override the release or supply
> your own source ISO with `-UbuntuRelease 24.04`, `-IsoPath …`, or `-IsoUrl …`;
> add `-SkipCreateVm` to only build the ISO.

If you run this script with the VM already installed, it prompts you to either re-provision the vm (which just does all the config again but keeps your data), or instead completely reset it (which will wipe your vm and reinstall it from iso, in case you screwed it up or suspect you somehow caught something shady on the vm).

### Option B — full bundle (repo + autoinstall ISO together)

The distributed bundle is this repo with an Ubuntu Server autoinstall ISO
(`agent-vm-autoinstall.iso`) included in the zip file.

1. Extract the bundle anywhere.
2. Right-click **`Create-AgentVM.ps1`** → **Run with PowerShell** (it
   self-elevates to Administrator).

That's it. The script runs the whole flow unattended (~5 minutes for the install,
plus provisioning).

> The host needs Hyper-V and the OpenSSH client. `Create-AgentVM.ps1` checks for
> both and installs them if missing (enabling Hyper-V may require one reboot —
> just re-run the script afterwards).


### Option C — repo and ISO downloaded separately

If you have the repo and the autoinstall ISO as separate downloads:

- **Run** `Create-AgentVM.ps1` and pick an ISO in
  the file dialog. Picking an `*autoinstall*.iso` still triggers the unattended
  end-to-end flow; picking a plain Ubuntu Server ISO does a normal manual install
  instead (you then run `Provision-AgentVM.ps1` yourself once the VM is up).

### Option D - No Admin access required

If you do not trust this script with admin access (fair enough), you can set up the VM yourself, install it yourself, and then just run the provision script directly. 
You'll need an ubuntu server VM, at least 4gb ram, disable dynamic memory allocation (since that breaks something in the kernel), and run the ubuntu server setup using an iso of your choice.

  If you choose to do a manual install with an unprepared ubuntu ISO image, you have to set up
  the vm system as follows:
  - Install type: minimal
  - Hostname: agent-vm
  - user: agent
  - password: agent
  - openssh enabled per default

Then you just run the `Provision-AgentVM.ps1` script to install all the rest and set up your config - that script needs no admin access.

### What the automated flow does

| Step | Script | Action |
|------|--------|--------|
| 1 | `Create-AgentVM.ps1` | Ensures OpenSSH + Hyper-V, creates a Gen-2 VM (half host RAM ≤ 24 GB, Secure Boot off), boots the autoinstall ISO, waits for SSH, then calls `Provision-AgentVM.ps1`. |
| 2 | autoinstall ISO (built by `bin/build-autoinstall-iso.sh`) | Installs a blank **minimized** Ubuntu unattended: user/host preset, SSH on, the committed bootstrap key authorized, and a console hint to run the provisioner. |
| 3 | `Provision-AgentVM.ps1` | Uploads the repo, runs `bin/provision.sh` via sudo, retrieves the VM's root key, removes the bootstrap key, configures the host's `~\.ssh\` + VS Code, and reboots the VM. |
| 4 | `bin/provision.sh` (on the VM) | `bootstrap.sh` → write `config.env` → root SSH key → install AI tools (incl. codex) → generate runtime config → install selected projects' runtimes → start the `construct` service. |

Defaults line up across all of these: user `agent`, password `agent`, hostname
`agent-vm` (→ `agent-vm.mshome.net` on Hyper-V NAT), and `root` as the VS Code
connection user.

---

The sections below document the individual components — for rebuilding the ISO,
running provisioning by hand, or setting up a VM without the Windows scripts.

## Build A Bootable Autoinstall ISO

`bin/build-autoinstall-iso.sh` repacks a stock Ubuntu live-server ISO into an
**autoinstall** ISO that installs a *blank* Ubuntu base completely unattended,
with the username, password, and hostname preconfigured, an SSH server enabled,
the bootstrap public key authorized, and a console login banner telling you to
finish setup from your Windows host.

It deliberately does **not** install the agent stack — that happens afterwards
via `Provision-AgentVM.ps1`, which reaches the VM over SSH using the bootstrap
key baked into the image, and which also configures your host.

Build it on a Linux box (needs `xorriso` and `mkpasswd`/`openssl`):

```bash
sudo apt-get install -y xorriso whois
# auto-detects /opt/construct/ubuntu-*-live-server-*.iso
bash bin/build-autoinstall-iso.sh

# or be explicit, and override identity via env:
VM_USER=agent VM_PASS=agent VM_HOST=agent-vm \
  bash bin/build-autoinstall-iso.sh /path/to/ubuntu-live-server.iso /path/to/out.iso
```

**On Windows**, don't run this directly — there's no native `xorriso`. Use
`Auto-Install.ps1` (Option A above), which runs this exact script inside WSL and
installs the dependencies for you. To only build the ISO (no VM), add
`-SkipCreateVm`.

The build requires the bootstrap public key at `keys/bootstrap_ed25519.pub`
(committed). Defaults: user `agent`, password `agent`, hostname `agent-vm` —
matching `Provision-AgentVM.ps1`. The output is
`<source-dir>/<hostname>-autoinstall.iso`.

What the generated ISO does on first boot:

- GRUB defaults (5 s timeout) to an "Autoinstall The Construct VM" entry that
  boots the installer with `autoinstall ds=nocloud;s=/cdrom/nocloud/`; the
  original menu entries are kept for manual installs.
- Installs Ubuntu unattended using the **minimized** server source
  (`ubuntu-server-minimal` — small footprint, no-human-login), whole-disk
  `direct` layout (**it wipes the target disk**), creates the user/host, enables
  SSH (password + the bootstrap key in `authorized_keys`). Override with
  `SOURCE_ID=ubuntu-server` for the standard curated install.
- Writes the setup hint to `/etc/issue.d/construct.issue`, so the console
  login screen shows "run `Provision-AgentVM.ps1`". This is the same file the
  provisioner later overwrites with live service info, so it is replaced
  automatically once setup completes.

> The built `*.iso` is git-ignored (large) and is not committed; only the build
> script is. Re-run the script to regenerate it.

## Provision From A Windows Host

`Provision-AgentVM.ps1` provisions a *running* VM and configures your host. It is
called automatically by `Create-AgentVM.ps1`, but you can also run it standalone
against any reachable autoinstall VM.

What it does:

1. Packs this repo folder (the folder the script lives in) into a `tar.gz`
   (excludes `.git` and `*.iso`).
2. Waits for the VM to be reachable on port 22, re-prompting for the Hyper-V
   hostname (and re-deriving the host alias) if it cannot connect.
3. Connects over SSH as the `agent` user using the committed **bootstrap key**
   (`keys/bootstrap_ed25519`), which the autoinstall ISO authorized — no password
   prompt. The seed password is still used for `sudo` on the VM.
4. Uploads the archive, unpacks it to `/opt/construct/repo`, and runs the
   non-interactive provisioner `bin/provision.sh` via `sudo`.
5. Retrieves the root SSH private key the VM generated.
6. Removes the bootstrap public key from the `agent` user's `authorized_keys`.
7. Configures the Windows host: `~\.ssh\` (private key, `known_hosts`, and a
   `Host` entry in `~\.ssh\config`) and sets `remote.SSH.remotePlatform` in VS
   Code so Remote-SSH connects to `agent-vm` as `root` without prompts; then
   reboots the VM.

From a checkout of this repo on Windows:

```powershell
# defaults: VmHost=agent-vm.mshome.net, HostAlias=agent-vm, root as the VS Code user
.\Provision-AgentVM.ps1

# or override anything:
.\Provision-AgentVM.ps1 -VmHost agent-vm.mshome.net -HostAlias agent-vm `
    -AiTools "opencode,claude-code" -Projects "default"
```

Requirements on the host: Windows 10/11 OpenSSH client (`ssh`, `scp`,
`ssh-keyscan`, `ssh-keygen`) and `tar.exe` — all bundled with current Windows. No
Posh-SSH dependency. After it finishes, connect with `ssh agent-vm` or via the VS
Code Remote Explorer.

> SECURITY: the bootstrap key in `keys/` is intentionally committed so a fresh
> autoinstall VM can be provisioned unattended. Treat it as burned — it is
> removed from the VM's `authorized_keys` at the end of provisioning, but anyone
> with the repo can log into an un-provisioned VM as `agent`. The retrieved root
> private key grants full VM access and is written to `~\.ssh\`.

### Non-Interactive Provisioner (`bin/provision.sh`)

`bin/provision.sh` is the VM-side, no-prompt counterpart to `ui-setup.sh`. It is
what the PowerShell script invokes, and you can also run it directly over SSH or
in cloud-init. It reads all inputs from environment variables (with sensible
defaults) and runs the full chain: `bootstrap.sh` (non-interactive) → write
`config.env` → root SSH key → `install-ai-tools.sh` → `generate-runtime-config.sh`
→ `install-sdks.sh` (install the selected projects' runtimes) → start the service.

```bash
sudo env \
  AI_TOOLS=opencode,claude-code,codex \
  PROJECTS=names,separated,by,comma \
  AGENT_NAME=agent-vm-01 \
  CLAUDE_USER=root \
  bash /opt/construct/repo/bin/provision.sh
```

Recognized variables: `AGENT_NAME`, `PROJECTS`, `SSH_USER`, `AI_TOOLS` (default
`opencode,claude-code,codex`), `ALLOW_HOST_PACKAGES`, `WORKSPACE_ROOT` (default
`/root/repos` — where project repos are checked out), `CLAUDE_USER` (default
`root` — the user Claude Code's CLI and VS Code extension settings are written
for), `SETUP_ROOT_SSH_KEY` (default `true`), `INSTALL_SDKS` (default `true` —
install node/python/.NET declared by the selected projects), `CHECKOUT_PROJECTS`
(default `false`), `START_SERVICE` (default `true`).

### Project profiles & runtimes

Project profiles live in `projects/*.json` (see **Project Configs**). Selecting a
profile pulls in its declared repos and `sdks`. When `INSTALL_SDKS=true`,
`bin/install-sdks.sh` installs the merged runtimes on the VM host: `node` (via
NodeSource), `python` (apt python3 toolchain), and `dotnet` (the .NET SDK channel
via Microsoft's `dotnet-install.sh`).

`Provision-AgentVM.ps1` prompts which profiles to load (it lists `projects/*.json`)
unless you pass `-Projects`. The non-interactive `provision.sh` takes them via
`PROJECTS=`.

## Manual Setup On A Blank Ubuntu VM

The ordered procedure to take a freshly installed **Ubuntu Server (minimal,
headless)** VM to the ready state by hand — no Windows scripts. Run everything as
a sudo-capable user (or as `root`).

### 0. Prerequisites on the blank VM

A minimal Ubuntu image often ships without SSH, sudo, git, or curl:

```bash
sudo apt-get update
sudo apt-get install -y openssh-server sudo git curl ca-certificates
sudo systemctl enable --now ssh
```

On Hyper-V NAT this template assumes `<hostname>.mshome.net` (e.g.
`agent-vm.mshome.net`); otherwise use the IP. `bootstrap.sh` installs the rest
(jq, ripgrep, unzip, gnupg, Docker, …).

### 1. Put this repo at `/opt/construct/repo`

```bash
sudo mkdir -p /opt/construct
sudo chown -R "$USER:$USER" /opt/construct
# from a zip:
unzip /path/to/construct-repo.zip -d /opt/construct/repo
# or from Git:
git clone <CONSTRUCT_ENV_REPO_URL> /opt/construct/repo
```

The repo must live exactly at `/opt/construct/repo` — the scripts and systemd
units hard-code that path.

### 2. Bootstrap the host

```bash
sudo bash /opt/construct/repo/bootstrap.sh
```

Installs base packages and Docker, creates `/opt/construct`, `/root/repos`,
and `/etc/construct`, writes a default `/etc/construct/config.env`,
installs the systemd units, and — on an interactive terminal — launches the AI
tool setup workflow. If Docker group membership changed for your user, log out
and back in.

### 3. Select and install AI tools

If the bootstrap ran interactively this already happened. Otherwise:

```bash
sudo /opt/construct/repo/bin/ui-setup.sh
```

The workflow optionally generates a root SSH key, lets you pick tools, records
`AI_TOOLS=`, and runs `bin/install-ai-tools.sh`. When `claude-code` is selected,
`install-ai-tools.sh` **automatically applies the sandbox bypass defaults** for
the user that runs it — no manual config needed:

- `~/.claude/settings.json` (CLI): `env.IS_SANDBOX="1"`,
  `permissions.defaultMode="bypassPermissions"`,
  `skipDangerousModePermissionPrompt=true`.
- `~/.vscode-server/data/Machine/settings.json` (VS Code extension, Remote-SSH
  machine scope): `claudeCode.allowDangerouslySkipPermissions=true`,
  `claudeCode.initialPermissionMode="bypassPermissions"`.

Both are merged with `jq`, preserving any existing settings, and re-applied on
every run even if the tool was already installed.

> Bypass mode runs agent tools without permission prompts. It is appropriate for
> a disposable, isolated sandbox VM (which `IS_SANDBOX=1` flags) and risky
> anywhere holding real credentials or data.

### 4–7. Configure, generate, check out, start

```bash
sudo nano /etc/construct/config.env                       # 4. set AGENT_NAME, PROJECTS, SSH_USER, AI_TOOLS
sudo /opt/construct/repo/bin/generate-runtime-config.sh   # 5. merge project profiles
/opt/construct/repo/bin/checkout-projects.sh              # 6. (optional) clone project repos
sudo systemctl start construct                            # 7. start the service
docker ps
```

### 8. Connect from VS Code (Remote-SSH)

Add the VM as a Remote-SSH host and connect. VS Code installs its server under
`~/.vscode-server/` on first connect; because step 3 already seeded the
machine-scope settings, the Claude Code extension comes up in bypass mode
automatically.

## Target Host

- Ubuntu Server, preferably 24.04 LTS
- SSH access
- sudo access
- Docker / Docker Compose
- Git
- ripgrep
- `/opt/construct` for agent environment files
- `/root/repos` for project checkouts

## Local Config

Host-specific config lives outside Git:

```bash
/etc/construct/config.env
```

Example:

```env
AGENT_NAME=agent-vm-01
PROJECTS=default,your-name-here
AGENT_HOME=/opt/construct
WORKSPACE_ROOT=/root/repos
SSH_USER=agent
```

Do not put long-lived secrets in this file. Prefer SSH keys, short-lived tokens, or a secret manager later.

## AI Tool Setup

Run the interactive setup workflow with:

```bash
sudo /opt/construct/repo/bin/ui-setup.sh
```

The workflow can generate a root SSH key for Codex App or other remote clients. It configures OpenSSH to allow root login by public key only, authorizes the generated key, restarts SSH, and prints the private key once in the terminal so you can save it on the host machine.

You can run that SSH step directly with:

```bash
sudo /opt/construct/repo/bin/setup-root-ssh-key.sh
```

Currently supported selections:

- `opencode`: installs the CLI and autostarts `opencode serve --hostname 0.0.0.0 --port 4096` as root via `opencode-serve.service`.
- `claude-code`: installs the Claude Code CLI and prints the SSH connection target.
- `codex`: installs the Codex CLI, supports Codex App SSH remote connections, and can start the experimental Codex app-server on `0.0.0.0:4500` via `codex-app-server.service`.
- `pi`: records the selection only; installer/runtime is not implemented yet.

Selections are stored in `/etc/construct/config.env`:

```env
AI_TOOLS=opencode,claude-code
OPENCODE_HOST=0.0.0.0
OPENCODE_PORT=4096
CODEX_HOST=0.0.0.0
CODEX_PORT=4500
CODEX_TOKEN_FILE=/etc/construct/codex-app-server.token
```

The VM writes connection information to `/etc/issue.d/construct.issue` via `construct-console-info.service`, so getty shows it on the physical console before the login prompt.

On Hyper-V NAT, the banner uses the host-provided DNS name `<hostname>.mshome.net`, for example `agent-vm.mshome.net`, and prints the current IP only as a fallback.

For Codex, prefer the supported SSH host workflow in Codex App: configure the VM as an SSH host, ensure `codex` is on the remote PATH, and let Codex App start the remote app-server through SSH. The managed `codex-app-server.service` is for experimental WebSocket app-server usage and defaults to `0.0.0.0` for NAT-only VM setups.

## Project Configs

Project profiles live in `projects/*.json`.

The schema is documented in `projects/project.schema.json`.

Each selected project may declare:

- repos to clone
- SDK versions needed by project containers
- MCP servers to enable
- optional host packages, disabled by default
- test commands and notes

The selected projects are read from `PROJECTS` in `/etc/construct/config.env`.

Requirements are merged across all selected projects and duplicates are removed by:

```bash
sudo /opt/construct/repo/bin/generate-runtime-config.sh
```

The generated files are written to:

```bash
/opt/construct/runtime/generated.json
/opt/construct/runtime/generated.env
```

## Checkout Projects

After Git auth is configured:

```bash
/opt/construct/repo/bin/checkout-projects.sh
```

Repos are cloned under `/root/repos`.

## Agent Runtime

The template includes a minimal local runtime in `agent-runtime/` so the VM can start before a real construct image exists. It prints the merged project requirements and then stays alive.

Replace `agent-runtime/entrypoint.sh` or set `AGENT_IMAGE` to a registry image when the real agent runtime is available.

## Service Lifecycle

```bash
sudo systemctl start construct
sudo systemctl stop construct
sudo systemctl restart construct
sudo systemctl status construct
```

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
- Make VMs disposable.
- Make setup repeatable.
