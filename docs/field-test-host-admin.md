# Field test — host administration and child VMs

A step-by-step checklist for the project owner's first deployment of host administration,
delegated child VMs and signed host updates on the `standpc` Hyper-V host. It assumes the
original [remote-host field test](field-test-remote-host.md) passed and the existing primary
`haus-vm` must remain usable throughout.

This checklist is deliberately conservative: destructive work uses disposable VMs, every
update has a recorded rollback point, and no step treats a Linux fake or recording runner as
proof of Windows/Hyper-V behavior.

## Validation status for this document

| Mark | Meaning |
|---|---|
| `[SIMULATED]` | Covered in this implementation run by Linux unit/integration tests, fake service backends or recording runners only. |
| `[FIELD]` | Must be performed on `standpc`; it was **not** performed while writing these docs. |

Every checkbox below is `[FIELD]` unless it explicitly says otherwise. During this run no
service was published to Windows, no Hyper-V VM was created or changed, and no update or
rollback was applied. Record the exact host, installed commit and time before changing that
status.

Names used below; substitute the actual values once at the start:

| Name | Suggested value |
|---|---|
| Hyper-V host | `standpc` |
| Existing primary that must survive | `haus-vm` |
| Disposable owner primary | `ha-parent` |
| Disposable second-user primary | `ha-consumer` |
| Linux child | `ha-linux` |
| Windows-hardware child | `ha-windows` |
| Service checkout | `C:\Construct` |
| Service publish directory | `C:\Construct\service\publish` |
| Service data directory | `C:\ProgramData\Construct\service` |

Keep tokens out of screenshots, transcripts, shell history and process arguments. The Admin
panel and `construct vm` use the existing credential providers; prefer them to ad-hoc curl
commands.

---

## 0. Book the window and freeze the evidence

- [ ] `[FIELD]` Get the host owner's explicit approval for service restarts, disposable VM
      creation/deletion and the rollback drill. Ensure nobody is provisioning or deleting a VM.
- [ ] `[FIELD]` Keep `haus-vm` running and record its Hyper-V state, SSH endpoint, Construct
      commit and one working application URL. This is the continuity witness.
- [ ] `[FIELD]` Record free RAM and disk, the active Hyper-V switch, Windows build, service
      account, service image path and current constructd version.
- [ ] `[FIELD]` Save the current output of:

  ```powershell
  Get-CimInstance Win32_Service -Filter "Name='constructd'" |
    Select-Object Name, State, StartName, PathName
  Get-VM | Select-Object Name, State, Generation, MemoryAssigned, ProcessorCount
  & 'C:\Construct\service\publish\Constructd.Api.exe' admin host status --json
  & 'C:\Construct\service\publish\Constructd.Api.exe' admin iso status --json
  ```

- [ ] `[FIELD]` Record the SHA-256 fingerprint already pinned by the client. Do not rotate
      the host certificate during this test.
- [ ] `[SIMULATED]` Confirm the candidate commit passed the repository's .NET, Node,
      PowerShell, bash and fake-service end-to-end suites. Attach the counts to the field log.

**Stop condition:** do not continue if `haus-vm` is already unhealthy, the database cannot be
backed up, the candidate commit is dirty/unidentified, or the production release public key is
not available through the owner's approved secret-handling process.

## 1. Publish and perform the first manual rollout

The installed service cannot use the update API until this version is present. The first
rollout is therefore manual; later ones use Maintenance. Do not rerun
`Install-ConstructHost.ps1` with defaults—it rewrites `appsettings.Production.json`.

### 1.1 Prepare an immutable candidate

