# Windows child VMs: OS type, unattended install, license keys

Status: plan, dispatched 2026-09-18. Branch `feat/windows-child-vms` (off `main`). Implementation is
handed to a T3 Code thread; this document is the brief. Decisions in §6 are final.

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

1. **OS type on child create**: `--os linux|windows` (default `linux`, which is exactly today's
   behaviour and injects nothing). `windows` implies the firmware preset, the SATA system disk
   and the e1000e card the Proxmox driver already gives the Windows preset, unlocks the
   host-prepared Windows media, the host-rendered answer file and the key injection below.
   An agent-supplied ISO keeps working for either OS. `--windows <product>-<edition>` (default
   `win11-pro`) selects **product and edition together**: products are `win11` and
   `server2022`/`server2025` (the rig ships distinct answer files and first-logon scripts for
   the client and the server family: `examples/winvm-blank/vm/unattend-win11/` and
   `examples/winvm-blank/vm/unattend/`; keep them as two templates, selected by product), editions
   are `pro`, `pro-n`, `enterprise`, `education` for the client family and `standard`,
   `datacenter` (each also as `-core`) for the server family. The host lists what the chosen
   media actually contains (the image names inside its install image, e.g. "Windows 11 Pro",
   "Windows Server 2025 Datacenter") and refuses a product or edition the media lacks. The
   answer file installs exactly that image with that product and edition's public generic key.
2. **Host-built auxiliary ISO.** `construct vm create --os windows --iso … --unattend-*`
   parameters (admin password, hostname, locale, time zone, extra first-logon script, extra
   files) are rendered by the host into `autounattend.xml` + `firstlogon.ps1` from the template
   in `examples/winvm-blank/vm/unattend-win11/`, the key from the pool is inserted on the host,
   the ISO is built on the host (`xorriso`/`genisoimage` on Linux, the existing ISO builder
   seam on Windows) and attached as auxiliary media. The agent can still supply its own
   auxiliary ISO instead, in which case the host injects nothing.
3. **The host fetches the Windows ISO itself.** Decided 2026-09-17: a media action
   `construct vm media acquire --windows 11 --edition pro --lang en` (and `--windows server-2022`
   for the evaluation media, which has a stable link) resolves the official download on the
   host the way the example's `download-images.sh` does (Fido, run under `pwsh` on the host or
   ported to the host's own resolver), stores the result as a shared media item (owner: host,
   readable by every user, counted once), and also performs the no-prompt repack once and
   caches the repacked variant beside it. The item shows `edition`, `build`, `language` and
   `sha256` in `construct vm media list`. Agents then only supply the configuration (answer
   file parameters) and never download or repack anything; an agent-supplied ISO keeps working
   for other media. The repack stays available on its own for uploaded media
   (`construct vm media prepare-windows <id>`).
   Not too roundabout means: one CLI verb, one host job with progress, one cached item; no
   new service, no extra daemon.
4. **Key pool.** Admin adds keys (edition, kind retail/MAK/KMS-client, activation budget for
   MAK, notes) through the host admin panel or CLI. Encrypted at rest; the API returns only the
   last five characters. Assignment records per VM incarnation with states assigned, installed,
   activated, failed; released on delete (a MAK activation does not return). Auto-assign by the
   edition the guest reports, manual assign from the panel, audit entries. No matching key, or a
   KMS domain: nothing is pushed, the guest stays on the generic edition key and its grace
   period, i.e. trial as normal. The pool records the edition per key; the host-built answer
   file selects the install image from the requested or assigned edition.
5. **The key never enters the answer file.** The answer file carries only the public generic
   edition key (as the template does today), so an agent-built auxiliary ISO holds no secret.
   The template's `firstlogon.ps1` ends with a drop-in Construct block that reports the edition
   and the stage "first logon done", waits a bounded time for a key, applies it with
   `slmgr /ipk` and `/ato`, and reports the activation state and the partial key back. The
   key travels host to guest through the platform's guest channel, never through the agent:
   - Hyper-V: Data Exchange (KVP). The host writes the key as a host-to-guest item, the guest
     reads it from `HKLM\SOFTWARE\Microsoft\Virtual Machine\External`, the host removes the
     item once activation is reported; the guest's report goes into the Guest KVP pool the host
     already reads for addresses.
   - Proxmox: once the first-logon block has installed the QEMU guest agent from the virtio
     ISO, the host runs `slmgr` inside the guest itself (`qm guest exec`) and reads the output;
     nothing is stored in the guest.
   The host records the report per VM incarnation, checks the partial key against the assigned
   key, shows the state in the panel (marked guest-reported on Hyper-V), and detaches the
   auxiliary ISO after the "first logon done" beacon. Retail and MAK keys need internet from
   the guest at activation time; KMS client keys need the KMS host.
