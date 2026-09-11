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

- 🤖 **Agents preconfigured with full access** — Claude Code, Codex & Opencode, ready in
  unattended bypass mode: no permission prompts, root shell.
- 🔒 **Sandboxed by design** — a throwaway Hyper-V VM stands between the agents and your PC.
- 🎛️ **One-screen control panel** — a VS Code extension on your host runs the whole VM:
  status, power, lifecycle, projects, updates, usage.
- 🟢 **[Construct Companion](docs/companion.md)** — a Windows tray app that keeps port
  forwards, notifications and the microphone alive with VS Code closed, and carries the
  same control panel.
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
- 🧪 **Disposable child VMs** — on a shared host, `construct vm create` gives an agent a
  short-lived Windows or Linux test machine booted from an ISO, with no host credentials
  inside.
- 🛠️ **Shared-host administration** — admins manage users, allowances, VMs, media, jobs,
  configuration and host updates from one VS Code view. Users see their own VMs.
- 🖥️ **T3 Code, patched** — the VM and the Windows client get a patched T3 Code build:
  voice input, automatic resume after a session limit, Construct integration.
- 🤷 **It just works™** — agents are told to install whatever tool a task needs.

<sub>Bonus: auto-deploy MCP servers to all three agents · patched Claude Code extension for faster UI updates · no AI attribution by default.</sub>

## ⚡ Load the Construct

Open **PowerShell** on Windows and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

The script automatically builds an Ubuntu autoinstall ISO, creates the Hyper-V
VM, installs Ubuntu unattended, provisions the full agent stack, and wires up your host's
SSH + VS Code config. After some initial questions the setup runs completely unattended.

> **Requirements:** Windows 10/11 with local admin access, and some free disk space as well as about 15 min time.

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
| **T3 Code** | Opt in from Construct settings, then use its paired web UI or the Windows Desktop app |
| **Windows file share** | `\\agent-vm.mshome.net\repo` — map to a drive with `-MountRepoShare true` |
| **Terminal** | `ssh agent-vm` — direct root access |

Ports go the other way too. When an agent starts a dev server it runs `construct expose 5173`,
which opens that port on **your** PC and prints the link. See [`construct expose`](docs/expose.md).

More in [Remote access & services](docs/remote-access.md). The addresses above belong to the
default VM; a second local VM follows the same pattern under its own name. For a VM on a
[remote host](docs/remote-host.md), the host service publishes the SSH port, so `ssh <name>`,
Remote-SSH and the Codex App work under that instance's alias. Its web ports are reached with
`construct expose`. The SMB share exists for local VMs only.

## ⚙️ Configure

Per-project setup is declared once in `projects/*.json` and reused on every (re)provision:

```jsonc
{
  "name": "customer-portal",
  "repos": [{ "url": "git@github.com:acme/customer-portal.git", "directory": "customer-portal" }],
  "sdks": { "node": "22" },
  "mcp": [{ "name": "context7", "type": "stdio", "command": "npx", "args": ["-y", "@upstash/context7-mcp"] }],
  "provisionCommands": ["npm install", "if [ ! -e .env ]; then cp .env.example .env; fi"]
}
```

VM-level settings live in `/etc/construct/config.env` (agent name, projects, tools,
workspace root). Reference: [Project profiles & configuration](docs/projects.md) and
[Provisioning](docs/provisioning.md). Optional features such as microphone passthrough and
the patched T3 Code build are switched on in the
[control panel](docs/control-panel.md#patched-t3-code-server--desktop-build).

## 🖧 Run it on a remote host

The VM can live on a shared Hyper-V machine instead of your PC. An admin installs the
`constructd` service there once. After that, everyone creates and manages their own VMs on
it from the same installer and the same control panel:

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://buildbox.example.local:7462 -InstanceName work-vm
```

On a fresh machine the installer asks: local Hyper-V, or remote host. With a remote host:

- The host builds the ISO, creates the VM and assigns an SSH port.
- Your PC still runs the provisioning, so git credentials, agent auth and backups never
  pass through the service.
- No administrator rights are needed on your PC.
- The VM keeps running with your laptop closed.

Administrators get **The Construct: Host Administration** in VS Code and in the Companion:
users and their allowances (VM count, CPU, RAM, storage, lifetime, sharing), every VM with
its settings, media, jobs, configuration and host updates. Host updates come from the
published `main` releases in the Maintenance tab; guest provisioning stays per instance.
From a VM on such a host, `construct vm` creates [child VMs](docs/child-vms.md) for tests.

Several VMs, local or remote, are instances in a small registry on your PC
(`%LOCALAPPDATA%\The-Construct\instances.json`). The control panel and the Companion switch
between them. A single local VM never sees any of this.

Admin setup, authentication (Kerberos or admin-issued tokens), certificate pinning and the
idle policy: [Remote host](docs/remote-host.md). First run on a domain:
[Field test](docs/field-test-remote-host.md). Host administration and child VMs are tested
on Linux and still need the [Hyper-V field test](docs/field-test-host-admin.md) before
rollout.

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
| [Remote host](docs/remote-host.md) | The `constructd` service on a shared Hyper-V host: setup, auth, pinning, idle policy |
| [Field test checklist](docs/field-test-remote-host.md) | First run of a remote host on a domain, step by step |
| [Host-admin field test](docs/field-test-host-admin.md) | Hyper-V validation of host administration and child VMs |
| [`construct expose`](docs/expose.md) | Port forwards from the VM to your PC or the host |
| [Child VMs](docs/child-vms.md) | `construct vm`: create, run, share and remove test VMs |
| [Hypervisor drivers](docs/drivers.md) | The backend contract and how to add one |
| [Construct Companion](docs/companion.md) | The Windows tray app: install, settings, troubleshooting |
| [Control panel](docs/control-panel.md) | The VS Code operator console |
| [Backup & restore](docs/backup-restore.md) | Carrying agent config and auth across reinstalls |
| [Config sync](docs/config-sync.md) | How project profiles survive a reinstall and sync between VM and host |

## 📄 License

[MIT](LICENSE.md) © permissionBRICK

<div align="center">
<sub><i>Unfortunately, no one can be told what the Construct is. You have to <a href="#-load-the-construct">see it for yourself</a>.</i></sub>
</div>
