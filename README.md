<div align="center">

<img src="assets/banner.svg" alt="The Construct" width="100%">

### *"This… is the Construct. Our loading program. We can load anything."*

**A disposable Ubuntu VM for unattended AI coding agents.**
Claude Code, Codex and Opencode run as root in bypass mode inside a Hyper-V VM.
They cannot touch your host PC.

[![License: MIT](https://img.shields.io/badge/License-MIT-00cc66.svg?style=flat-square)](LICENSE.md)
[![Platform](https://img.shields.io/badge/Host-Windows%2010%2F11%20%2B%20Hyper--V-0078d4.svg?style=flat-square)](docs/installation.md)
[![Guest](https://img.shields.io/badge/Guest-Ubuntu%20Server%20%28latest%29-e95420.svg?style=flat-square)](docs/installation.md)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-00cc66.svg?style=flat-square)](https://github.com/permissionBRICK/The-Construct/pulls)

[Features](#-features) · [Install](#-load-the-construct) · [Connect](#-jack-in) ·
[Configure](#-configure) · [Docs](#-documentation)

</div>

---

## ✨ Features

- 🤖 **Agents with full access.** Claude Code, Codex and Opencode run as root in bypass mode,
  with no permission prompts.
- 🔒 **Sandboxed.** A throwaway Hyper-V VM sits between the agents and your PC.
- ♻️ **Disposable.** Reinstall the VM and the agent configuration, auth and memory come back.
- 📦 **Project profiles.** Repos, SDKs, MCP servers and setup commands in one JSON file,
  applied on every provision.
- 🛜 **Local or hosted.** Run the VM on your own PC, or on a shared Hyper-V host and use it
  from anywhere.
- 🖥️ **T3 Code *plus*.** The VM and the Windows client get a patched T3 Code build with voice
  input, automatic resume after a session limit and Construct integration.
- 🤷 **It just works.** The agents are trained to install whatever tool a task needs.

<sub>And many... *many* more practical features and tweaks handcrafted through daily use.</sub>

## ⚡ Load the Construct

Open PowerShell on Windows and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

The script builds an Ubuntu autoinstall ISO, creates the Hyper-V VM, installs Ubuntu
unattended, provisions the agents and writes your PC's SSH and VS Code configuration. It asks
a few questions at the start and then runs on its own.

> **Requirements:** Windows 10 or 11 with local admin rights, free disk space, and about 15 minutes.

<div align="center">
  
<img src="https://i.imgur.com/VHLWENf.png" alt="Construct integration into T3 Code" width="100%">

<sub>*Construct integrated directly into T3-Code, zero-click setup.*</sub>

<img src="https://i.imgur.com/GHg3XaD.png" alt="The Construct operator console, the one-screen VS Code control panel" width="100%">

<sub>*The control panel: lifecycle, agent versions, mic passthrough and project profiles on one screen.*</sub>

</div>

## 🔌 Jack in

The VM answers as `agent-vm.mshome.net` (alias `agent-vm`). The installer sets up every
target below:

| Client | How |
|--------|-----|
| VS Code Remote-SSH | Remote Explorer, `agent-vm`. Claude Code starts in bypass mode. |
| VS Code in the browser | `http://localhost:8000/?tkn=<token>`. On by default and token-gated. The token only works from a localhost origin, so reach the port through a tunnel (`ssh -L 8000:127.0.0.1:8000 agent-vm`, or `construct expose 8000` on the VM). |
| vscode.dev tunnel | `https://vscode.dev/tunnel/<name>`. Opt in with `VSCODE_TUNNEL=true`. |
| Codex App | Add `agent-vm` as an SSH host. |
| Opencode | `agent-vm.mshome.net:4096`. `opencode serve` starts at boot. |
| T3 Code | Opt in from the Construct settings, then use its paired web UI or the Windows Desktop app. |
| Windows file share | `\\agent-vm.mshome.net\repo`. Map it to a drive with `-MountRepoShare true`. |
| Terminal | `ssh agent-vm`. Root access. |

Ports go the other way too. When an agent starts a dev server it runs `construct expose 5173`,
which opens that port on your PC and prints the link. See [`construct expose`](docs/expose.md).

More in [Remote access & services](docs/remote-access.md). The addresses above belong to the
default VM. A second local VM follows the same pattern under its own name. For a VM on a
[remote host](docs/remote-host.md), the host service publishes the SSH port, so `ssh <name>`,
Remote-SSH and the Codex App work under that instance's alias. You reach its web ports with
`construct expose`. The SMB share exists for local VMs only.

## ⚙️ Configure

Each project is declared once in `projects/*.json` and applied on every provision:

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
[Provisioning](docs/provisioning.md). You switch optional features on in the
[control panel](docs/control-panel.md#patched-t3-code-server--desktop-build), for example
microphone passthrough and the patched T3 Code build.

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

The Host Administration view, in VS Code and in the Companion, covers users and their
allowances (VM count, CPU, RAM, storage, lifetime, sharing), every VM with its settings,
media, jobs, configuration and host updates. The Maintenance tab installs host updates from
the published `main` releases. Guest provisioning stays per instance. From a VM on such a
host, `construct vm` creates [child VMs](docs/child-vms.md) for tests.

Several VMs, local or remote, are instances in a small registry on your PC
(`%LOCALAPPDATA%\The-Construct\instances.json`). The control panel and the Companion switch
between them. With a single local VM the registry is not needed.

[Remote host](docs/remote-host.md) covers the admin setup, authentication (Kerberos or
admin-issued tokens), certificate pinning and the idle policy.
[Field test](docs/field-test-remote-host.md) walks through the first run on a domain. Host
administration and child VMs have Linux tests and still need the
[Hyper-V field test](docs/field-test-host-admin.md) before rollout.

## 📚 Documentation

| Guide | What's inside |
|-------|---------------|
| [Installation](docs/installation.md) | The one-liner in detail, install options A to D, the autoinstall ISO |
| [Provisioning](docs/provisioning.md) | `Provision-AgentVM.ps1`, `provision.sh` and its environment variables, agent setup |
| [Manual setup](docs/manual-setup.md) | From a blank Ubuntu VM to a ready one by hand |
| [Project profiles & configuration](docs/projects.md) | `config.env`, the profile schema, MCP servers, checkouts |
| [Remote access & services](docs/remote-access.md) | serve-web, tunnels, Codex remote, T3 Code, service lifecycle |
| [Remote host](docs/remote-host.md) | The `constructd` service on a shared Hyper-V host: setup, auth, pinning, idle policy |
| [Field test checklist](docs/field-test-remote-host.md) | First run of a remote host on a domain, step by step |
| [Host-admin field test](docs/field-test-host-admin.md) | Hyper-V validation of host administration and child VMs |
| [`construct expose`](docs/expose.md) | Port forwards from the VM to your PC or the host |
| [Child VMs](docs/child-vms.md) | `construct vm`: create, run, share and remove test VMs |
| [Hypervisor drivers](docs/drivers.md) | The backend contract and how to add one |
| [Construct Companion](docs/companion.md) | The Windows tray app: install, settings, troubleshooting |
| [Control panel](docs/control-panel.md) | The VS Code operator console |
| [Backup & restore](docs/backup-restore.md) | Carrying agent configuration and auth across reinstalls |
| [Config sync](docs/config-sync.md) | How project profiles survive a reinstall and sync between VM and PC |

## 📄 License

[MIT](LICENSE.md) © permissionBRICK

<div align="center">
<sub><i>Unfortunately, no one can be told what the Construct is. You have to <a href="#-load-the-construct">see it for yourself</a>.</i></sub>
</div>