6a. **Proxmox lessons from the first field run (2026-09-18).** A Windows-preset child needs a
   real CPU model (the driver now sets one; `kvm64` hides POPCNT and Windows 11 loops in the
   boot manager), a SATA system disk and an e1000e card (no in-box virtio drivers; the driver
   now does both). Instead of a full no-prompt repack, a four-byte patch of the stock ISO's
   El Torito catalog pointing at its own `efisys_noprompt.bin` extent keeps UDF intact; make
   that the host-side "prepare Windows media" operation. After Setup's first phase the OVMF
   firmware boots the no-prompt DVD again instead of the disk (Hyper-V orders the Windows boot
   entry first by itself), so the host must eject the install medium at the guest's first
   reboot (uptime reset) for the install to stay unattended.
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
5. Docs, written for the agent that wants a Windows guest: a "Windows guests" section in
   `docs/child-vms.md` (the file agents already reach from `construct vm --help`), covering the
   host-side ISO fetch, the answer-file parameters, the key pool behaviour (what "no key" means),
   the first-logon report, and where the SSH credentials come from; `construct vm --help` and
   `construct vm create --help` name the section; the `examples/winvm-blank` README points at
   the feature. Not in the system prompt.

## 5. Acceptance

A `construct vm create --os windows --iso <win11.iso> --cpus 4 --ram-gb 8 --disk-gb 100
--lifetime 4h --unattend-admin-password …` on a Hyper-V host reaches the desktop unattended,
answers SSH with the given password, shows "installed, activated with key …3V66T" (or
"installed, not activated, grace period") in the panel, and has no auxiliary medium attached
afterwards.

## 6. Decisions

Decided (2026-09-17): the host stays credential-free; the key is pushed post-install through
the guest channel and never baked into the answer file; verification comes from the same
channel.

Decided (2026-09-18): both platforms in scope now; the Proxmox child VMs landed and the Windows
preset there already uses a SATA disk and an e1000e card, so no virtio driver handling is
needed for the install itself. The QEMU guest agent is installed by the first-logon script
from the virtio driver ISO the host attaches as a third medium when the platform is Proxmox,
and only then does the key push use `qm guest exec`; until the agent is up the host waits.

Products and editions: every pool key has a product (`win11`, `server2022`, `server2025`, …)
**and** an edition (client: pro, pro-n, enterprise, education; server: standard, datacenter,
with or without desktop experience) and a kind (retail, mak, kms-client; MAK with an activation
budget). The guest reports its installed product and edition at first logon (from the OS
caption and edition id); auto-assignment matches both exactly, a manual assignment from the
panel is validated against both. The host-fetched media carries its product too (`--windows 11`
or `--windows server-2022`/`server-2025` on acquire), so the create command can refuse a
product that the selected media does not contain. No matching key: the
guest stays on the generic key and its grace period, and the panel shows "not activated".

Key storage: encrypted at rest with a host key that is DPAPI-protected on Windows and a
root-only file under `/etc/constructd/keys/` on Linux; the API returns only the last five
characters, the audit log never the key.

Media: the host fetches official Windows media (Fido resolver, `construct vm media acquire
--windows 11 --edition pro --lang en`) and prepares it with the four-byte El Torito catalog
patch (§6a), never a repack; both the fetched original and the prepared variant are shared
media items readable by every user (the media store gains a `shared` flag; deletion admin-only).
Install media is ejected by the host when the first-logon beacon arrives.

## 7. Future: clusters

When Construct hosts form a cluster (a future, Construct-managed cluster only, see the Proxmox
guide), license keys must be bound to one physical host inside it: Windows activation is tied
to the hardware it was performed on, a MAK activation counts per installation, and an OEM key
is legally tied to its machine. Record the owning host on every pool key from day one
(`hostId`, today always the local host) and on every assignment, refuse to assign a key on a
different host, and treat a VM migration between hosts as an event that needs a new
assignment. Nothing else in this plan changes for a single host. (Noted 2026-09-18.)
