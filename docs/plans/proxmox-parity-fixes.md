# Proxmox parity: nested virtualization policy, T3 pairing, honest create options

Status: plan, 2026-09-17. Branch `feat/proxmox-parity` (off `feat/proxmox-backend`, PR #19).
Implementation is handed to a T3 Code thread; this document is the brief.

## 1. Goal

Three small, independent fixes that close parity gaps between the Hyper-V and the Proxmox host:

1. **Nested virtualization is a setting**, not an accident. Today Hyper-V honours the create
   option `opts.nested` (`Set-VMProcessor -ExposeVirtualizationExtensions`), while the Proxmox
   driver ignores the flag and every guest gets it anyway through `--cpu host`. Make it a host
   default, overridable per user, and honoured per VM on both platforms.
2. **T3 pairing reaches the VM on a Proxmox host.** The default instance's pairing link hardcodes
   `<hostname>.mshome.net`, which does not exist for a Proxmox guest. Use the client-reachable
   address the guest already knows, and offer both the forwarded and the direct link when both
   exist.
3. **Ignored create options are reported.** `opts.automaticCheckpoints` is silently dropped on
   Proxmox; the response and the client must say so.

Decisions already taken (do not re-open): CPU and RAM settings stay without an Off-state guard
on Proxmox (over-provisioning is intended there); disk growth stays out of scope; a
Construct-controlled Proxmox cluster is a future path and gets one documented sentence, no code.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Create option flow | `service/src/Constructd.Api/Endpoints/VmEndpoints.cs` (~line 132: `Nested`, `AutomaticCheckpoints` into `VmDescriptor`), `Core/Domain/VmDescriptor.cs`, `Constructd.Windows/HyperV/HyperVScript.cs` (~line 59-61), `drivers/hyperv-local/HyperVLocal.Driver.ps1` (~line 245-256, `ExposeVirtualizationExtensions`), `Constructd.Proxmox/ProxmoxDriver.cs` (create argv ~line 140-155, `--cpu` from `Proxmox:CpuType`), `ConstructdOptions.cs` (`ProxmoxOptions.CpuType`) |
| Client side of the option | `Auto-Install.ps1` (`-Nested`, ~line 1224 and 2294-2335), `drivers/hyperv-remote/HyperVRemote.Driver.ps1` (~line 298-305), the create request contract in `service/src/Constructd.Api/Contracts` |
| Per-user flag pattern | `Core/Domain/User.cs` (`AllowHostForwards`), `Endpoints/HostAdminEndpoints.cs` user update (~line 33-56), `extension/media/hostadmin.js` (~line 325-348, the "Host forwards" dropdown), `Core/Services/DelegationPolicy.cs` |
| Host config section pattern | `Core/Domain/HostConfig.cs` (`NetworkConfig`), `Configuration/HostAdminDefaults.cs`, `Infrastructure/HostConfigValidation.cs`, `GET /host/capabilities` |
| Per-VM pending setting pattern (apply on Off→Start) | `Api/Jobs/PrimaryCpuSettings.cs`, `PrimaryMemorySettings.cs`, the VM settings routes and modal |
| Node capability | Linux: `/sys/module/kvm_intel/parameters/nested` or `kvm_amd` (`Y`/`1`); Hyper-V: always available on a nested-capable host |
| Installer | `service/host/install-construct-host.sh` (§ "Host power" is a good neighbour for a "Nested virtualization" step) |
| T3 pairing | `extension/vm/t3-pairing.sh` (default instance, hardcoded mshome), `extension/vm/t3-pairing-instance.sh` (named instance, reads `CONSTRUCT_EXTERNAL_HOST`), `extension/src/t3code.js` (~line 215-240, the rationale comment), `Get-ConstructT3PairingLink.ps1` (~line 43, 139, 143), `bin/provision.sh` (`CONSTRUCT_EXTERNAL_HOST` / `CONSTRUCT_EXTERNAL_SSH_PORT` in `/etc/construct/config.env`), `docs/remote-host.md` (external host propagation) |
| Direct network mode (in flight on `feat/proxmox-direct-network`, do not depend on its code) | `docs/plans/proxmox-direct-network.md`: in direct mode the guest's `CONSTRUCT_EXTERNAL_HOST` becomes the VM's own address and SSH port 22 |
| Docs | `docs/proxmox-host.md` §6 and §7, `service/README.md` (config rows, platform table), `docs/remote-host.md` |

## 3. Design

### 3.1 Nested virtualization

- **Host config** section `virtualization`: `NestedDefault` (bool, default `false`) and
  `NestedSelectable` (bool, default `true`: owners may ask for it at create time or in the VM
  settings). Validation: `NestedDefault = true` refused with `unsupported-on-host` when the host
  cannot nest. Surfaced in `GET /host/capabilities` as `nested: { available, default,
  selectable }` and in the host admin config UI (raw JSON is enough, plus the capability
  line in the Overview).
- **Per user** `AllowNested` (nullable bool: null = host `NestedSelectable`, true/false
  override), same plumbing and UI as `AllowHostForwards`.
- **Per VM**: `opts.nested` at create time keeps working; unspecified means the host default.
  Add a pending setting `nested:<vm>` in the CPU/RAM pattern so it can be flipped later from
  the VM settings modal and applied on the next stop/start. Policy: a request for `nested=true`
  is refused with `policy-denied` when the user may not select it; admins always may.
- **Proxmox driver**: honour the flag. Nested on → `--cpu host` (today's default); nested off
  → `--cpu <Proxmox:CpuTypeWithoutNesting>` (new option, default `x86-64-v2-AES`, the Proxmox VE
  8 default model, which does not expose VMX/SVM). Apply the same on the pending setting via
  `qm set --cpu`. Report the node capability from `/sys/module/kvm_*/parameters/nested`.
- **Hyper-V driver**: already honours the flag; only the policy layer is new.
- **Installer**: a "Nested virtualization" step that writes
  `/etc/modprobe.d/construct-kvm.conf` (`options kvm_intel nested=1` or `kvm_amd`) when the
  parameter is off, and prints whether it is live (a reboot or module reload is needed when
  VMs are running; say so, do not reload modules with guests up).
- **Client**: `Auto-Install.ps1 -Nested` stays; the create response now carries the effective
  value so the client prints "nested virtualization: on/off (host default)".

### 3.2 T3 pairing

- Both pairing scripts derive the base from the same rule: `CONSTRUCT_EXTERNAL_HOST` when set,
  else `<hostname>.mshome.net`. The old rationale for the default instance (a user-edited
  `config.env` could redirect the URL) is documented as accepted: the same file already
  redirects SSH and every other tool.
- The pairing JSON gains `links`: one entry per reachable route, each `{ kind:
  "forwarded"|"direct", pairUrl }`. Forwarded = external host plus the T3 forward port (what
  `t3base` computes today); direct = the VM's own address on the T3 port, included only when the
  guest knows a direct address (`CONSTRUCT_DIRECT_HOST` in `config.env`, written by the
  direct-network feature when it lands; absent today, so the list has one entry). `pairUrl`
  stays as the first entry for old readers.
- `Get-ConstructT3PairingLink.ps1` and the control panel show every link with its kind.
- Tests: `test/t3-https.test.sh` / the pairing tests in `test/run-local-checks.sh` cover both
  scripts with and without `CONSTRUCT_EXTERNAL_HOST`, and with `CONSTRUCT_DIRECT_HOST`.

### 3.3 Honest create options

- The create response includes `ignoredOptions: ["automaticCheckpoints"]` when the backend
  cannot honour an option that was explicitly requested (Proxmox: automatic checkpoints; add
  `nested` here too if a host cannot nest). `Auto-Install.ps1` prints one line per ignored
  option. Never fail the create for it.

### 3.4 Docs

`docs/proxmox-host.md`: §6 gets the cluster sentence ("a future path is a cluster created and
managed by Construct only; joining an existing cluster is not planned because the service needs
management access to the API"), §7 the new options; `service/README.md` the config rows and the
policy; `docs/remote-host.md` the pairing links.

## 4. Work, in order

1. Host config section, user flag, capability, policy in the create route; tests (policy
   matrix, validation, capabilities payload).
2. Proxmox driver `--cpu` selection + node capability probe + pending per-VM setting; argv tests
   on the process-runner fake; Hyper-V untouched except the policy layer.
3. Installer step + bash test with a stubbed `/sys` path.
4. T3 pairing scripts, PowerShell link helper, control panel display; tests.
5. `ignoredOptions` in the create response and client output; tests.
6. Docs.

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
and the extension tests green.

## 5. Acceptance

- A user without the allowance asking for `nested` gets `policy-denied`; an admin's request on
  a Proxmox host produces `--cpu host`, a `nested=false` request produces the non-nesting model.
- A Proxmox guest's default-instance pairing link uses the external host and works through the
  forwarded port; with `CONSTRUCT_DIRECT_HOST` set, a second direct link appears.
- A create with `automaticCheckpoints` on Proxmox succeeds and the client prints that the option
  was ignored.

## 6. Rules for the implementing agent

- Work on `feat/proxmox-parity` in this worktree (`/root/repos/construct-parity`). Commit in
  small steps with the repo's configured author; never put any other email in commits or files.
- Other threads work in `/root/repos/construct`, `/root/repos/construct-net` and
  `/root/repos/construct-children`. Do not touch those checkouts and do not merge or rebase
  across; keep edits to `ReleaseInfo`, `ProxmoxDriver` create argv, `HostConfig` and the
  installer small and additive so the human can merge.
- Do not run anything against a real host; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation
  in the final report.
