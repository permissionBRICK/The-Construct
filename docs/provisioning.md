# Provisioning

How a running VM gets turned into a ready agent box — from the Windows host with
`Provision-AgentVM.ps1`, or directly on the VM with the non-interactive `bin/provision.sh`.

## Provision from a Windows host

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
   `agent-vm` as `root` without prompts; then, on a full install/reinstall, reboots the VM.
   A reprovision of an already-provisioned VM is left running — every provisioning step
   applies its change live (services are restarted, config is re-read, no kernel is
   replaced) — unless the VM itself reports a pending reboot (`/var/run/reboot-required`,
   e.g. a project command installed a new kernel).

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

### Project selection menu

`Provision-AgentVM.ps1` (and `Auto-Install.ps1`) shows an interactive checkbox menu of the
`projects/*.json` profiles — Up/Down to move, Space to toggle, Enter to activate a row — with
every loaded profile selected by default, plus rows to open the `projects/` folder in Explorer
and to continue. The list refreshes in place if you drop a new profile into the folder while
the menu is open. With no profiles present it still shows, noting the VM will be built from the
default blank config. Pass `-Projects` to skip the menu. The non-interactive `provision.sh`
takes them via `PROJECTS=`. See [Project profiles](projects.md) for the profile format.

## Non-interactive provisioner (`bin/provision.sh`)

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

Recognized variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `AGENT_NAME` | — | Name of this agent VM |
| `PROJECTS` | — | Comma-separated project profiles to activate |
| `SSH_USER` | — | Seed SSH user |
| `AI_TOOLS` | `opencode,claude-code,codex` | Agent CLIs to install |
| `ALLOW_HOST_PACKAGES` | — | Allow project profiles to install host packages |
| `WORKSPACE_ROOT` | `/root/repos` | Where project repos are cloned |
| `CLAUDE_USER` | `root` | User Claude Code's CLI and VS Code extension settings are written for |
| `SETUP_ROOT_SSH_KEY` | `true` | Generate a root SSH key |
| `INSTALL_SDKS` | `true` | Install the merged project runtimes |
| `CHECKOUT_PROJECTS` | `false` | Clone the selected projects' repos |
| `START_SERVICE` | `true` | Start the `construct` service |
| `VSCODE_SERVER` | `true` | Install the VS Code CLI / server for Remote-SSH |
| `VSCODE_SERVE_WEB` | `true` | Autostart browser-based `code serve-web` |
| `VSCODE_TUNNEL` | `false` | Opt in to also set up + register a `code tunnel` |
| `SMB_SHARE` | `true` | Run an SMB server sharing `WORKSPACE_ROOT` to the host |
| `SMB_USER` | `dev` | SMB login name (file access on the share runs as root) |
| `SMB_SHARE_NAME` | `repo` | Share name in the UNC path `\\<vm>\<name>` |
| `SMB_PASSWORD` | _generated_ | Generated once and persisted; reused on reprovision |

### Project runtimes (SDKs)

Selecting a profile pulls in its declared repos and `sdks`. When `INSTALL_SDKS=true`,
`bin/install-sdks.sh` installs the merged runtimes on the VM host: `node` (via NodeSource),
`python` (apt python3 toolchain), and `dotnet` (via Microsoft's `dotnet-install.sh`).

## AI tool setup (`bin/ui-setup.sh`)

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

The workflow lets you pick tools, records `AI_TOOLS=`, and runs `bin/install-ai-tools.sh`.
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
SMB_SHARE=true
SMB_USER=dev
SMB_SHARE_NAME=repo
SMB_PASSWORD=
```

### Bypass-mode defaults

When `claude-code` is selected, setup **automatically applies the sandbox bypass defaults**
for the user that runs it — no manual config:

- `~/.claude/settings.json` (CLI): `env.IS_SANDBOX="1"`,
  `permissions.defaultMode="bypassPermissions"`, `skipDangerousModePermissionPrompt=true`, and
  `attribution.commit=""` / `attribution.pr=""` (see attribution note below).
- `~/.vscode-server/data/Machine/settings.json` (VS Code extension, machine scope):
  `claudeCode.allowDangerouslySkipPermissions=true`,
  `claudeCode.initialPermissionMode="bypassPermissions"`.

Both are merged with `jq`, preserving existing settings, and re-applied on every run.

> Bypass mode runs agent tools without permission prompts. It's appropriate for a disposable,
> isolated sandbox VM (which `IS_SANDBOX=1` flags) and risky anywhere holding real credentials
> or data.

### No AI attribution

Provisioning also turns off AI attribution by default so commits and PRs read as authored
solely by you:

- Claude Code (`~/.claude/settings.json`): `attribution.commit=""` and `attribution.pr=""` — empty
  strings suppress the `Co-Authored-By: Claude …` commit trailer and the "Generated with Claude
  Code" PR footer. (`attribution` is the current key; the older `includeCoAuthoredBy` is deprecated.)
- Codex (`~/.codex/config.toml`): `commit_attribution = ""` — suppresses the
  `Co-authored-by: Codex <noreply@openai.com>` commit trailer.
