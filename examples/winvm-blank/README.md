# winvm-blank — scripted blank Windows VM on a Construct host

> **Template.** This is a working, field-proven example of an unattended Windows 11 child VM
> driven from a Linux primary: media repack, answer-file ISO, first-logon debloat, SSH channel,
> media release after install. It is the reference recipe for the host-side Windows guest
> feature described in [Windows guests](../../docs/child-vms.md#windows-guests).

For host-managed installation, media acquisition and activation, use
[Windows guests](../../docs/child-vms.md#windows-guests). The service embeds this rig's
client and server templates; the scripts below remain a standalone reference.

Creates an unattended Windows 11 Pro guest as a Construct child VM next to the
Linux agent VM, reachable over SSH, with the consumer bloat stripped out. It
installs nothing else: no build tools, no runtimes, no databases, no
application. That part is yours.

Extracted from an internal end-to-end test rig; all project-specific deployment,
build, database and test scripts were removed, names and credentials
genericized.

```text
Construct host
  Linux primary (these scripts)  ── SSH/SCP ──>  Windows child
```

## Quick start

```bash
cd vm
./setup-host.sh        # one-time, as root: host packages (idempotent)
./download-images.sh   # Win11 ISO via Fido (~6 GB), cached under $E2E_HOME/images
./create-vm.sh         # repack media, build unattend ISO, create + install (~30-60 min)
./post-install.sh      # optional: copy guest helpers, set 1920x1080
./vm.sh ssh 'hostname' # ad-hoc PowerShell in the guest
```

`create-vm.sh` is resumable: re-running it picks up an interrupted install,
`--force` deletes the VM and starts over. It polls for
`C:\provision\firstlogon.done`, then detaches both media slots so the answer
file cannot be applied again on a later boot, and writes
`C:\provision\install-complete.done`.

## Files

| File | Purpose |
| --- | --- |
| `vm/config.sh` | All configuration: backend selection, sizing, variant, paths, credentials. Sourced by everything else. |
| `vm/construct.sh` | Construct backend: inventory, LAN address discovery, `guest_ssh` / `guest_scp`. |
| `vm/setup-host.sh` | Host packages on Ubuntu (`genisoimage`, `xorriso`, `sshpass`, `jq`, `pwsh`, …). Idempotent, suitable as a Construct `provisionCommand`. |
| `vm/download-images.sh` | Fetches the Windows ISO. Win11 has no stable URL, so the official link is resolved via [Fido](https://github.com/pbatard/Fido). |
| `vm/prepare-media.sh` | Repacks the ISO with its own `efisys_noprompt.bin` (UEFI boot otherwise stops at "Press any key…") and builds the answer-file ISO. |
| `vm/create-vm.sh` | Creates the Construct child (UEFI gen 2, Secure Boot, TPM) and waits out the unattended install. |
| `vm/post-install.sh` | Copies `guest/*.ps1` into `C:\provision\scripts` and sets the desktop resolution. |
| `vm/vm.sh` | `start` / `stop` / `restart` / `status` / `ssh` / `scp` / `screenshot` / `web`. |
| `vm/test-backend.sh` | Self-test of the backend wiring against a mocked `construct` CLI. No VM needed. |
| `vm/unattend-win11/autounattend.xml` | UEFI/GPT answer file: generic Pro edition key, LabConfig bypasses, auto-logon, German locale with English UI. |
| `vm/unattend-win11/firstlogon.ps1` | **The debloat + enable script.** See below. |
| `vm/unattend-win11/README.md` | Why UEFI + TPM, and the BIOS specialize crash that made it look like a media bug. |
| `guest/run-interactive.ps1` | Runs a command in the *interactive* desktop session via a scheduled task. SSH sessions cannot drive the desktop, and their children die with the session. |
| `guest/set-resolution.ps1` | Sets 1920×1080. |
| `guest/send-keys.ps1` | SendKeys into a window that exposes nothing over UIA. |

## What `firstlogon.ps1` does

Runs once from the answer-file CD after the unattended install:

- **Enables**: OpenSSH server (incl. the inbound firewall rule, which the 25H2
  capability install does not create), PowerShell as the SSH shell, RDP, WinRM
  as fallback.
- **Removes**: every provisioned and installed Appx package except frameworks
  (VCLibs / UI.Xaml / WindowsAppRuntime / .NET Native), the Defender UI, App
  Installer and Get Help — removing Get Help reproducibly crashes
  `StartMenuExperienceHost` on this image. Edge stays: removal is fragile and
  it is inert when unused. OneDrive is uninstalled.
- **Turns off**: consumer feature/suggested-app delivery, Copilot and AI data
  analysis, widgets, taskbar chat and search box, telemetry + DiagTrack,
  second-chance OOBE nags, Windows Update, `WSearch`, `SysMain`, `MapsBroker`.
- **Defender**: realtime monitoring off and path exclusions (`C:\provision`,
  `C:\work`). Tamper Protection blocks outright removal on client SKUs.
- **Keeps the desktop alive**: no sleep, no monitor blanking, no hibernate, no
  lock screen, no screensaver, no inactivity lock — required for UI automation
  and console screenshots.

Adjust the `$keep` list and the exclusion paths to taste before the first run;
changing them afterwards means rebuilding, since this runs once.

## Configuration

Everything is env-overridable, e.g. `VM_RAM=8192 ./create-vm.sh`.

| Variable | Default | Meaning |
| --- | --- | --- |
| `E2E_HOME` | `/opt/winvm` | ISOs, disks, run state, results. Deliberately outside the repo. |
| `VM_NAME` | `winvm-win11` | Construct child name. |
| `VM_CPUS` | omitted | Optional override. Construct uses the host CPU allowance; QEMU uses all local CPUs. |
| `VM_RAM` | `12288` (MiB) | |
| `VM_DISK_SIZE` | `100G` | |
| `VM_LIFETIME` | `never` | Construct requires an explicit lifetime. |
| `VM_BACKEND` | `construct` if the CLI exists | `qemu` selects the nested-KVM path. |
| `WIN_VARIANT` | `win11` | `server2022` is QEMU-only. |

Guest credentials are `Administrator` / `WinVm-Lab!2026`, set in **both**
`vm/config.sh` and the two `autounattend.xml` files — change all of them
together. They are throwaway lab credentials: the guest has plaintext-auth
WinRM and a disabled firewall path for SSH; do not put it anywhere it can be
reached from outside the host.

The Win11 answer file uses the generic non-activating Pro edition key. Add a
real key for licensed permanent use.

## QEMU fallback

`vm/vm-qemu.sh`, `vm/create-qemu.sh` and the `vm/unattend/` (Server 2022) answer
file are the nested-KVM implementation behind the same entry points, selected
with `VM_BACKEND=qemu`. It needs `/dev/kvm` exposed to this VM and brings its
own port-forward layout, VNC/noVNC and swtpm setup. `vm/input.sh` and
`vm/type-de.py` drive synthetic mouse/keyboard input through QMP and are
QEMU-only. Delete all of these if you only ever use Construct.

## Not included

The original rig's build, sync, database, deployment, release and E2E-test
scripts, its guest provisioning (Visual Studio Build Tools, SQL Server) and
its disk-trim script (SQL-specific). A blank guest ends here; layer your own
provisioning on top of `post-install.sh`.
