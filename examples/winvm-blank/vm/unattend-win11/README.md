# Win11 variant (UEFI)

Unattended Windows 11 Pro install: `autounattend.xml`
(UEFI/GPT layout, generic non-activating Pro edition key, built-in
Administrator auto-logon) + `firstlogon.ps1` (aggressive debloat: consumer
Appx, OneDrive, Copilot, widgets, telemetry; installs OpenSSH).

The variant boots **UEFI (OVMF) with a swtpm TPM 2.0** — the configuration
Windows 11 actually supports. `create-vm.sh` repacks the stock ISO once with
the media's own `efisys_noprompt.bin` (UEFI boot of stock media otherwise
stops at "Press any key to boot from CD or DVD..."); the repacked copy is
cached as `win11-noprompt.iso`. There is no reboot loop: after setup's first
phase the Windows Boot Manager NVRAM entry (persisted per-VM in
`<disks>/<vm>.OVMF_VARS.fd`) precedes the CD in boot order.

## Root cause of the old "25H2 media bug" (resolved 2026-07-21)

On **legacy BIOS** (SeaBIOS/MBR — the harness default before UEFI wiring),
25H2 media (Build 26200.8037) crashes during Windows Setup's *offline
specialize* pass:

```
SYSPRP Entering RdpSysPrepRestoreOffline
SYSPRP ActionPlatform::CallEntryPoint: ... unhandled exception (0xc0000005)
SP     Sysprep specialize offline failed. Error: 0xC0000005
```

→ "Windows 11 installation has failed". Reproduced with/without TPM and
with/without the RDP unattend components; Server 2022 installed fine on the
identical BIOS harness — which made it look like a media bug. It is not: the
**same ISO installs cleanly under OVMF UEFI + GPT** (verified end-to-end,
desktop + firstlogon reached). The crashing code path is specific to
BIOS/MBR-offline-specialize, a configuration Win11 never officially
supported. No 24H2 ISO needed.

The LabConfig bypass keys in the answer file are kept as belt-and-braces
(with UEFI + TPM the checks pass anyway; Secure Boot stays off — plain
`OVMF_CODE_4M.fd` — and LabConfig covers that check).
