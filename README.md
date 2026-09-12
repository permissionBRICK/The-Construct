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
unattended, provisions the agents and links up your PC for remote access through whatever application you like.

> **Requirements:** Windows 10 or 11 with local admin rights, some free disk space and RAM (4 GB RAM min recommended), and about 15 minutes.

<div align="center">
  
<img src="https://i.imgur.com/VHLWENf.png" alt="Construct integration into T3 Code" width="100%">

<sub>*Construct integrated directly into T3-Code, zero-click setup.*</sub>

<img src="https://i.imgur.com/GHg3XaD.png" alt="The Construct operator console, the one-screen VS Code control panel" width="100%">

<sub>*The control panel: lifecycle, agent versions, mic passthrough and project profiles on one screen.*</sub>

</div>

## 🔌 Jack in

The installer automatically links any tool you want, just enable it in the settings panel:

| Client | How |
|--------|-----|
| VS Code Remote-SSH | Remote Explorer, `agent-vm`. Claude Code starts in bypass mode. |
| T3 Code | Opt in from the Construct settings, then use its paired web UI or the Windows Desktop app. |
| Codex App | Add `agent-vm` as an SSH host. |
| Opencode | `agent-vm.mshome.net:4096`. `opencode serve` starts at boot. |
| VS Code in the browser | `http://localhost:8000/?tkn=<token>`. On by default and token-gated. The token only works from a localhost origin, so reach the port through a tunnel (`ssh -L 8000:127.0.0.1:8000 agent-vm`, or `construct expose 8000` on the VM). |
| vscode.dev tunnel | `https://vscode.dev/tunnel/<name>`. Opt in with `VSCODE_TUNNEL=true`. |
| Windows file share | `\\agent-vm.mshome.net\repo`. Map it to a drive with `-MountRepoShare true`. |
| Terminal | `ssh agent-vm`. Root access. |


## 🖧 Run it on a remote host

The VM can live on a shared Hyper-V machine instead of your PC. Install the service there once. After that, everyone creates and manages their own VMs remotely.

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://buildbox.example.local:7462 -InstanceName work-vm
```

Or just click the convert to Host button in the settings panel.

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
