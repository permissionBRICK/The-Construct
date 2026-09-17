# Windows child VMs: OS type, unattended install, license keys

Status: **draft for discussion**, 2026-09-17. Not dispatched. Open decisions in §6.

## 1. Goal

An agent asks for a Windows child VM and gets one that installs itself, comes up with SSH,
and is activated with a key the admin put into a pool on the host, without the agent ever
handling the key. Without a pool key the guest installs with the generic edition key and runs
the normal grace period. The host can tell whether the install finished and whether the key
activated. Linux children stay as they are.

## 2. The proven recipe (from `examples/winvm-blank`)

The example in `examples/winvm-blank/` is a working rig on a Hyper-V host; the feature
generalises it. What it established, and what the host-side implementation must keep:

- **UEFI + TPM 2.0 is mandatory for Windows 11 media.** On legacy BIOS the 25H2 media crashes
  in the offline specialize pass (`tssysprep.dll`, 0xc0000005) and it looks like a media bug.
  Gen 2, Microsoft Secure Boot template, TPM on, GPT layout in the answer file.
- **Stock media stops at "Press any key to boot from CD".** The ISO is repacked once with its
  own `efi/microsoft/boot/efisys_noprompt.bin` (the media stores files in UDF, so mount and
  copy, then `xorriso -as mkisofs … -eltorito-platform efi -e efi/microsoft/boot/efisys_noprompt.bin`).
  Cache the repacked ISO next to the original (`prepare-media.sh`).
- **Boot order `disk,installMedia`** avoids a reboot loop: once setup's first phase has written
  the Windows Boot Manager NVRAM entry, the disk wins; before that the empty disk falls through
  to the DVD. Requires the firmware variables to persist per VM (Hyper-V does; Proxmox via the
  EFI disk).
- **The answer file is the auxiliary ISO** (`autounattend.xml` + `firstlogon.ps1`,
  `genisoimage -J -R -V UNATTEND`). Windows Setup finds it on any removable medium.
  `FirstLogonCommands` runs `firstlogon.ps1` straight from the CD (`for %d in (D E F G H)`).
- **No RDP/TerminalServices components in the answer file** (same specialize crash on 25H2);
  RDP is switched on in the first-logon script instead. SSH is the primary channel.
- **First logon** installs OpenSSH server (and its firewall rule, which the 25H2 capability
  install omits), sets PowerShell as the SSH shell, enables RDP and WinRM, strips consumer
  Appx (keeping frameworks, Get Help, the Defender UI, App Installer), turns off consumer
  content, Copilot, widgets, telemetry, Windows Update, search indexing, and keeps the desktop
  awake for UI automation. It ends by writing `C:\provision\firstlogon.done`.
- **Completion and hygiene.** The controller polls `firstlogon.done` over SSH, stops the VM,
  detaches **both** media slots so the answer file can never be applied again, starts it, and
  writes `install-complete.done`. Re-running resumes; `--force` recreates.
- **Credentials** live in the answer file (built-in Administrator, auto-logon) and in the
  controller config; the example uses throwaway lab credentials and documents that.
- **The generic edition key** (`VK7JG-NPHTM-C97JM-9MPGT-3V66T` for Pro) selects the edition
  and does not activate; the LabConfig bypass keys are belt and braces only.
- **The ISO itself** has no stable URL; Fido resolves the official download. Server 2022
  evaluation media has a stable link and 180 days.

## 3. Design (proposed)

1. **OS type on child create**: `--os linux|windows|other`; `windows` implies the current
   `--preset windows` firmware and unlocks the unattended options below.
2. **Host-built auxiliary ISO.** `construct vm create --os windows --iso … --unattend-*`
   parameters (admin password, hostname, locale, time zone, extra first-logon script, extra
   files) are rendered by the host into `autounattend.xml` + `firstlogon.ps1` from the template
   in `examples/winvm-blank/vm/unattend-win11/`, the key from the pool is inserted on the host,
   the ISO is built on the host (`xorriso`/`genisoimage` on Linux, the existing ISO builder
   seam on Windows) and attached as auxiliary media. The agent can still supply its own
   auxiliary ISO instead, in which case the host injects nothing.
3. **Media repack on the host.** The no-prompt repack becomes a host-side media operation
   (`construct vm media prepare-windows <id>`), cached per media item, so agents do not need
   `xorriso` and 8 GB of scratch space in the primary.
4. **Key pool.** Admin adds keys (edition, kind retail/MAK/KMS-client, activation budget for
   MAK, notes) through the host admin panel or CLI. Encrypted at rest; the API returns only the
   last five characters. Assignment records per VM incarnation with states assigned, installed,
   activated, failed; released on delete (a MAK activation does not return). Auto-assign by
   edition, manual assign from the panel, audit entries. Default when the pool has no matching
   key: the generic edition key, i.e. trial as normal; on a KMS domain the generic key is also
   the right answer.
5. **Guest write-back.** The generated first-logon script reports install stage, activation
   state (`slmgr /dli` partial key and status), hostname and addresses through the platform's
   guest channel: Hyper-V KVP (Data Exchange, already read for addresses), Proxmox QEMU guest
   agent (installed by the first-logon script from the virtio-win ISO). The host records it,
   shows it in the panel as guest-reported, and detaches the auxiliary ISO once the installed
   beacon arrives.
6. **Proxmox specifics.** Windows on QEMU needs the virtio storage driver during setup:
   the host adds the virtio-win ISO as a third medium or injects the driver folder into the
   auxiliary ISO with a `PnpCustomizationsWinPE` driver path; e1000 NIC until the virtio NIC
   driver is installed. Depends on the Proxmox child VM work.

## 4. Work (once decided)

1. OS type + unattended parameters in the create contract and CLI; template rendering with
   tests on the rendered XML (no secrets in logs).
2. Host-side ISO build (auxiliary) and the no-prompt repack as media operations.
3. Key pool store, encryption, admin routes and panel, assignment and audit.
4. Guest write-back reader (KVP on Hyper-V; guest agent on Proxmox) and the auto-detach.
5. Docs: `docs/child-vms.md` Windows section; `examples/winvm-blank` README points at the
   feature.

## 5. Acceptance

A `construct vm create --os windows --iso <win11.iso> --cpus 4 --ram-gb 8 --disk-gb 100
--lifetime 4h --unattend-admin-password …` on a Hyper-V host reaches the desktop unattended,
answers SSH with the given password, shows "installed, activated with key …3V66T" (or
"installed, not activated, grace period") in the panel, and has no auxiliary medium attached
afterwards.

## 6. Open decisions

- Guest channel: KVP / guest agent write-back (host stays credential-free), or should the host
  keep the generated Administrator credentials and use PowerShell Direct / SSH for full control
  and verification?
- Is the key readable from inside the guest during setup acceptable (the agent could read the
  mounted auxiliary ISO before the detach), or must the key be pushed post-install through the
  guest channel instead of baked into the answer file?
- Hyper-V first, Proxmox once child VMs land there?
