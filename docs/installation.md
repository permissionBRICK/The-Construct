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
projects) through full-screen terminal menus — one screen per choice — and once the
"all set" banner appears, everything after that runs unattended with normal log output.

> On a machine that has **no** Construct VM yet, the very first screen asks where the VM
> should run — *Local Hyper-V install* (preselected) or *Remote host install*. Press Enter
> and everything from there is the install described on this page. The remote path is a
> separate story: [Several VMs, and VMs on another host](#several-vms-and-vms-on-another-host).
> An existing install never sees the question.

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
> policy `Bypass` for its own process only. `install.ps1` doesn't declare most params
> itself — it forwards unknown args straight through to `Auto-Install.ps1` generically, so
> new Auto-Install options (like the config-sync ones below) work through the one-liner for
> free.

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

Then run `Provision-AgentVM.ps1` — it needs no admin access. It defaults to
`-VmHost agent-vm.mshome.net -HostAlias agent-vm -SshPort 22`; point it at a VM that lives
somewhere else with

```powershell
.\Provision-AgentVM.ps1 -VmHost 192.168.1.50 -SshPort 22 -HostAlias my-vm -LocalKeyName construct_my-vm_ed25519
```

See [Provisioning](provisioning.md) for the full parameter list, or
[Manual setup](manual-setup.md) to skip the Windows scripts entirely.

## Option E — add project config (`-Action add-config`)

Bring project requirements from a shared config repo or a local folder onto a Construct
install — new or existing — in one command:

```powershell
.\Auto-Install.ps1 -Action add-config -ConfigRepo https://git.company.com/vm-config.git -ImportConfigs customer-portal,billing-api
```

- **Construct not installed** → runs the full install (build ISO, create VM, provision)
  with the imported configs already selected.
- **Construct already installed** → an additive reprovision: existing project selections
  and checked-out repos are kept (`PROJECTS` becomes the union of what's already there and
  what you imported), only the new repos clone.

Params:

| Param | Meaning |
|-------|---------|
| `-Action add-config` | Trigger this mode |
| `-ConfigRepo <url>` | Remote source — clones to a local staging cache and imports from it |
| `-ConfigDir <path>` | Local source — imports config files from `<path>\projects\*.json` |
| `-ImportConfigs a,b` | Which config files to import, by name. Omit it to import **every** config file discovered in the source — this applies to both `-ConfigRepo` and `-ConfigDir`; pass names only to cherry-pick a subset |
| `-AutoResolve ours\|theirs` | Non-interactive conflict resolution if the host config repo has a pending merge conflict when this runs (see [Config sync](config-sync.md#8-conflict-resolution)) |

Name collisions with an existing profile of different provenance are a hard error naming
the collision — this path never silently overwrites. See [Config sync](config-sync.md) for
the full import/collision/provenance model.

### Sharing a config as a one-liner

Piping to `iex` can't carry arguments, so a shareable one-liner that needs params uses the
scriptblock idiom instead:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1))) `
    -ConfigRepo https://git.company.com/vm-config.git -ImportConfigs customer-portal,billing-api -Action add-config
```

