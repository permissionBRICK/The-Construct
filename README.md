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

> **Requirements:** Windows 10/11 with Hyper-V, and WSL for the ISO build
> (`wsl --install -d Ubuntu` if missing). Already have a VM? The installer offers
> **reprovision**, **reinstall** (with [config save & restore](docs/backup-restore.md)),
> and **export config**. Other paths — bundled ISO, BYO VM, no-admin — are in the
> [installation guide](docs/installation.md).

<div align="center">

<img src="https://i.imgur.com/Z1YKRJr.png" alt="The Construct operator console — the one-screen VS Code control panel" width="100%">

<sub>*The operator console: lifecycle, live agent versions, mic passthrough, and project profiles on one screen.*</sub>

</div>

## 🔌 Jack in

The VM answers as `agent-vm.mshome.net` (alias `agent-vm`); every target below is wired up
during install:

| Client | How |
|--------|-----|
| **VS Code Remote-SSH** | Remote Explorer → `agent-vm` — Claude Code starts in bypass mode |
| **VS Code in the browser** | `http://agent-vm.mshome.net:8000/?tkn=<token>` — on by default, token-gated |
| **vscode.dev tunnel** | `https://vscode.dev/tunnel/<name>` — opt-in (`VSCODE_TUNNEL=true`) |
| **Codex App** | Add `agent-vm` as an SSH host |
| **Opencode** | `agent-vm.mshome.net:4096` — `opencode serve` autostarts |
| **Windows file share** | `\\agent-vm.mshome.net\repo` — map to a drive with `-MountRepoShare true` |
| **Terminal** | `ssh agent-vm` — direct root access |

Details in [Remote access & services](docs/remote-access.md).

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
[Provisioning](docs/provisioning.md).

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
| [Remote access & services](docs/remote-access.md) | serve-web, tunnels, Codex remote, service lifecycle |
| [Control panel](docs/control-panel.md) | The VS Code operator console, module by module |
| [Backup & restore](docs/backup-restore.md) | Carrying agent config and auth across reinstalls |
| [Config sync](docs/config-sync.md) | How project profiles survive a reinstall and sync between VM and host |

## 📄 License

[MIT](LICENSE.md) © permissionBRICK

<div align="center">
<sub><i>Unfortunately, no one can be told what the Construct is. You have to <a href="#-load-the-construct">see it for yourself</a>.</i></sub>
</div>
