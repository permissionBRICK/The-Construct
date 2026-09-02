# Modular / Remote Architecture Plan

Status: **in execution** · plan 2026-09-01, progress log at the end
Scope decided with Christoph via Q&A; findings below come from a four-way repo/notes audit
(assumption map, idea mining, jarvis logs, pipeline anatomy).

## 1. Goal

Make The Construct flexible enough to run its VMs on more than "the local Hyper-V of the
user's own PC", without changing the default single-VM experience at all:

1. **Local Hyper-V** (today's mode) — unchanged, zero-migration default.
2. **Remote Hyper-V host** (built in this effort) — an admin installs a Windows service
   once on a shared machine; multiple users create and manage their own VMs on it
   remotely, from the same VS Code extension and scripts.
3. **Proxmox / anything else** (designed-for only) — the seams introduced here must fit a
   future Proxmox driver without rework; nothing Proxmox-specific is implemented now.

Guiding requirement (from `jarvis/personal/tasks/construct-remote-proxmox.md`):
**PC-independence** — once a VM runs on a remote host, nothing it depends on may live on
the user's PC. The user's machine can shut down; the VM, its port forwards, and its
management state keep working. Authority/state for remote VMs therefore lives in the
host service, and the extension is a management UI only.

## 2. Decisions already made (settled, not up for re-litigating)

| Topic | Decision |
|---|---|
| Layering | Hypervisor **driver contract + shared core**. Local Hyper-V = driver #1, remote Hyper-V = driver #2, Proxmox later. ISO build, in-guest provisioning, config sync stay backend-agnostic. |
| VM identity | **Client-side instance registry**; each VM is a named instance. `agent-vm` remains the implicit default instance → existing installs need no migration. |
| Remote control channel | **HTTP API** from a **.NET minimal API** Windows service on the Hyper-V host. |
| Auth | **Kerberos/Negotiate first** (VS Code process identity, fallback: manually entered domain user+password), **admin-issued tokens** as the alternative. Admin explicitly adds users; per-VM ownership; per-user quota. Testable on Christoph's home domain. |
| VM reachability | **Host-service port forwards** (per-VM SSH port on the host's LAN address). Plus an **in-VM CLI** (`construct expose <port>`) so agents can self-serve additional forwards for dev servers etc. |
| VM-CLI auth | **Scoped per-VM token** injected at provision time; valid only for that VM's own forward management. |
| Provisioning split | **Hybrid**: service does ISO build + VM create + OS install wait; the **client** runs the agent-stack provisioning (`Provision-AgentVM.ps1`) over the forwarded SSH port — user secrets (git creds, agent auth, backups) never transit the service. |
| ISO build on remote host | **WSL is a service-host prerequisite** (checked/installed by the one-time service setup); the proven `build-autoinstall-iso.sh` path is reused as-is. |
| Compatibility | **Zero-change default path.** One-liner install, `agent-vm` name, `mshome.net`, existing extension flows all keep working exactly as today. Multi-VM and remote are opt-in. |
| Process | Plan doc (this file) → analyze/consolidate → parallel worktree implementation with per-chain codex auto-review → merge batches. |

## 3. What the audit established

### 3.1 Where the single-VM coupling actually lives

Full inventory in the audit; the load-bearing points:

- **Three root literals** define the VM name — `Create-AgentVM.ps1:158` (`$VmName`, not
  even a param), `Auto-Install.ps1:640` (`$HyperVmName`, comment says "must match"),
  `extension/src/vmpower.js:27` (`VM_NAME`). Nearly all other ~120 occurrences flow from
  these as values; `Auto-Install.ps1` is already variable-threaded internally.
- **`mshome.net` is concatenated in five layers**, including **inside the guest**:
  `bin/print-connection-info.sh:40` sets `hyperv_dns="$(hostname).mshome.net"` and 15
  lines consume it (banner, SMB UNC, service URLs); same pattern in `install-vscode.sh`,
  `install-ai-tools.sh`, `setup-root-ssh-key.sh`, `setup-smb-share.sh`.
- **The extension already has the multi-VM seam, unused**: `ssh.js` `DEFAULTS` +
  `resolveCfg(opts.cfg)` is honored by every module (`remote.js`, `audio.js`,
  `notify.js`, `t3code.js`, `probe.js`) — but no caller ever passes `cfg` and
  `package.json` contributes no vmHost/hostAlias setting. Wiring one config source in
  converts most of the extension in one move.
- **Only two real Hyper-V leaks into the UI layer**: `vmpower.js` (spawns
  `Get-VM`/`Start-VM` PowerShell directly) and `Set-AgentVmCheckpoints.ps1` (checkpoints
  as a control-panel feature, launched from `lifecycle.js`). These need the driver
  interface, not a parameter. `lib/AgentVm.Common.ps1` also contains Hyper-V bits
  (`Remove-AgentVm`, `Ensure-HyperV`, checkpoint classification) to be moved behind it.
- **Data-model singletons** (the deep work): one flat machine-wide
  `%LOCALAPPDATA%\The-Construct\config` dir; config-sync's fixed git branch literally
  named `vm`; the `Host agent-vm` SSH-config block that is *replaced* per provision; one
  key filename `agent_vm_ed25519` (hardcoded, not parameterized, in
  `AgentVm.Common.ps1:3355`); one notification spool per VM; `findScriptsDir` resolving
  "the newest install", singular; fixed ports everywhere (mic 8767–8774 is per-window
  de-confliction, **not** per-VM).
- **Entry points with no target argument**: `Update-T3Code.ps1`, the extension's
  `lifecycle.js buildInvocation` (never emits `-VmHost`/`-HostAlias`), `Get-AgentUsage`
  defaults.

### 3.2 The seams that already exist (reuse, don't rebuild)

- **ISO build** (`bin/build-autoinstall-iso.sh`): fully env-parameterized
  (`VM_USER/VM_PASS/VM_HOST`, bootstrap pubkey), no PowerShell knowledge;
  `-SkipCreateVm` is an existing "ISO only, no admin" mode.
- **In-guest provisioning** (`bin/provision.sh`): real machine contract — env in,
  sentinel block + exit code 0/3/N out; knows nothing about Hyper-V or Windows.
  `docs/manual-setup.md` + installation "Option D" are already the de-facto
  hypervisor-agnostic story (BYO VM, `Provision-AgentVM.ps1 -VmHost … -HostAlias …`).
- **Client config**: `Set-HostSshConfig`, `Set-VsCodeRemotePlatform`,
  `Set-OpenCodeRemote`, vsix install, ffmpeg — pure host-side lib functions.
- **Compat discipline to preserve**: parameter probing before splatting; env-var result
  channels (`CONSTRUCT_UPDATE_RESULT` etc.); the sentinel/result-file protocol; the
  three-level precedence idiom (explicit > `config.env` saved > default, `""` = keep).
- **Precedents to copy**: the mic-tunnel port-range allocator with busy-detection
  (ARCHITECTURE.md §mic) for per-VM port allocation; `Invoke-DeElevatedProvision` (kill-
  switched) sits exactly where the privilege boundary belongs; config-sync §7 already
  spec'd the multi-repo/company story.

### 3.3 Constraints recorded in jarvis worth honoring

- **Hostname-per-instance rule**: for browser-facing HTTP services, two instances must
  not share a hostname and differ only by port (cookies ignore ports; two T3 instances
  clobbered each other's login). SSH is fine with host:port. Consequence: port forwards
  are the v1 answer for SSH + dev servers, but the design must leave room for per-VM
  hostnames (wildcard DNS / reverse proxy) for cookie-sensitive services like T3/serve-web.
- **Canonical state is host-side**: VM-local edits to `/opt/construct/repo` are
  overwritten by reprovision. The service/registry must be the source of truth for
  remote-VM metadata.
- **Enterprise trajectory** (company fork `docs/company-hosting-plan.md`): Proxmox VE,
  one VM per developer, OIDC via Keycloak→LDAP. Our token/Kerberos auth layer must be
  pluggable enough that an OIDC validator can be added without reshaping the API.
- **Secret store** (`construct-secret-store.md`): a future separate service, but it will
  co-locate with this host service. Don't bake secrets authority into the extension.

## 4. Target architecture

### 4.1 Layer model

```
┌────────────────────────── user's machine ──────────────────────────┐
│ VS Code extension (UI, per-instance)   PowerShell scripts (CLI)    │
│           │ instance registry (instances.json)                     │
│           ▼                                                        │
│   shared core: provisioning client · config sync · backup/restore  │
│   client config (SSH config, VS Code, OpenCode)                    │
│           │ driver contract                                        │
│    ┌──────┴────────┐                                               │
│    ▼               ▼                                               │
│ hyperv-local   hyperv-remote ────HTTP+Negotiate/token────┐         │
│ (PS module,    (thin API client)                         │         │
│  today's code)                                           │         │
└──────────────────────────────────────────────────────────┼─────────┘
                                                           ▼
                            ┌────────── remote Hyper-V host ────────┐
                            │ constructd (.NET minimal API service) │
                            │  auth · users · quotas · job engine   │
                            │  port-forward allocator · VM registry │
                            │  wraps: build-autoinstall-iso (WSL),  │
                            │  Create-AgentVM, power, checkpoints   │
                            │        ┌─────────┬─────────┐          │
                            │        ▼         ▼         ▼          │
                            │      VM #1     VM #2     VM #3        │
                            │   (internal NAT switch, SSH fwd'd)    │
                            └───────────────────────────────────────┘
```

The **guest payload is identical in every mode** — same ISO build inputs, same
`provision.sh` contract, same `/opt/construct` layout. Only *who calls the hypervisor*
and *what address the client dials* change.

### 4.2 Driver contract

A small capability-flagged interface. On the PowerShell side a driver is a module
exposing a fixed function set; on the extension side a JS dispatch keyed by the
instance's `backend`. Operations:

| Op | Notes |
|---|---|
| `Test-Prereqs` / `Ensure-Prereqs` | local: Hyper-V feature, WSL; remote: API reachable + auth |
| `New-Vm(descriptor)` | descriptor: name, cpu, ramGB, diskGB, installIsoRef, nested, autoCheckpoints |
| `Remove-Vm(name)` | includes disk chain cleanup |
| `Start-Vm` / `Stop-Vm` / `Get-VmState` | state: running/off/paused/unknown |
| `Get-VmEndpoint(name)` | → `{sshHost, sshPort}` — local: `<name>.mshome.net:22`; remote: `serviceHost:allocatedPort` |
| `Wait-VmReachable(name, deadline)` | replaces the raw socket poll in Create-AgentVM |
| capability: `checkpoints` | local Hyper-V: yes (existing scripts); remote: yes via API; Proxmox later: snapshot mapping |
| capability: `console` | local: vmconnect; remote/Proxmox: URL or "none" |
| capability: `suspend` | `Save-Vm`/resume-on-start; Hyper-V: `Save-VM` (state `saved`, RAM freed, `Start-VM` resumes); Proxmox later: suspend-to-disk. `Get-VmState` gains `saved`. |

Explicitly **not** in the contract: ISO building (shared layer above drivers), in-guest
provisioning, client config. `Get-VmEndpoint` is the key abstraction: everything
downstream (provisioner, extension SSH, probes, usage) dials an endpoint, never a name
convention. Proxmox sanity check: every op maps 1:1 onto the Proxmox REST API
(`qemu` create/status/start/stop, snapshot, cloud-init ISO via storage upload), which is
why the remote-Hyper-V API below deliberately mirrors that shape.

### 4.3 Instance registry (client-side)

`%LOCALAPPDATA%\The-Construct\instances.json` (next to the existing `config\` dir):

```jsonc
{
  "version": 1,
  "defaultInstance": "agent-vm",
  "instances": {
    "agent-vm": {                       // implicit; synthesized if file absent
      "backend": "hyperv-local",
      "vmName": "Agent-VM",
      "sshHost": "agent-vm.mshome.net", "sshPort": 22,
      "hostAlias": "agent-vm",
      "keyName": "agent_vm_ed25519",
      "scriptsDir": null                // null = newest install (today's behavior)
    },
    "work-vm": {
      "backend": "hyperv-remote",
      "service": { "url": "https://buildbox.example.local:7462", "auth": "negotiate" },
      "vmName": "work-vm",              // name on the remote host, unique per host
      "sshHost": "buildbox.example.local", "sshPort": 2201,
      "hostAlias": "work-vm",
      "keyName": "construct_work-vm_ed25519",
      "owner": "DOMAIN\\christoph"
    }
  }
}
```

Rules:

- **Missing file / missing entry ⇒ exactly today's behavior** (defaults above). No
  migration, no prompts for existing installs.
- Instance name is the primary key: SSH alias = `<name>` (the first DNS label, matching
  every lib helper's derivation; the default instance is `agent-vm`), key file = `construct_<name>_ed25519` (default keeps
  `agent_vm_ed25519`), config-sync branch = `vm` for the default and `vm-<name>` otherwise
  (a slash form like `vm/x` cannot coexist with the existing `refs/heads/vm` file), notification spool and sync locks keyed by name.
- The SSH-config writer **appends/replaces only its own alias block per instance**
  (the block-walking parser already does per-alias replacement — the bug is that
  everything uses the same alias today).
- The registry stores **no secrets**. Tokens go in Windows Credential Manager
  (`cmdkey`-style via .NET `CredentialManager` / DPAPI file fallback); Kerberos needs no
  stored secret.
- The extension gains an **instance picker** (status bar + panel dropdown). All
  `resolveCfg` call sites pass the active instance's cfg. Per-window active instance,
  persisted in workspace state; panel sections render for the active instance.

### 4.4 Remote host service (`constructd`)

**Stack**: ASP.NET Core minimal API, .NET 8 LTS, single self-contained exe, runs as a
Windows service (`sc create`), new top-level `service/` directory in the repo. Uses
`Microsoft.AspNetCore.Authentication.Negotiate` for Kerberos/NTLM and a custom bearer
scheme for tokens. TLS with a self-signed cert generated at install; the client pins its
thumbprint at enrollment ("add remote host" flow shows the fingerprint once).

**Execution layer**: the service does *not* reimplement VM logic. It invokes the same
repo scripts (`build-autoinstall-iso.sh` via `wsl.exe`, `Create-AgentVM.ps1
-VmName <n> -SwitchName <internal> -NoProvision`, power/checkpoint cmdlets) through a
job engine: every long operation is `POST → 202 + jobId`, progress streamed via
`GET /jobs/{id}/events` (SSE) and polled state, mirroring the sentinel/result-file
discipline the repo already uses. The service keeps its own copy of the repo payload
(admin updates it with the service installer / `constructd update`).

**Data** (`C:\ProgramData\Construct\service\`): SQLite (or JSON-file store, decided at
implementation) holding users, tokens (hashed), VMs {name, owner, created, specs,
sshForwardPort, extraForwards[], vmTokenHash}, quotas, audit log (who did what when —
the enterprise-readiness table stake).

**API sketch** (all under `/api/v1`):

```
GET  /whoami                         → resolved identity + role
# admin
POST /users {name, role, quota}       DELETE /users/{name}
POST /users/{name}/tokens             → one-time-visible token
GET  /audit
# user (owns only their VMs; admin sees all)
GET  /vms                             POST /vms {name, cpu, ramGB, diskGB, opts}  → job
GET  /vms/{name}                      DELETE /vms/{name}                          → job
POST /vms/{name}/power {action}       GET  /vms/{name}/state
GET  /vms/{name}/endpoint             → {sshHost, sshPort}
POST /vms/{name}/checkpoints {…}      (capability-gated)
GET  /jobs/{id}  /jobs/{id}/events    (SSE)
# port forwards (user-auth OR the VM's own scoped token); target host|client — only
# target defaults to client (relayed to the owner's extension, opened on the user PC);
# target=host forwards are materialized by the service (netsh) — see §4.6
GET    /vms/{name}/forwards
POST   /vms/{name}/forwards {vmPort, label, target}   → {publicPort?, url?}
DELETE /vms/{name}/forwards/{id}
# idle policy (user sets; admin config caps/defaults) + activity heartbeat (VM token)
GET/PUT /vms/{name}/idle-policy               → {timeoutMinutes, action: save|shutdown|off}
POST    /vms/{name}/activity                  → guest heartbeat {busy, reasons[]}
```

**VM creation flow (hybrid split)**:

1. Client `POST /vms` → service builds the autoinstall ISO (WSL, `VM_HOST=<name>`),
   creates the VM on an **internal NAT switch** it owns (created at service install;
   the "Default Switch" stays untouched for local mode), waits for SSH-up exactly like
   `Create-AgentVM.ps1` §8a does, allocates the VM's SSH forward from a configured range
   (e.g. 2201–2299), generates + injects the scoped VM token config, and completes the
   job with the endpoint.
2. Client runs **`Provision-AgentVM.ps1 -VmHost <serviceHost> -SshPort <fwd>
   -HostAlias <name> -LocalKeyName construct_<name>_ed25519`** — the same
   bootstrap-key negotiation, payload upload, config-sync tick, `provision.sh`, restore,
   and host-side client wiring as today, just over host:port. (New: `-SshPort` threading
   through every ssh/scp/keyscan call — today port 22 is implicit.)
3. Provisioning writes the client-reachable identity into the guest
   (`CONSTRUCT_EXTERNAL_HOST=<serviceHost>`, `CONSTRUCT_EXTERNAL_SSH_PORT=<fwd>`,
   port-map for banner URLs) so guest-printed URLs are actually dialable — replacing the
   guest-side `$(hostname).mshome.net` concatenation (locally these default to the
   mshome name, unchanged output).

**Port forwarding mechanism**: the service manages `netsh interface portproxy` rules
(host LAN IP:publicPort → VM internal IP:vmPort) and reconciles them against its DB at
startup (netsh rules survive reboots; reconciliation heals drift and VM IP changes via a
static DHCP lease or IP re-resolution on VM boot). Forward state lives in the service ⇒
survives the user's PC being off (PC-independence).

**Auth flow in clients**:
- PowerShell: `Invoke-RestMethod -UseDefaultCredentials` (Negotiate, process identity);
  on 401 → prompt for domain user+password → explicit `-Credential`; token mode: bearer
  header from Credential Manager.
- Extension (Node): tries a tiny PowerShell helper for Negotiate (same pattern as
  vmpower's encoded-command spawns) since Node lacks native SSPI; token mode is pure
  HTTPS. Failing both, it prompts and stores per the user's choice.
- Design note for Proxmox/OIDC later: auth is an injectable "credential provider" per
  instance (`negotiate` | `token` | future `oidc` | future `proxmox-token`); the driver
  API client takes a provider, not a hardcoded scheme.

### 4.5 Installer UX (one entry point, mode prompt)

The one-liner / `Auto-Install.ps1` stays the single front door. New flow:

1. **Mode prompt** (TUI, only when no default instance exists yet — existing installs
   see zero new prompts): `Local Hyper-V install` (default, today's path verbatim) or
   `Remote host install`.
2. Remote path: prompt for the **service hostname/URL** → fetch + show the cert
   fingerprint once → **auth**: try Windows/Kerberos as the current user automatically;
   on failure offer `token` (paste, stored in Credential Manager) or `domain user +
   password`. `GET /whoami` confirms identity and quota.
3. Prompt for **instance name** (unique on that host) and the usual RAM/disk/profile
   questions — same TUI block as today, gathered up front.
4. `POST /vms` and stream the service job (ISO build → create → OS install wait →
   SSH forward allocated) into the same scrolling log the local path shows.
5. On job completion, the installer runs **the regular `Provision-AgentVM.ps1` from the
   user's machine straight into the VM** over the returned endpoint — required anyway,
   since provisioning also configures the user machine (SSH config + key, VS Code
   Remote-SSH settings, OpenCode server entry, SMB mapping, extension wiring).
6. Registry entry written; extension picks the new instance up and offers connect.

Reprovision/reinstall/export from the menu or panel work per-instance: lifecycle ops on
the VM object go through the driver (API for remote), provisioning always runs
client-side.

### 4.6 In-VM self-serve CLI (`construct expose`) — two forward targets

`construct expose 3000 [--label "vite dev"] [--to client|host]`, plus `--list` /
`--close`. Two **separately configurable** targets:

- **`--to client` (the default, and the agents' main path)** — the forward lands on the
  **user's PC**: the extension opens a local port and tunnels it over the existing SSH
  connection (`ssh -L`-style, the reverse of what mic passthrough does). The CLI prints a
  link pointing *there* (`http://localhost:<port>/`, or the user PC's hostname when the
  extension reports one) for the agent to hand to the user. Private to the user's
  machine, works identically in local *and* remote mode, needs no LAN exposure — but
  lives only while VS Code runs.
- **`--to host`** — only when something must be **externally reachable** (webhooks,
  another machine, a teammate): the service materializes a LAN-reachable port on the
  service host (netsh, §4.4). PC-independent: survives the user's PC being off. Admin
  can cap/disable host-target forwards per user. On local Hyper-V it degrades to
  printing the `<host>.mshome.net:<port>` URL (NAT already reachable).
- **Documentation policy**: the default agent instructions (`config/systemprompt.md` /
  the generated agents file) mention only `construct expose <port>` → "give the user the
  printed link". Host forwarding is documented **only in the CLI's own `--help`/docs**,
  not in the default agent instructions, so agents don't reach for LAN exposure by habit.
- **Request plumbing**: the CLI records the request via the scoped VM token — remote:
  `POST /vms/{self}/forwards`; the service materializes `host` targets itself and relays
  `client` targets to the owner's extension (picked up with the instance status probe).
  Local mode has no service, so requests go to a guest spool (`/etc/construct/forwards/`)
  that the extension watches over the existing inotifywait SSH stream — the proven
  notification-spool pattern. The CLI blocks briefly until the extension acknowledges
  the client forward (so the printed link is live), with a clear message if no client is
  attached.
- The scoped VM token authorizes **only** forward management + activity heartbeat for
  that one VM, capped count/range.
- Idle interplay: a `client` tunnel rides the VM's SSH connection, so the service's
  connection counting (§4.7) still sees it on the SSH forward port.
- The CLI becomes the one place that answers "what URL does the user open?" — replacing
  scattered banner logic over time.

### 4.7 Idle detection & auto-save

Remote VMs consume host RAM whether or not anyone is using them. Per-VM idle policy,
enforced by `constructd` (host-side, so it works with the user's PC off — and the same
logic applies unchanged to a future Proxmox driver):

- **Policy**: `{timeoutMinutes, action}` per VM. `action`: `save` (default — Hyper-V
  `Save-VM`: state to disk, RAM freed, resume is transparent), `shutdown`, or `off`
  (never idle out). The **user** sets their own VMs' policy (extension panel + API);
  the **admin** sets the service-wide default and an optional cap (max timeout /
  forced-on) in the service config.
- **Idle means BOTH of these, continuously for the timeout window**:
  1. **No client connections** — the service owns every forward (SSH, and later web),
     so it counts live proxied TCP connections per VM: no SSH session, no VS Code
     Remote-SSH, no T3/opencode/serve-web connection through any forward.
  2. **No in-guest activity** — a small guest reporter (systemd timer under the
    existing `construct` service, using the scoped VM token channel from §4.6) posts a
    heartbeat: `busy` when agent processes are actively working (claude/codex/opencode
    processes with recent CPU time), tmux panes have recent output activity, t3code/
    opencode servers have active tasks, or provisioning is running. **An agent running
    a long unattended job keeps the VM alive even with zero connections** — that's the
    whole point of unattended agents. Missing heartbeats (crashed guest) count as idle
    only after a grace multiple.
- **Enforcement**: a service scheduler evaluates policies each minute; on timeout it
  runs the driver's `Save-Vm` (or shutdown), audit-logs it, and marks the VM `saved`.
- **Wake**: any user action in the extension/API (`connect`, power start) resumes via
  `Start-Vm`. Stretch (recorded, not v1): the service's port proxy can detect an
  incoming connection to a `saved` VM's forward, resume the VM, and complete the
  connection — "wake-on-SSH".
- **Local Hyper-V**: not enforced in v1 (no always-on service locally); the policy
  field exists in the registry so a later local agent could honor it.

### 4.8 Client tool modules — extension features that must stay relocatable

Several extension features are really **client-side services that happen to live in the
extension today**. Per the recorded secret-store decision (authority must be able to
move out of the extension so a host/always-on service can take it over), these are
structured as self-contained modules behind narrow interfaces, so any of them can later
be lifted into a dedicated process (a local daemon, or `constructd`) without rewriting:

- **Port forwarder (client target)** — new in B8; built module-first from day one.
- **Mic passthrough** (`audio.js`) — existing; conforms gradually, no rewrite.
- **Password vault / secret store** — future; must *start* as a module-consumer design.
- **Notification listener** (`notify.js` inotify stream) — existing; same treatment.

Module rules (enforced in review for new code, adopted opportunistically in old):

1. **Core logic free of VS Code API** — `vscode.*` only in a thin adapter (commands,
   status bar, settings read); the module core takes plain callbacks/config.
2. **Transport injected, not owned** — modules receive an SSH/exec/tunnel provider
   (today `ssh.js` with the instance cfg; later a daemon RPC) instead of spawning
   their own connections.
3. **Own state + config namespace** per module, keyed by instance — no reads of
   another module's files or globals.
4. **A documented contract** (inputs, spool/file formats, ports) — the spool formats
   (§4.6 forwards, notification spool) are the wire protocol a future service would
   speak; changing them is a versioned decision, not an implementation detail.

### 4.9 What stays design-only (Proxmox + enterprise)

- **Proxmox driver**: contract mapping documented (§4.2); implementation deferred. The
  ISO path maps to Proxmox's ISO storage upload + ide2 media; endpoint = Proxmox node
  IP + forwarded/bridged port; auth = Proxmox API token via the credential-provider seam.
- **OIDC/Keycloak**: an additional auth scheme in `constructd` later; API/ownership
  model already user-keyed so it slots in.
- **Per-VM hostnames for cookie-sensitive HTTP** (T3, serve-web): out of scope for v1
  forwards; the recorded upgrade path is a reverse proxy + wildcard DNS on the service
  host. Registry/API leave room (`url` on forwards is a field, not a format).
- **Secret store service**: separate effort; will co-locate with `constructd`.

## 5. Implementation batches

Ordered for the parallel-worktree pipeline; ownership is file-disjoint per phase so
chains don't collide. Every batch keeps the zero-change default green (existing
single-VM install must behave identically — that's the regression bar for review).

**Phase 1 (parallel):**
- **B1 — Parameterize host-side identity.** `-VmName`/`-VmHost`/`-HostAlias`/
  `-LocalKeyName`/**new `-SshPort`** threaded through `Create-AgentVM.ps1` (promote
  `$VmName` to param), `Auto-Install.ps1`, `Provision-AgentVM.ps1`,
  `Set-AgentVmCheckpoints.ps1`, `Get-AgentUsage.ps1`, `Update-T3Code.ps1`; fix the
  hardcoded key path in `AgentVm.Common.ps1:3355`; per-alias SSH-config blocks.
  Defaults = today's literals.
- **B2 — De-Hyper-V the guest.** `CONSTRUCT_EXTERNAL_HOST`/`_SSH_PORT`/port-map in
  `config.env`; consume in `print-connection-info.sh`, `install-vscode.sh`,
  `install-ai-tools.sh`, `setup-root-ssh-key.sh`, `setup-smb-share.sh`. Default =
  `$(hostname).mshome.net` ⇒ byte-identical local output.

**Phase 2 (parallel, after 1):**
- **B3 — Instance registry + extension multi-VM.** `instances.json` + PS/JS readers,
  `resolveCfg` wiring at every extension call site, instance picker UI, per-instance
  lifecycle invocations (`buildInvocation` emits target args), per-instance mic-tunnel
  ranges + notification spool keys, `findScriptsDir` per-instance override. Establishes
  the client-tool module conventions (§4.8) — the transport-provider interface lands
  here, since instance-keyed transport is what this batch builds anyway.
- **B4 — Driver extraction.** `drivers/hyperv-local` PS module absorbing Create's
  Hyper-V calls, `Remove-AgentVm`, checkpoint lib, power; `vmpower.js` and
  `lifecycle.js` route through a JS driver dispatch (local driver = today's spawns).
  Contract doc + capability flags.
- **B5 — Config-sync instance keying.** Branch `vm-<name>` (default stays `vm`), locks,
  restore/export paths keyed; both engines (PS + `configsync.js`).

**Phase 3:**
- **B6 — `constructd` service** (independent of 3–5, can start with Phase 2): API,
  Negotiate+token auth, users/tokens/quotas/audit, job engine wrapping the scripts, port
  allocator + netsh reconciliation, admin CLI, service installer (`Install-ConstructHost.ps1`:
  prereqs incl. WSL, cert, internal switch, service registration). Includes the idle-policy
  engine (§4.7): per-VM connection tracking on the forwards, heartbeat intake, scheduler
  → `Save-VM`/shutdown, admin defaults/caps.

**Phase 4 (after B3/B4/B6):**
- **B7 — `hyperv-remote` driver + end-to-end remote flow.** PS+JS API clients with the
  credential-provider seam, "Add remote host" + "New VM on host" extension flows,
  hybrid create→provision orchestration, Kerberos fallback prompt.
- **B8 — `construct expose` CLI + scoped tokens + idle reporter.** Guest CLI subcommand
  with both targets (`--to client|host`, client default; host only when explicitly external), token injection, forward
  endpoints hardening (scope enforcement, caps, per-user host-target policy), guest
  forward spool for local mode, the extension's **client-forwarder module** (built to
  the §4.8 module rules: injected transport, own spool contract), systemprompt note.
  Plus the guest activity heartbeat (§4.7): systemd timer detecting agent-process
  CPU activity, tmux output movement, t3code/opencode task state → `POST /activity` with
  the same scoped token. Extension: idle-policy control in the panel + `saved` state
  rendering ("Resume & connect").

**Phase 5:**
- **B9 — Docs + field test.** New `docs/remote-host.md`, updates to installation/
  manual-setup/ARCHITECTURE, README; field test on the home domain (Kerberos both
  paths, two VMs on one host, expose flow, PC-off survival of forwards).

**Known risks to watch in review:** version-skew discipline on every new parameter
(probe before splat); SSH-config block collisions between instances; Windows OpenSSH
key-ACL handling on new key names; netsh portproxy + VM IP churn; Negotiate from a
non-elevated extension host; keeping `provision.sh`'s contract untouched; idle
connection-counting with netsh portproxy (no per-rule stats — count established entries
in the host TCP table on the forward ports; if that proves flaky, or for wake-on-SSH,
switch to an in-process TCP proxy, which is a contained change inside the service);
false-idle saves killing long agent jobs (heartbeat `busy` semantics must be generous
and reviewed as make-or-break).

**Recorded follow-ups from the Phase 1 integration review (not blocking):** IPv6
zone identifiers (`fe80::1%12`) are not `%25`-encoded in guest-printed URLs, and the
SMB UNC (guest banner, `smb-status`, host auto-mount, provisioner summary) is built from
the raw host, which Windows cannot use for IPv6 literals — derive one Windows-compatible
SMB endpoint (or suppress SMB instructions when none exists) when the remote flow (B7)
defines how SMB is reached through the service host. The host side has the same gap:
`Provision-AgentVM.ps1` builds the SMB UNC and the OpenCode/summary URLs from the raw
`-VmHost` (ignores the guest-reported `SMB_DNS`, no IPv6 bracketing) — centralize
endpoint formatting there in the same batch.


## 6. Progress log

| Date | Milestone |
|---|---|
| 2026-09-01 | Phase 1 (B1 host identity params, B2 guest external host) merged; integration review closed after 8 rounds (`d0b1eff`, follow-ups `f132cc4`). |
| 2026-09-01 | Phase 2 (B3 registry + extension multi-VM, B4 driver extraction, B5 config-sync branch keying) merged (`d43fe79`); integration review rounds 1–5 fixed and merged through `f28b3f0`; round 6 fix pair in flight. |
| 2026-09-01 | B6 `constructd` skeleton merged (`c9c8287`): .NET 10 solution, contract-first, fakes, 223 tests. |
| 2026-09-02 | B8g guest side merged (`d108fd1`): `construct expose` (client default, spool contract in `docs/expose.md`, remote API mode), idle heartbeat reporter. |
| 2026-09-02 | B6b merged (`0e2ea00`): Hyper-V driver over the PS contract, WSL ISO builder, netsh forwarder with reconcile + connection counting, `service/host/Install-ConstructHost.ps1`, admin CLI; console capability is a kind. |
| 2026-09-02 | B7 merged (`c8f390c`): hyperv-remote PS/JS drivers, `lib/AgentVm.Remote.ps1` (Negotiate/token/credential providers, DPAPI token store, cert pinning), Auto-Install mode prompt + remote flows, extension remote-host flows, `docs/remote-host.md`, remote e2e test. |
| 2026-09-02 | Phase 2 review round 6 fixes merged (`4c8e654`): mic re-arm sequenced across instance switches (handover chain, identity-gated status), honest switch-persistence reporting, registry endpoint uniqueness = composite (sshHost, sshPort) in both readers. Round 7 review submitted. |
| 2026-09-02 | B8x merged (`1626d6f`): `extension/src/forwarder.js` (pure planner, injected transport; inotify spool watch local / 10 s poll remote) + `forwarder-ui.js`, service client-forward ack relay (`POST /vms/{name}/forwards/{id}/ack`, VM token excluded), Forwards + idle-policy panel cards, saved → "Resume & connect". 412 dotnet tests, 21 node files. |
| 2026-09-02 | Phase 2 review round 7 fixes merged (`b5f9051`): installer remote flow uses the shared registry collision logic (pre-create identities, post-create composite endpoint), hyperv-remote canonical `vmName === name`, case-variant backend ids fail closed, one mic-session chain incl. cancellable `HostAudio` enable, remote entries without `sshHost` rejected whole. |
| 2026-09-02 | Phase 2 review round 8 fixes merged (`93b1f68`): client forwarder is lazy + guest-gated (starts from the existing status probe once the VM is reachable, one capability exec for the spool markers, watcher argv documented as the one default-path addition), forwarder start/stop on the shared handover/session-owner chain, git-invalid branch names rejected in all three validators. |
| in flight | Phase 2 review round 9; B9 docs pass on `docs/b9`. Remaining: field test on the home domain. |

Process notes: every package ran as an omniloop dev/reviewer pair (opus developer,
gpt-5.6-sol reviewer) in its own worktree; cross-package integration reviews ran on the
merged tree with the zero-change default path as the primary bar. Workflow budgets must
be ≥ 720 min because a usage-limit park does not pause the workflow-level timeout.