The control panel's **Share** action (see [Control panel](control-panel.md)) generates
exactly this form for you, fork-correct (`-Repo`/`-Ref` filled in when you're on a fork),
copied to the clipboard. A selection that includes local-only (not remote-backed) profiles
is shared as a zip instead, since there's no URL to point the command at.

### Git on the host

Host `git` powers config sync and remote config-repo clones, but it's **never required**
just to install Construct:

| When | Needs host git? |
|---|---|
| Plain install, no config params | No — never prompted |
| `-ConfigRepo` | Yes, at install time — interactive installs prompt to install it (winget); unattended runs attempt it silently and abort loudly on failure |
| `-ConfigDir` / a shared zip bundle | No — plain file copy; picked up by sync later once git exists |
| Ongoing sync between the VM and host | Yes, for merging — without it, sync degrades to the old additive-only [backup/restore](backup-restore.md) behavior |

Whenever git first becomes available, the next sync tick lazily initializes the host config
repo — there's no separate migration step. See
[Config sync §10](config-sync.md#10-git-on-the-host--never-required-strictly-an-upgrade) for
the full table.

## Several VMs, and VMs on another host

Everything above describes the **default instance** — one local Hyper-V VM named `Agent-VM`,
answering as `agent-vm.mshome.net`. Nothing on this page changes if that is all you want:
the registry below is optional, a missing one *means* the default instance, and no prompt
or parameter about instances appears.

### The instance registry

`%LOCALAPPDATA%\The-Construct\instances.json` (next to the existing `config\` directory)
names the VMs this PC knows about. It is read by both halves of Construct —
`lib/AgentVm.Instances.ps1` for the scripts and `extension/src/instances.js` for the control
panel — from one schema with one set of rules:

```jsonc
{
  "version": 1,
  "defaultInstance": "agent-vm",
  "instances": {
    "agent-vm": {                        // implicit: synthesized when absent
      "backend": "hyperv-local",
      "vmName": "Agent-VM",
      "sshHost": "agent-vm.mshome.net", "sshPort": 22,
      "hostAlias": "agent-vm",
      "keyName": "agent_vm_ed25519",
      "configBranch": "vm",
      "scriptsDir": null                 // null = the newest install (today's detection)
    },
    "work-vm": {
      "backend": "hyperv-remote",
      "service": { "url": "https://buildbox.example.local:7462", "auth": "negotiate" },
      "vmName": "work-vm",
      "sshHost": "buildbox.example.local", "sshPort": 2201,
      "hostAlias": "work-vm",
      "keyName": "construct_work-vm_ed25519",
      "configBranch": "vm-work-vm",
      "owner": "DOMAIN\\alice"
    }
  }
}
```

- **A missing file, an unreadable file or a missing entry all mean "exactly today's
  behaviour"**: the `agent-vm` default is synthesized in memory and nothing is written.
- Instance names are one lowercase DNS label — `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`, and
  **not** starting with the reserved prefix `construct-`. They end up verbatim in file
  names, SSH aliases and git refs, so the name must *end* alphanumeric too (`work-` would
  derive the endpoint `work-.mshome.net`, which is not a host name), it is 1–63 characters
  (the DNS label's own limit — the derived `construct_<name>_ed25519` is covered by the
  registry's longer key-file bound), and the `construct-` prefix is reserved because that
  is the namespace the derived key and branch names live in. The same rule is enforced by
  the extension, `Auto-Install.ps1`/`Create-AgentVM.ps1` and the host service.
- Everything an entry omits is **derived from its name**: alias `<name>`, key
  `construct_<name>_ed25519`, config-sync branch `vm-<name>`, VM name `<name>`, host
  `<name>.mshome.net`, port 22. The default instance keeps its historical literals
  (`agent-vm`, `agent_vm_ed25519`, branch `vm`, `Agent-VM`).
- Reading is **fail-closed**. An entry whose identity fields are unusable (a hostile host
  name, a reserved Windows device name as a key file, a branch git would refuse) is skipped
  whole rather than half-applied; a `backend` that is present but not a usable id — or that
  differs from a real id only by case — makes the entry unloadable, because the two readers
  would disagree about what it is. An unknown backend (`proxmox`) is kept but reported: its
  driver dispatch degrades, so rebuild and checkpoint actions are simply unavailable.
- Two more rules hold per backend. A `hyperv-local` entry must carry exactly the identity
  derived from its name — anything else does not describe a customised VM, it *targets a
  different machine* than the one a rebuild would create. A `hyperv-remote` entry must state
  its `sshHost` (only the host service knows the endpoint; deriving `<name>.mshome.net`
  would aim ssh at an unrelated machine on your own LAN) and its `vmName` must equal the
  instance name (the service addresses the VM by that name, and so does a rebuild).
- Identities must be **unique across the registry**: `vmName`, `hostAlias`, `keyName`,
  `configBranch`, and the endpoint as the **composite `(sshHost, sshPort)`**. Two entries
  sharing one are two names for one machine, so both are dropped. The composite matters for
  remote VMs: every VM on one service host shares that host's address and is told apart by
  the SSH forward it was allocated, so a shared host is fine and only a shared *port* is a
  clash. A non-default entry may also not claim any of the default instance's values.

The control panel surfaces registry problems as a toast and in its log, so a hand-edited
file that does not load says why.

### A second local VM

```powershell
.\Auto-Install.ps1 -InstanceName build-vm
```

Every derived value follows that name: guest hostname `build-vm`, `build-vm.mshome.net`,
Hyper-V VM `build-vm`, SSH alias `build-vm`, key `construct_build-vm_ed25519`, config-sync
branch `vm-build-vm`. `-VmName build-vm` does exactly the same thing and keeps working —
the two are one name, and passing both with different values is an error rather than a
silent choice between two machines. `Create-AgentVM.ps1 -InstanceName build-vm` takes it
too, for the bundle install (Option B/C).

**The installer writes the registry entry**, through the same library both readers use, so
the control panel lists the VM and can switch to it without any hand editing. A reinstall,
a redownload or a reprovision of that VM keeps its entry.

A default-only install still writes **no** `instances.json` at all — a missing file *is*
the `agent-vm` instance. The file appears when the second VM is created, and then carries
both entries.

### Targeting one VM by name

The scripts a second VM needs take **`-InstanceName <name>`** instead of four identity
arguments, and read the endpoint, alias, port, key file, config-sync branch — and, for a
VM on a host service, that service's URL — out of the registry entry:

```powershell
.\Provision-AgentVM.ps1 -InstanceName build-vm      # reprovision it
.\Update-T3Code.ps1     -InstanceName build-vm      # rebuild its patched T3 Code
.\Set-AgentVmCheckpoints.ps1 -InstanceName build-vm -Enabled false
.\Get-AgentUsage.ps1    -InstanceName build-vm
```

The explicit `-VmHost` / `-HostAlias` / `-SshPort` / `-LocalKeyName` parameters are still
there for a BYO or manual setup and are used as given. Passing one *together* with
`-InstanceName` is only allowed when it agrees with the entry; a disagreement stops the run
and names both values, because there is no safe way to guess which machine was meant. An
unknown name is **always** an error listing the names this PC does know — passing an
explicit endpoint alongside it does not make the name mean something else; a BYO or manual
setup uses the explicit parameters *without* a name. `-InstanceName agent-vm` on a PC with
no registry file resolves to exactly today's literals.

### A VM on a remote host

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote `
    -ServiceUrl https://buildbox.example.local:7462 -ServiceAuth negotiate `
    -InstanceName work-vm -VmCpuCount 4 -VmMemoryGB 8 -VmDiskGB 60 -Projects default
```

The host service creates the VM from the autoinstall ISO its administrator built at install
time (one generic ISO serves every VM: the guest takes its hostname from the Hyper-V VM name
at first boot) and allocates its SSH forward; your PC then
runs the ordinary `Provision-AgentVM.ps1` over that endpoint, so your git credentials, agent
auth and backups never transit the service. The registry entry is written by the installer
as soon as the endpoint is known. This path needs **no administrator rights on your PC** —
nothing is created locally.

Passing any of `-Backend` / `-ServiceUrl` / `-InstanceName` skips the mode prompt, as do
`-VmName`, `-Action`, `-FromPanel` and an existing default instance. The whole flow — admin
setup, authentication, certificate pinning, the idle policy — is in
[Remote host](remote-host.md); [Field test](field-test-remote-host.md) walks the first run
end to end.

## What the automated flow does

| Step | Script | Action |
|------|--------|--------|
| 1 | `Create-AgentVM.ps1` | Ensures OpenSSH + Hyper-V, creates a Gen-2 VM (half host RAM ≤ 24 GB, vCPUs = all host logical processors, Secure Boot off, automatic checkpoints off, nested virtualization exposed when the host supports it), boots the autoinstall ISO, waits for SSH, then calls `Provision-AgentVM.ps1`. Every hypervisor call goes through the [driver contract](drivers.md) (`-Backend`, default `hyperv-local`) rather than raw cmdlets. |
| 2 | autoinstall ISO (built by `bin/build-autoinstall-iso.sh`) | Installs a blank **minimized** Ubuntu unattended: user/host preset, SSH on, the committed bootstrap key authorized, and a console hint to run the provisioner. |
| 3 | `Provision-AgentVM.ps1` | Connects (re-using the saved root key when re-provisioning an existing VM, otherwise the bootstrap key), uploads the repo, runs `bin/provision.sh`, obtains the VM's root key, removes the bootstrap key, configures the host's `~\.ssh\` + VS Code, and reboots the VM on a full install/reinstall (a reprovision leaves the running VM up unless a reboot is pending). |
| 4 | `bin/provision.sh` (on the VM) | `bootstrap.sh` → write `config.env` → root SSH key → install AI tools → generate runtime config → install selected projects' runtimes → start the `construct` service → install the VS Code CLI/server (and, when selected, deploy + register the `code tunnel`). |

Defaults line up across all of these: user `agent`, password `agent`, hostname `agent-vm`
(→ `agent-vm.mshome.net` on Hyper-V NAT), and `root` as the VS Code connection user. A VM
created under another name derives its own set from that name (see
[Several VMs](#several-vms-and-vms-on-another-host)); the seed user and password are the
same either way.

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

Because the hostname is baked into the ISO, each VM gets its own: `Create-AgentVM.ps1`
attaches `-AutoinstallIso <path>` when it is given one, otherwise a named VM looks for
`<name>-autoinstall.iso` next to the script and **refuses to guess another instance's ISO**,
while the default VM keeps the historical "newest `*autoinstall*.iso`" discovery. On a
[remote host](remote-host.md) the service builds the per-VM ISO itself, in WSL, from the
same script.

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
