# Reusable Windows license machines

Design draft, 2026-09-22. This records the requested behavior; production code is
not implemented. Disk replacement, security cleanup and activation replay require
a real Hyper-V proof before the pool lifecycle can be implemented with confidence.

## Agreed behavior

- Adding an eligible Retail or MAK key automatically queues its first activation.
  Prefer an existing compatible VM waiting for a host license; otherwise create
  a host-owned Windows VM and install the matching OS. Never replace a user's own
  activation automatically. Storing a key alone cannot activate it. Missing media
  or capacity leaves a visible queued prerequisite, rather than consuming an
  activation attempt. After an unused enrollment succeeds, delete its setup disk
  through the same verified cleanup path and retain the machine idle.
- The first activation contacts Microsoft using proxy activation and retains the
  installation ID and confirmation ID. A bare `slmgr /ato` call is insufficient as
  the enrollment workflow because it does not give Construct that saved record.
- A key remains associated with its retained VM identity. Reuse installs Windows
  on a new disk, installs the same key, applies the saved confirmation ID and
  verifies the resulting Windows activation state.
- A failed replay never automatically obtains a new confirmation ID or starts a
  new online activation. The host displays the failure. An administrator can choose
  **Activate again** to authorize one new online activation and replace the saved
  record after success. The installed guest remains usable to the extent Windows
  permits and keeps waiting for that activation result; setup itself does not hang.
- Without an available host license, ordinary Windows provisioning still works.
  Construct supplies no pool activation, the user may install their own key, and
  actual evaluation editions retain their own expiry behavior. Construct adds no
  license prompt or activation requirement to the provisioning flow.
- All Windows guests periodically report licensing state, whether activated by
  the pool, by their user, through KMS, or not activated.

MAK initialization quantity remains a design choice: prepare one VM when the key
is added and grow on demand within its budget, or prepare the entire budget at
once. One initially is the proposed default, pending Christoph's answer. Each
MAK machine needs its own activation record; the CID is never shared across machines.
KMS client keys use KMS and do not enter the CID pool.

## Activation protocol

Microsoft's [proxy activation](https://learn.microsoft.com/en-us/windows/deployment/volume-activation/proxy-activation-vamt)
supports eligible Retail and MAK keys. VAMT collects each installation ID, obtains
a corresponding confirmation ID from Microsoft and applies it to the guest.
Use the supported VAMT interface for initial acquisition in the first implementation;
validate its unattended automation and installation requirements in the proof.

