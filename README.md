<div align="center">

<img src="assets/banner.svg" alt="The Construct" width="100%">

### *"This… is the Construct. Our loading program. We can load anything."*

**A disposable Ubuntu VM for unattended AI coding agents.**
Claude Code, Codex, and Opencode running as root in bypass mode — sealed inside Hyper-V,
where they can't touch your host PC.

[![License: MIT](https://img.shields.io/badge/License-MIT-00cc66.svg?style=flat-square)](LICENSE.md)
[![Platform](https://img.shields.io/badge/Host-Windows%2010%2F11%20%2B%20Hyper--V-0078d4.svg?style=flat-square)](docs/installation.md)
[![Guest](https://img.shields.io/badge/Guest-Ubuntu%20Server%20%28latest%29-e95420.svg?style=flat-square)](docs/installation.md)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-00cc66.svg?style=flat-square)](https://github.com/permissionBRICK/The-Construct/pulls)

[Features](#-features) · [Install](#-load-the-construct) · [Connect](#-jack-in) ·
[Configure](#-configure) · [Docs](#-documentation)

</div>

---

## ✨ Features

- 🤖 **Agents preinstalled, zero config** — Claude Code, Codex & Opencode, ready in
  unattended bypass mode: no permission prompts, real root shell.
- 🔒 **Sandboxed by design** — a throwaway Hyper-V VM stands between the agents and your PC.
- 🎛️ **One-screen control panel** — a VS Code extension on your host runs the whole VM:
  status, power, lifecycle, projects, updates, usage.
- ♻️ **Disposable, not amnesiac** — reinstall the VM and your agent config comes back on its
  own: instructions, memory, skills, subscription auth, git & MCP credentials.
- 📦 **Project profiles** — repos, SDKs, MCP servers, and setup commands in one JSON file,
  applied on every (re)provision.
- 🔁 **Config sync** — project requirements an agent records on the VM survive reinstall:
  git-versioned on the host, shareable with a teammate via a one-liner or a zip.
- 🎤 **Microphone passthrough** — voice input in the Claude Code extension works, even over
  Remote-SSH.
- 🔌 **Agents hand you links** — `construct expose 5173` on the VM opens that port on *your*
  PC — over an SSH tunnel the extension opens to that VM — and prints the URL to open.
- 🖥️ **Optional T3 Code, patched end to end** — build the selected stable or nightly
  server and Windows Desktop app inside the VM, then keep both updated together. The shared
  patch adds live voice input, Claude usage-limit recovery, and OpenCode task monitoring.
- 🤷 **It just works™** — system prompts make agents just install whatever tool they need for the task automatically

<sub>Bonus: auto-deploy MCP servers to all three agents · patched Claude Code extension for faster UI updates · no AI attribution by default.</sub>

## ⚡ Load the Construct

Open **PowerShell** on Windows and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

One command, zero VM interaction: it builds an Ubuntu autoinstall ISO, creates the Hyper-V
VM, installs Ubuntu unattended, provisions the full agent stack, and wires up your host's
SSH + VS Code config. Answer a few questions up front — then just hit connect.

> **Requirements:** Windows 10/11 with Hyper-V, and WSL — *yours*, as the user who
> installs — for the ISO build (`wsl --install -d Ubuntu` if missing). On a
> [remote host](docs/remote-host.md) the administrator builds that ISO once and the
> service reuses it; the service itself never runs WSL. Already have a VM? The installer offers
> **reprovision**, **reinstall** (with [config save & restore](docs/backup-restore.md)),
> and **export config**. Other paths — bundled ISO, BYO VM, no-admin — are in the
> [installation guide](docs/installation.md).

<div align="center">

<img src="https://i.imgur.com/GHg3XaD.png" alt="The Construct operator console — the one-screen VS Code control panel" width="100%">

<sub>*The operator console: lifecycle, live agent versions, mic passthrough, and project profiles on one screen.*</sub>

</div>

## 🔌 Jack in

The VM answers as `agent-vm.mshome.net` (alias `agent-vm`); every target below is wired up
during install:

| Client | How |
|--------|-----|
| **VS Code Remote-SSH** | Remote Explorer → `agent-vm` — Claude Code starts in bypass mode |
| **VS Code in the browser** | `http://localhost:8000/?tkn=<token>` — on by default, token-gated; its token auth only works from a localhost origin, so reach the port through a tunnel (`ssh -L 8000:127.0.0.1:8000 agent-vm`, or `construct expose 8000` on the VM) |
| **vscode.dev tunnel** | `https://vscode.dev/tunnel/<name>` — opt-in (`VSCODE_TUNNEL=true`) |
| **Codex App** | Add `agent-vm` as an SSH host |
| **Opencode** | `agent-vm.mshome.net:4096` — `opencode serve` autostarts |
| **T3 Code** | Opt in from Construct settings, then use its paired web UI or automatically installed and patched Windows Desktop app |
| **Windows file share** | `\\agent-vm.mshome.net\repo` — map to a drive with `-MountRepoShare true` |
| **Terminal** | `ssh agent-vm` — direct root access |

Ports go the other way too: an agent that starts a dev server runs `construct expose 5173`,
which opens that port on **your** PC and prints the link to hand you — see
[`construct expose`](docs/expose.md).

Details in [Remote access & services](docs/remote-access.md). The addresses above are the
*default* VM's; a second local VM has the same shape under its own name. A VM on a
[remote host](docs/remote-host.md) is different: the host service publishes **its SSH port**
(so `ssh <name>`, Remote-SSH and the Codex App workflow work under that instance's alias),
and nothing else is forwarded automatically — reach its web ports with
[`construct expose`](docs/expose.md), and note that the SMB share is a local-VM feature
today.

## ⚙️ Configure

Per-project setup is declared once in `projects/*.json` and reused on every (re)provision:

```jsonc
{
  "name": "customer-portal",
  "repos": [{ "url": "git@github.com:acme/customer-portal.git", "directory": "customer-portal" }],
  "sdks": { "node": "22" },
  "mcp": [{ "name": "context7", "type": "stdio", "command": "npx", "args": ["-y", "@upstash/context7-mcp"] }],
  "provisionCommands": ["npm ci", "cp -n .env.example .env || true"]
}
```

VM-level settings live at `/etc/construct/config.env` (agent name, projects, tools,
workspace root). Full reference: [Project profiles & configuration](docs/projects.md) and
[Provisioning](docs/provisioning.md). Optional features—including microphone passthrough and
the shared patched T3 Code server/Desktop build—are toggled in the
[Construct control panel](docs/control-panel.md#patched-t3-code-server--desktop-build).

## 🖧 Run it on a remote host

The VM does not have to live on your own PC. An admin installs the `constructd` service
once on a shared Hyper-V machine, and everyone creates and manages **their own** VMs on it
from the same installer and the same control panel:

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://buildbox.example.local:7462 -InstanceName work-vm
```

On a fresh machine the installer simply asks — *local Hyper-V* (the default, unchanged) or
*remote host*. The host builds the ISO, creates the VM and allocates an SSH port; your PC
still runs the provisioning, so your git credentials, agent auth and backups never transit
the service. Once it is up, the VM keeps running with your laptop closed — and a remote
install needs no administrator rights on your own machine, because nothing is created there.

Several VMs, local or remote, are just as fine: each is a named **instance** in a small
registry on your PC (`%LOCALAPPDATA%\The-Construct\instances.json`), and the control panel
gets a picker to switch the window between them. An install that only ever wants the one
classic local VM never sees any of this — the default instance is implicit, and a missing
registry means exactly today's behaviour.

See **[Remote host](docs/remote-host.md)** for the admin setup, authentication
(Kerberos or admin-issued tokens), certificate pinning and the idle policy, and
**[Field test](docs/field-test-remote-host.md)** for the step-by-step first run on a
domain.

## 🔐 Know the trade

The Construct swaps guardrails for isolation:

- **Bypass mode is sandbox-only** — root, no prompts. Great in a throwaway VM, a terrible
  idea anywhere holding real credentials or data.
- **The bootstrap key is burned** — a repo-committed keypair authorizes first contact and is
  removed after provisioning, but anyone with the repo can reach an *un-provisioned* VM.
- **Backups hold plaintext secrets** — treat the git-ignored `.construct-backup/` folder as
  a secret.
- **`code serve-web` is a root IDE over HTTP** — token-gated, but keep it on trusted
  networks.

## 📚 Documentation

| Guide | What's inside |
|-------|---------------|
| [Installation](docs/installation.md) | One-liner details, install options A–D, the autoinstall ISO |
| [Provisioning](docs/provisioning.md) | `Provision-AgentVM.ps1`, `provision.sh` + env vars, agent setup |
| [Manual setup](docs/manual-setup.md) | Blank Ubuntu VM to ready state by hand |
| [Project profiles & configuration](docs/projects.md) | `config.env`, profile schema, MCP servers, checkouts |
| [Remote access & services](docs/remote-access.md) | serve-web, tunnels, Codex remote, T3 Code, service lifecycle |
| [Remote host](docs/remote-host.md) | Running the VM on a shared Hyper-V host: the `constructd` service, auth, pinning, idle policy |
| [Field test checklist](docs/field-test-remote-host.md) | Step-by-step first run of the remote host on a domain, with what to check and where to look when it fails |
| [`construct expose`](docs/expose.md) | Self-serve port forwards from the VM, the spool/API contract, the idle heartbeat |
| [Hypervisor drivers](docs/drivers.md) | The backend contract (`hyperv-local`, `hyperv-remote`) and how to add one |
| [Control panel](docs/control-panel.md) | The VS Code operator console, optional voice and patched T3 Code features |
| [Backup & restore](docs/backup-restore.md) | Carrying agent config and auth across reinstalls |
| [Config sync](docs/config-sync.md) | How project profiles survive a reinstall and sync between VM and host |

## 📄 License

[MIT](LICENSE.md) © permissionBRICK

<div align="center">
<sub><i>Unfortunately, no one can be told what the Construct is. You have to <a href="#-load-the-construct">see it for yourself</a>.</i></sub>
</div>
