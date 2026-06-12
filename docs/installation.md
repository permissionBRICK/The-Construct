# Installation

All the ways to get a Construct VM running on a Windows 10/11 host with Hyper-V — from the
fully automated one-liner down to a no-admin manual setup. One script builds the VM, installs
Ubuntu unattended, provisions the agent stack, and wires up your host for SSH + VS Code — no
manual VM interaction.

## The one-liner

Open **PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
```

This downloads the latest repo and launches the guided installer (`Auto-Install.ps1`): it
elevates to Administrator (required for Hyper-V), builds the Ubuntu autoinstall ISO, then
creates and provisions the VM. You answer a few questions up front (RAM, disk size,
projects); everything after that runs unattended.

If the VM already exists, you get a menu:

- **Reprovision** — re-runs the config but keeps your data.
- **Reinstall / Redownload** — wipes the VM and reinstalls it from ISO, offering first to
  save the current agent config and auto-restore it afterwards, and warning about any
  uncommitted/unpushed work in the repos.
- **Export config** — saves the current agent config to the host without reprovisioning or
  rebooting the VM.

See [Saving & restoring config](backup-restore.md) for what gets saved and restored.

> Installs from `permissionBRICK/The-Construct`; pass `-Repo owner/name` for a fork.
> Requires WSL with a Linux distro for the ISO build — if it's missing, the installer tells
> you to run `wsl --install -d Ubuntu`, reboot, and re-run. The one-liner sets execution
> policy `Bypass` for its own process only.

## Option A — from scratch (`Auto-Install.ps1`)

The one-liner above, run from a local checkout. Downloads Ubuntu Server, builds the
autoinstall ISO, then creates and provisions the VM:

```powershell
.\Auto-Install.ps1
```

Override the release or supply your own source ISO with `-UbuntuRelease 24.04`,
`-IsoPath …`, or `-IsoUrl …`; add `-SkipCreateVm` to only build the ISO. Same WSL
requirement and reprovision/reset menu as the one-liner above.

## Option B — full bundle (repo + ISO together)

The distributed bundle is this repo with an autoinstall ISO (`agent-vm-autoinstall.iso`)
included in the zip.

1. Extract anywhere.
2. Right-click **`Create-AgentVM.ps1`** → **Run with PowerShell** (it self-elevates).

The script runs the whole flow unattended (~5 minutes for the install, plus provisioning).

> The host needs Hyper-V and the OpenSSH client; `Create-AgentVM.ps1` checks for both and
> installs them if missing (enabling Hyper-V may need one reboot — re-run afterwards).

## Option C — repo and ISO downloaded separately

Run `Create-AgentVM.ps1` and pick an ISO in the file dialog. An `*autoinstall*.iso`
triggers the unattended end-to-end flow; a plain Ubuntu Server ISO does a normal manual
install instead (then run `Provision-AgentVM.ps1` yourself once the VM is up).

## Option D — no admin access

If you'd rather not give the script admin rights (fair enough), set up the VM yourself and
run the provisioner directly. You need an Ubuntu Server VM with at least 4 GB RAM and
dynamic memory **disabled** (it breaks something in the kernel). For a manual install from
a plain Ubuntu ISO, configure:

- Install type: minimal
- Hostname: `agent-vm`
- User / password: `agent` / `agent`
- OpenSSH enabled

Then run `Provision-AgentVM.ps1` — it needs no admin access. See
[Provisioning](provisioning.md) for details, or [Manual setup](manual-setup.md) to skip
the Windows scripts entirely.

## What the automated flow does

| Step | Script | Action |
|------|--------|--------|
| 1 | `Create-AgentVM.ps1` | Ensures OpenSSH + Hyper-V, creates a Gen-2 VM (half host RAM ≤ 24 GB, Secure Boot off), boots the autoinstall ISO, waits for SSH, then calls `Provision-AgentVM.ps1`. |
| 2 | autoinstall ISO (built by `bin/build-autoinstall-iso.sh`) | Installs a blank **minimized** Ubuntu unattended: user/host preset, SSH on, the committed bootstrap key authorized, and a console hint to run the provisioner. |
| 3 | `Provision-AgentVM.ps1` | Connects (re-using the saved root key when re-provisioning an existing VM, otherwise the bootstrap key), uploads the repo, runs `bin/provision.sh`, obtains the VM's root key, removes the bootstrap key, configures the host's `~\.ssh\` + VS Code, and reboots the VM. |
| 4 | `bin/provision.sh` (on the VM) | `bootstrap.sh` → write `config.env` → root SSH key → install AI tools → generate runtime config → install selected projects' runtimes → start the `construct` service → install the VS Code CLI/server (and, when selected, deploy + register the `code tunnel`). |

Defaults line up across all of these: user `agent`, password `agent`, hostname `agent-vm`
(→ `agent-vm.mshome.net` on Hyper-V NAT), and `root` as the VS Code connection user.

## Building the autoinstall ISO

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