For later installations, install the same product key and apply the saved CID
locally using the supported licensing interface, then query activation status.
Microsoft documents that [local reactivation](https://learn.microsoft.com/en-us/windows/deployment/volume-activation/local-reactivation-vamt)
preserves activation allowance when the hardware fingerprint still matches.
Significant changes require a new CID. Never report success solely because applying
the record returned without an error. Verify the installed OS SKU, key identity
and Windows licensing state.

Digital-license activation can also occur. Observe and report it; do not assume
that every key or Windows edition provides that path. There is no new acquisition
request when a fresh observation already proves the intended activation is present.

An activation operation has its own durable ID and stages for key installation,
IID collection, CID acquisition, application and verification. Persist the CID
before application so that a retry of local application does not contact Microsoft
again. An interrupted Microsoft acquisition with an uncertain result requires host
attention; it must not be repeated automatically as a fresh request. A guest report
and response must match the current operation and allocation, not just the VM GUID.

The existing per-key guest receipt from the late-delivery fix cannot be the sole
deduplication key: **Activate again** must allow a newly authorized operation with
the same product key. Transport retries retain the original operation ID.

The no-automatic-online-retry rule must cover Windows' own activation behavior as
well as Construct. Evaluate the documented
[SkipAutoActivation setup setting](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-security-spp-ux-skipautoactivation)
on each supported edition. Do not assume it prevents every later retry. Validate
replay failure with internet restored and across reboots; if the guest independently
performs another online activation, that platform configuration does not meet the
requirement. User-initiated activation with a personal key remains supported.

## Persistent machine and disposable user allocation

Separate three records:

| Record | Lifetime and purpose |
| --- | --- |
| License key | Host, product, edition, kind, encrypted key and activation budget. |
| License machine | Stable host/hypervisor identity, hardware profile, protected security state, encrypted IID/CID and pool state. One current allocation at most. |
| User allocation | Fresh identifier, owner, parent, name, lease, disk, credentials and access policy for one installation. |

Retail keys normally own one machine. MAK keys can own several. The user-visible
name may change while the underlying machine identity stays stable. Operations,
reports, credentials and network access are tied to the new allocation so that a
previous user's requests cannot affect the next installation. The existing code
uses the hypervisor incarnation as an installation discriminator in several places;
audit those uses rather than globally replacing it with the new allocation ID.

Create flow: atomically reserve a compatible idle machine, admit its CPU/RAM/disk
requirements, attach fresh storage, install Windows, replay activation and assign
the fresh user allocation. A machine cannot be reused concurrently. A replay failure
stays attached to that allocation and waits for host action. It does not silently
switch to another key or consume another machine's initial activation.

Delete flow: fence the allocation, revoke access, turn off the VM, remove all user
disks and checkpoint chains, saved memory, answer media, forwarding rules and old
guest-channel messages, then clean the retained security state. Verify completion
before exposing the machine as idle. A partial cleanup remains unavailable and
resumable. A VM with no managed pool identity is deleted normally, including VMs
activated with a user's personal key; do not collect that key or its CID for reuse.

An idle machine has no user, no parent, no disks, disconnected network access and
automatic startup disabled. It is absent from users' VM lists but visible in the
host license panel. Release CPU/RAM/user-disk reservations after verified cleanup;
account for retained configuration storage separately. Host inventory reconciliation,
parent deletion and ordinary cleanup must recognize these deliberate retained VMs.

Removing a key retires its idle machines and their activation records. Existing
allocations block key removal; deleting a key must not destroy another user's VM.
Deletion never refunds a Microsoft activation or decrements recorded online attempts.

## Reporting and UI

Refresh licensing after setup, after an activation operation, after startup and
periodically while running. Report even when no key is waiting, KMS is detected,
or evaluation media is in use. Reporting is independent of activation execution.
Use CIM properties rather than parsing localized `slmgr` output. Select the actual
installed Windows OS product and exclude add-on licenses; do not use the first
arbitrary product with a partial key.

Windows exposes the required fields through
[SoftwareLicensingProduct](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/sppwmi/softwarelicensingproduct):

| Report field | Meaning |
| --- | --- |
| OS product, edition and activation ID | Identifies the installed Windows license product. |
| Partial key and channel | Shows whether the expected pool key is installed, or activation is user-managed/KMS/unknown. Never report a full user key. |
| LicenseStatus and LicenseStatusReason | Windows' current licensing state and reason code. |
| GracePeriodRemaining | Minutes until notification or a volume client's required reactivation; interpret according to channel. |
| EvaluationEndDate | Actual evaluation expiry, when meaningful and available. |
| Observation timestamp and allocation ID | Freshness and ownership of this report. |

Display examples:

- `Activated · host license · checked 30 seconds ago`
- `Activated · user-managed · checked 1 minute ago`
- `Evaluation · expires 2026-12-01 · checked 1 minute ago`
- `Not activated · activation grace remaining: 12 days`
- `Needs host action · confirmation ID rejected · error 0x…`
- `Status unavailable · VM stopped · last reported activated yesterday`

"Activated" means Windows reports activation. It is not proof of purchased license
rights or a separate promise about a key's future acceptance. Key installation,
OS activation and the latest Construct activation operation are separate UI facts.
A personal key replacing a pool key must be observed without silently overwriting it.

Do not describe every unactivated Windows installation as an evaluation. Keep
evaluation expiry, activation grace and KMS renewal separate. Missing/sentinel
values are unknown or not applicable, not zero days, unlimited or an invented
30/90/180-day allowance. Stopped or unreachable VMs retain a clearly dated last
observation, not a fresh health verdict. Date-based countdowns can be derived from
an observed absolute expiry; do not invent a continuously ticking grace timer from
a stale duration whose behavior while powered off has not been verified.

Pool status separates `preparing`, `available`, `assigned`, `cleaning`,
`needs activation`, `cleanup failed` and `retiring`. Show live assignment counts,
new online activation attempts and successful CID reuses separately. Replays and
status reads do not charge the MAK budget. Initial acquisition and each explicitly
authorized reacquisition are recorded conservatively; this remains a local ledger,
not Microsoft's authoritative remaining activation count.

## Proof and implementation order

1. On a disposable Hyper-V VM, validate proxy enrollment and capture a CID for
   an actual supported key/product. Do not consume a real key as part of this
   design-only work.
2. Delete the VHDX, keep the VM identity, attach a new disk and reinstall. Prove
   local CID replay works with no guest internet connection. Repeat several times.
3. Validate removal of previous-user TPM/firmware secrets. Retaining the used vTPM
   unchanged is not an acceptable cleanup policy. Test a supported clear procedure
   or a clean baseline restoration, and verify activation after that operation.
   Do not assume changing TPM security state preserves the activation fingerprint.
4. Exercise failed CID replay, native Windows automatic activation, reboot and
   duplicate delivery. No new Microsoft request may occur without **Activate again**.
5. Add periodic reporting for managed, personal-key, KMS, unactivated and actual
   evaluation installations, including missing data and offline/stale observations.
6. Add persistent machine/allocation records, then resumable admission, deletion,
   cleanup and reuse jobs. Add the admin actions and the UI status projections.
7. Validate simultaneous allocation, partial cleanup, parent deletion, manual key
   changes, interrupted CID acquisition and host restart recovery.

Start retained-machine reuse on Hyper-V. Keep the reporting contract applicable
to both Hyper-V and Proxmox; Proxmox retention and identity preservation require a
separate backend proof before enabling the same pooling behavior there.

If deleting the disk or performing required security cleanup invalidates the CID,
the exact diskless-reuse design has failed its proof for that configuration. Report
that result and revisit the design; do not retain a user's disk or security secrets
to conceal the incompatibility.