- [ ] `[FIELD]` From a clean checkout of the exact approved 40-character commit, publish to a
      **new** staging directory, never over the live publish directory:

  ```powershell
  $commit = '<40-character approved main commit>'
  $stage = "C:\Construct-rollout\$commit"
  dotnet publish C:\Construct\service\src\Constructd.Api -c Release -r win-x64 `
    --self-contained true -o "$stage\service"
  ```

- [ ] `[FIELD]` Use the matching tracked `drivers`, `lib`, `bin`, `config`, `service\host`
      and `Provision-AgentVM.ps1` from that same commit. Record hashes or use the signed host
      package described in [Host releases](host-release.md); never mix scripts from another
      commit.
- [ ] `[FIELD]` Verify that `config\host-release.pub` contains the owner's approved public
      key and that it matches the private key held by the `host-release` publishing workflow.
      The repository default is intentionally empty, in which case updates must fail closed
      with `signing-key-missing`.
- [ ] `[FIELD]` Copy the live `appsettings.Production.json` into the staging service directory.
      For this manual bootstrap only, merge the nonempty public key as
      `Constructd:HostAdmin:Updates:ManifestPublicKey`; this is the value the normal installer
      would seed. Preserve every other existing setting byte-for-value and validate the JSON.
      Record a redacted diff whose only semantic addition is that **public** verification key;
      no token, certificate private key or password should appear.

### 1.2 Back up, swap and verify

- [ ] `[FIELD]` Create a timestamped rollback directory outside both the live scripts and
      publish directories. While constructd is stopped, copy the entire publish directory,
      matching host scripts and the complete service data directory into it. Record the hashes
      of `appsettings.Production.json`, `constructd.db` and `Constructd.Api.exe`.
- [ ] `[FIELD]` Stop only `constructd`. Confirm every Hyper-V VM, especially `haus-vm`, keeps
      its prior state. Swap the staged publish directory into the existing service image path,
      deploy the matching scripts, and start `constructd` again.
- [ ] `[FIELD]` If startup fails, capture Windows Event Log plus service logs, restore the old
      binaries/scripts, and restore the database only when the recorded schema compatibility
      requires it. Do not repeatedly start mixed versions.
- [ ] `[FIELD]` When startup succeeds, run:

  ```powershell
  & 'C:\Construct\service\publish\Constructd.Api.exe' admin db check
  & 'C:\Construct\service\publish\Constructd.Api.exe' admin host status --json
  ```

  **Expect:** database check succeeds; `/health` advertises `host-admin`, `children`, `media`,
  `console`, `updates` and `network`; `haus-vm` is still Running and its SSH/application checks
  still pass.

## 2. Verify migration of `haus-vm`

- [ ] `[FIELD]` Open **Construct: Host Administration** from the enrolled client. The Overview
      loads without requiring a VM creation and identifies the installed release and capacity
      policy.
- [ ] `[FIELD]` In **VMs**, find `haus-vm`. Record that it is `kind=primary`, has no parent,
      retains the same owner, CPU/RAM/disk values, SSH forward and idle policy, and shows unknown
      rather than invented guest facts where an older record had none.
- [ ] `[FIELD]` Connect to `haus-vm`, exercise its existing Start/Save/forward/provision path,
      and verify old client enrollment and tokens still work. This is the zero-change default
      path.
- [ ] `[FIELD]` Inside `haus-vm`, run `construct vm identity --json` **before** credential
      upgrade. A migrated token should report `legacy` and refuse child management without
      breaking heartbeat or existing primary forwards.
- [ ] `[FIELD]` Run `Provision-AgentVM.ps1 -InstanceName haus-vm -RotateVmToken` once
      from the owner PC. The planned credential-upgrade menu item is not wired into
      VS Code or Auto-Install yet. Re-run `construct vm identity --json`.
      **Expect:** token kind `primary`; the previous token is immediately invalid; no token value
      appears in logs or job results.

## 3. Enroll two users and set allowances

- [ ] `[FIELD]` Keep the existing owner as Admin. Register a separate ordinary user for the
      sharing test and create/provision the disposable primaries `ha-parent` and `ha-consumer`
      through the ordinary remote-primary flow. Do not use `haus-vm` for later cascade deletion.
- [ ] `[FIELD]` In **Users**, set the owner allowance to:

  ```json
  {
    "allowChildCreation": true,
    "maxRetainedChildren": 4,
    "cpuBudget": 8,
    "ramBudgetBytes": 17179869184,
    "storageBudgetBytes": 214748364800,
    "maxChildLifetimeSeconds": 14400,
    "allowNeverLifetime": false,
    "allowSharing": true
  }
  ```

  Leave one nonessential numeric field `null` first and record the inherited effective value;
  then save the explicit value above. Verify the returned stored and effective policies differ
  exactly where inheritance applies.
- [ ] `[FIELD]` Give the second user one retained child slot but disable child creation and
      sharing. Confirm their primary can operate a host-shared child but cannot create one.
- [ ] `[FIELD]` Temporarily lower the owner's CPU budget below the planned request. Confirm the
      create preview/request is refused with a capacity/policy problem and the audit entry names
      the decision without logging credentials. Restore the allowance before continuing.

## 4. Acquire a public Linux ISO and create `ha-linux`

- [ ] `[FIELD]` Choose an official public Linux ISO URL and obtain its SHA-256 from the publisher
      over an independent trusted path. Record URL, redirect chain, size and checksum.
- [ ] `[FIELD]` From `ha-parent`, verify `construct vm identity`, then create the child:

  ```bash
  construct vm create \
    --name ha-linux \
    --iso-url '<official public ISO URL>' --sha256 '<publisher SHA-256>' \
    --cpus 2 --ram-gb 4 --disk-gb 40 --lifetime 4h \
    --preset linux --generation 2 --secure-boot off \
    --operation-id field-ha-linux-01 --json-progress
  ```

- [ ] `[FIELD]` Record media-acquire and child-create job ids, phase events and final inventory.
      Confirm the checksum is verified, no private/loopback URL is accepted, reservations are
      charged to the owner, and the child is not falsely labelled provisioned merely because it
      booted.
- [ ] `[FIELD]` Reissue the identical request with the same operation id. **Expect:** replay of
      the original operation, not another VM or media item. A changed request with that id must
      return a conflict.

## 5. Create `ha-windows` with Windows-compatible hardware

- [ ] `[FIELD]` Upload an owner-approved Windows installation/evaluation ISO. Record license
      source, size and checksum; do not put unattended secrets in auxiliary media.
- [ ] `[FIELD]` Create the child powered off so every hardware field can be inspected before
      boot:

  ```bash
  construct vm create \
    --name ha-windows --iso ./windows.iso --sha256 '<SHA-256>' \
    --cpus 4 --ram-gb 8 --disk-gb 80 --lifetime 4h \
    --preset windows --generation 2 --secure-boot on \
    --secure-boot-template microsoftWindows --tpm on \
    --boot-order installMedia,disk,network --no-start \
    --operation-id field-ha-windows-01 --json-progress
  ```

- [ ] `[FIELD]` In Hyper-V Manager and `construct vm inspect ha-windows`, verify Generation 2,
      fixed 8 GiB memory, four vCPUs, Microsoft Windows Secure Boot template, TPM, boot order,
      disk maximum and Off state. Record whether TPM provisioning succeeds under LocalSystem.
- [ ] `[FIELD]` Start it with `construct vm start ha-windows --lifetime 4h`. Verify the Windows
      installer reaches its first interactive screen. Do not claim installation completion unless
      it was actually completed.

## 6. Exercise screenshot, keyboard and mouse

- [ ] `[FIELD]` Capture each boot console before guest networking is usable:

  ```bash
  construct vm console ha-linux --screenshot ./ha-linux-boot.png
  construct vm console ha-windows --screenshot ./ha-windows-boot.png
  ```

  Record dimensions, SHA-256 and a redacted copy of each image. **Expect:** valid PNG content
  reflecting the current framebuffer, obtained without SSH/RDP in the child.
- [ ] `[FIELD]` At a non-destructive installer prompt, use `--key 13` and a short public string
      through `--type-stdin`; capture a new screenshot proving input reached the intended VM.
      Never type a password through a command argument.
- [ ] `[FIELD]` Test one absolute move, one relative move and one click. Record the complete
      response. `applied: false` with the documented fallback is an honest supported outcome;
      do not record mouse as working unless the cursor or UI change is visible in a screenshot.
- [ ] `[FIELD]` Confirm a session expires/closes and cannot be reused, another user cannot access
      a private child's console, and the audit log contains session/input events without text,
      tokens or framebuffer data.

## 7. Share across users, then revoke

- [ ] `[FIELD]` From `ha-parent`, run `construct vm share ha-linux --scope host`. From
      `ha-consumer`, run `construct vm list --all-shared --json` and inspect the child.
- [ ] `[FIELD]` As the second user, take a screenshot, gracefully shut down the shared child and
      start it with an explicit lifetime. Confirm ownership and all capacity charges remain with
      the first user.
- [ ] `[FIELD]` Confirm the second user cannot delete, renew, reshape, reattach media or change
      sharing. Each refusal should be RFC 7807 problem details and audited.
- [ ] `[FIELD]` Restore `private` sharing. Confirm new console/lifecycle operations from the
      second user fail immediately and any exposure requested by that user is reconciled away.

## 8. Verify finite-lifetime expiry

- [ ] `[FIELD]` Ensure Hyper-V guest shutdown integration works in `ha-linux`, shut it down and
      wait for Off, then start it with the minimum test lease:
      `construct vm start ha-linux --lifetime 5m`.
- [ ] `[FIELD]` Record `activatedAt`, `expiresAt`, service time and the lease scheduler interval.
      Restart the guest and restart constructd once during the lease. **Expect:** neither action
      extends `expiresAt`; `haus-vm` remains unaffected.
- [ ] `[FIELD]` Wait beyond expiry plus one scheduler tick. **Expect:** a graceful-shutdown job,
      child Off, lease `expired`, runtime capacity released, disk/media retained, and no deletion.
- [ ] `[FIELD]` If guest shutdown cannot complete, record `overdue` and the reason. Confirm there
      is no force-off/save/delete fallback, repair integration, and let the configured retry or an
      explicit owner action resolve it.

## 9. Exercise the update drain gate with running VMs

- [ ] `[FIELD]` Confirm `haus-vm` and at least one disposable child are Running. Record their VM
      ids, state, uptime and reachable endpoint immediately before the update.
- [ ] `[FIELD]` In **Maintenance**, Check and Stage an immutable, signed `host-<commit>` release.
      Confirm the resolved commit, package/script hashes, signature, schema compatibility and
      installed/staged states. A tampered manifest/package must be refused before apply.
- [ ] `[FIELD]` Start a deliberately long, disposable media acquisition, then select **Apply**.
      **Expect:** phase `draining`; replacement does not begin while the media job is active.
- [ ] `[FIELD]` During drain, attempt a new child/media mutation. **Expect:** `503 maintenance`
      problem details with `Retry-After`; reads and `/health` still work. Cancel or let the original
      media job finish and verify apply continues exactly once.
- [ ] `[FIELD]` During handoff/replacement, watch Hyper-V from a separate elevated console.
      **Expect:** `haus-vm` and the child never stop, save, pause or reboot. The Admin panel shows
      maintenance, reconnects, and recovers the persisted result after service restart.
- [ ] `[FIELD]` Re-run `admin db check`, compare settings/certificate/database/media/user/token/VM
      inventories with the pre-update record, and exercise `haus-vm` SSH and application URLs.

## 10. Prove automatic rollback

Do not simulate rollback by corrupting a production release after signing: that only proves
staging rejection. Use a separately approved, correctly signed field-test release whose service
payload intentionally fails the updater's bounded health check and whose compatibility metadata
allows rollback.

- [ ] `[FIELD]` Record approval and the test release commit. Stage and apply it while `haus-vm`
      and a disposable child are Running.
- [ ] `[FIELD]` Confirm the updater detects the health failure, restores verified prior files,
      restarts the prior service and runs its authenticated health plus SQLite `quick_check`.
      VMs must remain Running throughout.
- [ ] `[FIELD]` Verify update status records `rolledBack` (not success), the failed phase, health
      attempts and complete-backup state. Inspect `<DataDir>\updates\last-update.json` locally;
      do not copy its one-time health credential into the field log.
- [ ] `[FIELD]` If recovery remains in maintenance, stop the test and follow the `resolve`/
      `resume` fences in [Host releases](host-release.md). Never manually mix backup and new files
      or declare success while the fence is unresolved.

## 11. Verify primary cascade deletion

This is why all destructive children belong to `ha-parent`, not `haus-vm`.

- [ ] `[FIELD]` Ensure `ha-parent` owns at least two disposable children, one private and one
      host-shared, with dedicated media. Record disk sizes, sharing, state and media references.
- [ ] `[FIELD]` Delete `ha-parent` from the Admin panel. **Expect:** the preview names every child,
      highlights the host-shared one, states that disks/saved state/dedicated media are permanent,
      and requires the exact primary name.
- [ ] `[FIELD]` Before confirming, create or change one child in another session. **Expect:** the
      stale cascade token is refused with `cascade-scope-changed` and a fresh complete preview.
- [ ] `[FIELD]` Confirm the fresh scope. Verify delegation closes atomically, no new child can be
      created, every child and dedicated medium is removed, reservations are released only after
      confirmed deletion, and the parent is removed last.
- [ ] `[FIELD]` Confirm `haus-vm`, `ha-consumer`, unrelated shared media and their forwards remain.
      If cleanup fails, verify retained ownership/storage liability is visible and retry from a
      fresh preview; never edit the database to hide it.

## 12. Close out and record the verdict

- [ ] `[FIELD]` Remove remaining disposable media/VMs and restore any temporary allowance or
      host-config changes. Keep `haus-vm` enrolled and verify it once more.
- [ ] `[FIELD]` Export/redact the Admin Overview, VM/user allowance records, capacity before/after,
      audit sequence, job ids/phases, console capability responses, screenshot hashes, update
      status and rollback record.
- [ ] `[FIELD]` Record for each capability: `passed`, `failed`, `conditional/unavailable` or
      `not run`. Include exact Windows/Hyper-V version, service commit, extension commit, switch,
      service account and timestamps.
- [ ] `[FIELD]` File implementation defects separately from environmental limitations. In
      particular, record the known missing `GET /api/v1/host/iso-catalog` route: the Admin Media
      tab's primary catalog read is expected to fail in this candidate, while
      `constructd admin iso status` and general-purpose `/media` must still work.
- [ ] `[FIELD]` Do not declare the host-admin release field-validated until migration, both child
      hardware paths, console, sharing/revocation, expiry, drain, rollback and cascade all have a
      recorded result. A skipped item stays a known limitation.

## Evidence bundle

Store the following in the project owner's approved private location, not in the repository:

- candidate/release commit and hashes; pre/post installed release and schema;
- redacted config diff and certificate fingerprint (never token material);
- pre/post VM, media, capacity and allowance inventories;
- job ids, operation ids, phase timelines and relevant RFC 7807 bodies;
- redacted PNGs plus their dimensions/hashes and console input capability results;
- update `last-update.json` with credentials removed, Windows service/event excerpts and the
  updater task result;
- pass/fail table with deviations, cleanup state and follow-up issue links.

The automated Linux evidence proves contract wiring and deterministic failure handling. This
bundle is what turns those simulations into a defensible Hyper-V field result.
