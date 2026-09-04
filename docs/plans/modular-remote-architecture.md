# Modular / Remote Architecture Plan

Status: **in execution** · plan 2026-09-01, progress log at the end
Scope decided with the project owner via Q&A; findings below come from a four-way repo/notes audit
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
| Auth | **Kerberos/Negotiate first** (VS Code process identity, fallback: manually entered domain user+password), **admin-issued tokens** as the alternative. Admin explicitly adds users; per-VM ownership; per-user quota. Testable on the project owner's home domain. |
| VM reachability | **Host-service port forwards** (per-VM SSH port on the host's LAN address). Plus an **in-VM CLI** (`construct expose <port>`) so agents can self-serve additional forwards for dev servers etc. |
| VM-CLI auth | **Scoped per-VM token** injected at provision time; valid only for that VM's own forward management. |
| Provisioning split | **Hybrid**: service does ISO build + VM create + OS install wait; the **client** runs the agent-stack provisioning (`Provision-AgentVM.ps1`) over the forwarded SSH port — user secrets (git creds, agent auth, backups) never transit the service. |
| ISO build on remote host | ~~WSL checked/used under LocalSystem~~ **Superseded 2026-09-02 (field test):** WSL 2.x refuses LocalSystem (`WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED`). The service stays LocalSystem and consumes a **pre-built autoinstall ISO** that the installing administrator builds interactively with their own WSL (`admin iso build`); the guest takes its hostname from the Hyper-V VM name at first boot (KVP). Proxmox later builds natively. See §4.10. Target design: pluggable ISO build strategy + hypervisor-supplied guest identity (§4.11). |
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
| capability: `checkpoints` | local Hyper-V: yes (existing scripts); remote: **not in this release** — both hyperv-remote drivers declare `Checkpoints=false` and `constructd` exposes no checkpoint endpoints (see `docs/drivers.md`); Proxmox later: snapshot mapping |
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
      "owner": "DOMAIN\\alice"
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
POST /vms/{name}/checkpoints {…}      (capability-gated; NOT implemented in this release)
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

### 4.10 Pre-built autoinstall ISO, service stays LocalSystem (B10 — a stopgap; the target design is §4.11)

**Field finding (2026-09-02, `standpc`, WSL 2.6.3):** `wsl.exe` as LocalSystem exits -1 with
`Wsl/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED`; a probe as a domain account in a batch logon did not even
produce output. Decision (project owner): **do not change the service identity.** The installing
administrator builds the autoinstall ISO **once, interactively, with their own WSL**; `constructd`
(LocalSystem) only consumes it. Updating the ISO (new Ubuntu release, new bootstrap key) is again an
interactive administrator action. The per-VM WSL build stays in the code behind a mode switch for
hosts whose service identity can run WSL (a future service-account or Proxmox host).

**Hard constraint discovered on the way:** the service finds a new VM through the driver contract's
`Get-ConstructVmEndpoint` → `<vmName>.mshome.net`, both for `Wait-ConstructVmReachable` and for the
netsh forward's `connectaddress` (resolved by DNS). The Default Switch's DNS registers the **guest's
own hostname**. A generic ISO therefore cannot bake a fixed hostname: the guest must adopt the Hyper-V
VM name at first boot. Hyper-V exposes it through KVP data exchange (`hv_kvp_daemon`, pool file
`/var/lib/hyperv/.kvp_pool_3`, key `VirtualMachineName`).

| Piece | Rule |
|---|---|
| `bin/build-autoinstall-iso.sh` | New opt-in mode `VM_HOSTNAME_SOURCE=hyperv-kvp`: `identity.hostname` is the placeholder `construct-seed`; `packages: [linux-cloud-tools-virtual]` (provides `hv_kvp_daemon`); late-commands install a first-boot oneshot unit `construct-hostname.service` that waits (bounded) for the KVP pool, reads `VirtualMachineName` (512-byte key / 2048-byte value records, NUL-padded), lowercases + validates it as a DNS label, `hostnamectl set-hostname`, fixes `/etc/hosts` 127.0.1.1, renews DHCP (`networkctl renew` / `netplan apply`) so the Default Switch DNS learns the name, writes a marker and disables itself. With `VM_HOST` set (every local install) the rendered user-data is **byte-identical** to today. |
| Seed identity | `SeedUser` (config, default `construct`) + a random password generated at build time and discarded (nobody logs in with it; the client provisions with the committed bootstrap key), bootstrap pubkey from `Constructd:Iso:BootstrapPublicKeyPath`. |
| Service | `IsoOptions.Mode = Prebuilt` (default) \| `PerVm` (today's `WslIsoBuilder`). `PrebuiltIsoBuilder : IIsoBuilder` returns the current pre-built ISO (pointer/sidecar in `CacheDir`), validates existence + sidecar, reports what it uses (built when, from which source ISO, key fingerprint), ignores `vmName`/`seedPassword`; missing ISO → `IsoNotBuiltException` whose safe text tells the admin the exact command. Composition picks the builder by mode; fakes untouched. |
| Admin CLI | `admin iso build [--force]`, `admin iso status`, `admin iso prune`: runs `WslIsoBuilder` **as the interactive admin** with the KVP mode; output is a **versioned** file `construct-autoinstall-<utc>.iso` + sidecar JSON, then the pointer is swapped atomically (a running install keeps the old ISO attached and Hyper-V holds a handle on it, so never overwrite in place); `prune` removes unpointed ISOs no VM has attached. Needs WSL + xorriso/whois in the admin's distro; downloads/verifies the source ISO into `CacheDir` via the existing `IIsoDownloader` when `SourceUrl` is set. |
| Installer | Stays LocalSystem. Removes every LocalSystem-WSL step (`Invoke-AsLocalSystem wsl`, `-ProvisionWslForService`, `<service root>\wsl`, "ensure xorriso as LocalSystem"); keeps the interactive WSL check and ensures xorriso + whois in the **admin's** distro. New step after `appsettings.Production.json` and before service registration: **"Building the autoinstall ISO (as you, via WSL)"** = `<PublishDir>\Constructd.Api.exe admin iso build`; `-SkipIsoBuild` to defer, `-IsoBuildOnly` to (re)build on an existing install; prints the rebuild command in the enrollment summary. The one-shot task runner stays only for anything still needed as SYSTEM (may be removed if unused). |
| Provisioning (belt and braces) | `bin/provision.sh`: if the hostname is still `construct-seed` and `CONSTRUCT_INSTANCE_NAME` is set, set the hostname to it. Default path untouched. |
| Docs | README requirement line (WSL is needed by the **administrator** for the ISO build, not by the service); `service/README.md` (ISO section, `admin iso`, identity section corrected); `docs/remote-host.md`; `docs/field-test-remote-host.md` §1.1 → build the ISO as the admin, verify `admin iso status`; `docs/installation.md` remote paragraph. |
| Tests | dotnet: `PrebuiltIsoBuilder` (present / missing / stale sidecar), mode wiring, `admin iso build` through fakes (versioned name, atomic pointer swap, refuses to overwrite an attached ISO), `SafeError` for the new exception. Installer suite: step order (config → ISO build → service), no LocalSystem WSL step remains, `-SkipIsoBuild`/`-IsoBuildOnly`. bash: `test/autoinstall-iso.test.sh` renders user-data in both modes (default byte-identical vs a pinned fixture; KVP mode contains unit + package) and unit-tests the pool-file parser on a synthetic `.kvp_pool_3`. |

**Open risk (field):** `linux-cloud-tools-virtual` must be installable during autoinstall (network via
the Default Switch NAT is available) and `hv_kvp_daemon` must populate pool 3 on Gen2 VMs — the
checklist verifies `hostname` inside the first VM before anything else.

### 4.11 ISO build strategy (target design; direction from the project owner, 2026-09-02)

The pre-built ISO of §4.10 is **not** the main design. Building install media must be a pluggable
**strategy**, and per-VM guest identity must come from the **hypervisor's own channel**, so that:

| Strategy (`Constructd:Iso:Mode`) | Where the ISO is built | Status |
|---|---|---|
| `Prebuilt` | by an interactive administrator, once; the service consumes a catalog entry | **now** (B10) |
| `PerVm` (WSL) | by the service through `wsl.exe`, per VM | exists; needs a WSL-capable service identity |
| `Native` | in-process on Windows (.NET): remaster the stock ISO without xorriso | planned |
| `InGuest` | inside an existing Construct VM over SSH (xorriso is there); the service copies the result back — this is how the system **self-updates its install media** and fetches new source ISOs for new installs | planned |
| `HypervisorHost` | natively on the hypervisor host: Proxmox (xorriso on PVE) or any Linux host; the regular autoinstall path when Hyper-V is replaced | planned (Proxmox) |

Rules that every strategy and B10 must respect:

- **One seam:** `IIsoBuilder` + an `IsoCatalog` (versioned files, pointer, sidecar, prune) that any
  builder publishes into; composition maps mode → builder in one place; `admin iso build` is a
  thin driver over "a builder + the catalog". Builders do not know about each other.
- **Generic media, hypervisor-supplied identity:** the autoinstall user-data never bakes a per-VM
  hostname. The guest adopts its identity at first boot from a pluggable **identity source**
  (`VM_HOSTNAME_SOURCE`): `hyperv-kvp` now; `cloud-init-metadata` for Proxmox / NoCloud /
  ConfigDrive, where the hypervisor already supplies per-VM identity natively. The first-boot
  unit is "source → hostname" with the source isolated, so a second source is additive.
- **Driver contract stays clean:** neither `IHypervisorDriver` nor the PowerShell driver contract
  grows KVP or ISO-format assumptions; a driver only receives an `IsoPath` (or, on Proxmox, a
  cloud-init drive) from the job.
- **Source ISO acquisition** stays admin-configured (`SourceUrl` + `Sha256`) and reusable by every
  strategy, including `InGuest` downloading inside the VM.

### 4.12 Multi-instance client completion (decisions with the project owner, 2026-09-03)

Phase 2 shipped the registry, the per-window picker, Remote-SSH adoption and one serialized
retarget chain, and the host side already keys the SSH block, the VS Code `remotePlatform` map
and the OpenCode server list by alias. What is **not** multi-instance yet was audited on
2026-09-03 and comes down to five gaps; the decisions below close them. As everywhere in this
plan, the zero-change default path is the regression bar: an install with one local `agent-vm`
must behave and print exactly as today.

**The five gaps**

1. **Local VMs never enter the registry.** Only the remote path calls
   `Save-ConstructInstanceEntry`; a second local VM is a hand edit, so most users never see the
   picker.
2. **All VM-scoped settings live in one file per scripts checkout.** `.construct-settings.json`
   holds `provisionedCommit`, mic preference, project selection, T3 toggles/channel and patch
   toggles for *every* VM. `provisionStale` is therefore a per-install signal, not per VM.
3. **T3 Code Desktop is a singleton.** One install, one CA file name, one manifest; the updater
   targets `defaultInstance` instead of the VM it is paired to; two VMs on different channels
   flip-flop the install on every reprovision.
4. **Web ports never reach a remote VM.** The service forwards SSH only, so the OpenCode entry
   written for a remote instance points at a dead URL and T3 needs `construct expose` by hand.
5. **Host scripts take four identity arguments, not a name.** Every caller re-derives
   `-VmHost -HostAlias -SshPort -LocalKeyName` and probes each one for version skew.

**Decisions (settled)**

| Topic | Decision |
|---|---|
| Registry for local VMs | `Auto-Install.ps1` writes the entry for a **local** install too (the default one on first run, a named one for `-InstanceName`/`-VmName`), through the shared library. `Create-AgentVM.ps1` takes the instance name, not only the Hyper-V name. |
| Name-only targeting | `Provision-AgentVM.ps1`, `Update-T3Code.ps1`, `Set-AgentVmCheckpoints.ps1`, `Get-AgentUsage.ps1` gain **`-InstanceName`**, resolved through `lib/AgentVm.Instances.ps1`. The extension and the T3 Desktop updater pass one name and probe one parameter. The explicit identity args stay for BYO/manual setups and win when given. |
| Per-VM state location | A **separate per-instance file**: `%LOCALAPPDATA%\The-Construct\instances\<name>.json`, next to the registry and independent of the scripts checkout. Holds `provisionedCommit`, mic preference, project selection, AI-tool set, T3 toggles/channel, patch toggles, T3 pairing hint. Install-wide facts (`installedCommit`, `constructRepo`, `constructRef`) stay in `.construct-settings.json`. The **default instance keeps mirroring** its legacy top-level keys in `.construct-settings.json` so old readers keep working. |
| VM-side marker | The guest records the Construct commit it was provisioned with in `/etc/construct/provisioned.env` (`CONSTRUCT_COMMIT`, from the `CONSTRUCT_VERSION` already passed in). It is the **source of truth**; the host file is a cache for offline display. The probe reads it, so a VM provisioned from another PC is judged correctly. |
| Stale detection | **Plain commit-string inequality**, never a history lookup: `installedCommit != provisionedCommit` (host cache or probed guest value). Must keep working when the Construct repo was reset/cleaned and the compare API cannot resolve the base — the banner then says "update available, unknown number of commits" (the existing 404 path) and the per-VM state still says "reprovision pending". |
| Update flow | `Update-Construct.ps1` stays install-wide (no target). Afterwards every instance whose own `provisionedCommit` differs shows the **yellow Reprovision** button when connected/switched to; the **status-bar item shows a count** of stale instances. No batch "reprovision all" for now. |
| Connected VM | Remote-SSH adoption stays **preselect, switchable**; the picker marks the attached instance as *connected*. Remaining live reads (`connect`, `openProject`, `openWebUi`, the global `lastT3WebUrl`, the global T3 enable/disable serialization in `t3code.js`) move to captured, instance-stamped targets. Attaching to a host the registry does not know offers **"register this VM"**. |
| Public hostname per VM | Two T3 web UIs on one remote host must not share a hostname and differ only by port (cookie rule, §3.3). **Service config `Constructd:PublicHostPattern`** (e.g. `{name}.vpnhost.domain`, behind a wildcard DNS record pointing at the host) → `GET /vms/{name}/endpoint` returns `publicHost` next to `sshHost`; falls back to `PublicHost` when unset. The registry stores `publicHost`; the provisioner passes it as `CONSTRUCT_EXTERNAL_HOST`, so the T3 CA/SAN, `T3CODE_PUBLIC_BASE_URL` and the printed URLs use it. Target design remains Proxmox with one LAN IP + hostname per VM; the wildcard is the Hyper-V-era bridge. |
| Web ports on remote VMs | **Host forwards for both OpenCode and T3**, requested **by the guest at provision time** (`construct expose --to host` with the VM token, one per enabled web service, idempotent). They survive PC-off and are re-created on reinstall. The provisioner reads the allocated ports back and writes the OpenCode server entry (`http(s)://<publicHost>:<port>`) and the T3 public base URL from them. A user with `--no-host-forwards` gets the entries omitted with a printed note (the extension's client forwarder remains the manual path). |
| T3 Desktop topology | **One install** (`%LOCALAPPDATA%\Programs\t3code`); T3 links **several remotes at once**, so each VM is paired as one more connection, named by instance. CA file keyed by instance (`construct-t3-ca-<name>.crt`; the store already dedupes by thumbprint). |
| T3 Desktop version tracking | The host's `installed.json` records the **T3 release version, channel and patched build hash**. A reprovision **does not reinstall** when the host already has that exact patched release; **otherwise it reinstalls** (last reprovisioned VM wins — no "newest wins", no owner instance). |
| T3 Desktop updater | Checks **every linked remote**: match each remote's base URL to a registry entry, one row per instance under Settings → Providers, **Reprovision per instance** (name-only targeting). Update Construct stays the single host-wide action. |
| Cleanup | New **Remove instance** action (panel + `Auto-Install.ps1` choice): removes the SSH block, `remotePlatform` key, OpenCode entry, T3 CA cert + pairing hint, per-instance file and the registry entry; for `hyperv-remote` it also `DELETE`s the VM after the typed confirmation. Also adds the missing **Remove Remote Host** (globalState + SecretStorage + pin). Reinstall keeps all of these. |
| Naming | **Instance name everywhere**: OpenCode `displayName`, T3 remote label, status bar, per-instance file, agent-name default `<name>-agent`. Default stays `agent-vm`. No host suffix, no free-form display name. |

**Smaller items folded in:** the client forwarder's local fallback range (18800–18815) is now
shared by all VMs — raise or allocate per instance; the SMB drive letter defaults to `Z` for every
instance; `$env:TEMP\construct-known_hosts` is shared between concurrent provisions; the
`.construct-backup` dir is per checkout, not per VM.

### 4.13 Follow-ups from the 2026-09-04 field preparation (project owner)

Decided while preparing the first haus-pc remote VM; each is a batch of its own (Phase 7),
none blocks Phase 6.

| Topic | Decision |
|---|---|
| Publish local profiles to a linked config repo (**B15**) | The "remote config repo" feature imports from an upstream and pushes back only *tracked* files, so profiles born locally can never be pushed. New **Publish** action (Projects tab + `Auto-Install.ps1`/lib function): for a linked remote, adopt selected (default: all) untracked local profiles — write the provenance manifest entry + stored base as if they had been imported from `projects/<name>.json` of that remote, commit them into the staging clone on the remote's **default branch** (not a review branch; the owner publishes to their own repo) and push. Afterwards *Push back* and *Import* work for every profile. Collision rule: a remote that already has a file of that name with different content is refused per file (import it first, then push back). GitGudLab **push-to-create** is relied on for the very first publish into an empty/nonexistent repo in the owner's namespace (user PAT, not a project token). |
| Per-VM disk location (**B16**) | `Create-AgentVM.ps1 -VmPath <dir>` (VHDX + VM config under `<dir>\<vmName>`; default = Hyper-V host defaults, byte-identical when absent); `Auto-Install.ps1` asks on the local path only when `-VmPath` is not given and more than one fixed drive exists (default: current behaviour); the service gets `Constructd:VmStorage:AllowedRoots` (list) + `DefaultRoot`, `POST /vms` accepts `storageRoot` which must be one of the allowed roots (else 400 naming them), the remote installer/extension offer the list from `GET /host/storage` and the registry entry records it. Manual local installs accept any path. |
| `construct expose` client-port leeway (**B17**) | When the requested client port is busy, the client forwarder picks a free port and the guest CLI **prints the port that was actually opened** (the forward record carries `requestedPort` + `clientPort`); a new `--strict` flag makes a busy port an error instead. Applies to the client target only; host forwards are allocated by the service anyway. |
| Host stays awake while VMs run (**B18**) | The haus-pc slept overnight with VMs expected to run. `constructd` holds a Windows power availability request (`PowerCreateRequest`/`PowerSetRequest`, SystemRequired) while any VM it manages is running and releases it when none is; installer prints the host's sleep timeouts and offers to set AC standby/hibernate to never. Until then the field-prep set the haus-pc timeouts by hand (2026-09-04). |
| GitGudLab parity | Anything an agent would do against GitLab (push-to-create, CI, MRs) is expected to work on GitGudLab; a gap is filed as a jarvis todo, not worked around silently. |

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

**Phase 6 — multi-instance client completion (§4.12, decided 2026-09-03; plan only, not started):**
- **B11 — Registry for local VMs + name-only targeting.** `Auto-Install.ps1`/`Create-AgentVM.ps1`
  write local entries; `-InstanceName` on Provision / Update-T3Code / Set-AgentVmCheckpoints /
  Get-AgentUsage resolved via `AgentVm.Instances.ps1`; `lifecycle.js` and the T3 Desktop updater
  emit the name; skew probe reduced to one parameter; "register this VM" for an unknown
  Remote-SSH host.
- **B12 — Per-instance state + VM marker + update flow.** `instances\<name>.json` (PS + JS
  readers/writers, default-instance mirroring), `CONSTRUCT_COMMIT` in `provisioned.env` and in
  the probe, per-VM `provisionStale`, stale count on the status-bar item, connected marker in the
  picker, remaining live reads moved to captured targets.
- **B13 — Public hostname + guest host forwards.** `Constructd:PublicHostPattern` + `publicHost`
  on the endpoint, registry field, `CONSTRUCT_EXTERNAL_HOST` from it; `provision.sh` requests
  host forwards for the enabled web services; provisioner reads ports back and writes the
  OpenCode entry / T3 base URL; `--no-host-forwards` degradation.
- **B14 — T3 Desktop multi-link + host version tracking + cleanup.** Per-instance CA files,
  `installed.json` (version, channel, build hash) reinstall rule, updater checks every linked
  remote with per-instance Reprovision, **Remove instance** and **Remove Remote Host**, forwarder
  range / SMB letter / temp file de-singletoning.

**Scope pass after Phase 6/7a (2026-09-04, orchestrator):** over-guards the dev/reviewer
pairs added beyond the plan were relaxed on main (explicit identity args win over the registry
with a warning, unknown name + endpoint resolves, `publicHost` problems drop the field not the
entry, Remove instance no longer wedges on advisory failures and gains `-KeepVm`, bare-user
URLs publish, invalid profiles do not fail a publish, the T3 Desktop reprovision offer for the
default instance is back, a service VM without a recorded T3 port matches when it is the only
claimant, the install rule re-installs a missing app, per-row Reprovision confirms, the guest
persists the forwarded T3 port, the power guard does not throw at service stop). Recorded
follow-ups: toggling T3 between HTTP/HTTPS leaves the previous host forward allocated (needs a
`--close` of the stale key); `Remove Remote Host` requires every instance on that host to be
removed first — use `Remove instance -KeepVm` to keep the VMs; "Register this VM" accepts a
bare ssh alias as a local host (a lost remote registry could register a local entry).

**Phase 7 — field-prep follow-ups (§4.13; after Phase 6 merges unless file-disjoint):**
- **B15 — Publish local profiles** (lib config-sync functions, `Auto-Install.ps1 -Action publish-config`, Projects-tab Publish action, docs/config-sync.md §7/§13, tests PS+node).
- **B16 — Per-VM disk location** (`-VmPath`, service allowed roots + `storageRoot`, remote installer/extension pickers, registry field, docs).
- **B17 — `construct expose` client-port leeway** (forwarder planner, guest CLI output, `--strict`, docs/expose.md).
- **B18 — Host power request in `constructd`** + installer sleep-timeout check.

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

**Recorded follow-ups from Phase 2 / B9 (not blocking):** installer prints the SHA-1 thumbprint under "clients pin this" while clients pin SHA-256 (print both); no VM-token rotation (proposed `POST /vms/{name}/token`; today a lost guest token means delete + recreate); no "Remove Remote Host" extension command (globalState/SecretStorage never cleared, client-side uninstall incomplete); forwarder tears down on a single timed-out status probe (consider a consecutive-failure threshold if it flaps in the field); the service does not push client forwards, so remote mode polls every 10 s; a registry file created after activation is only noticed by the refresh tick (no `fs.watch` until the next activation) — a lazy first-sighting watch start is a small follow-up.


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
| 2026-09-02 | B9 docs merged (`d091279`): 17 markdown files aligned with the code, new `docs/field-test-remote-host.md` (12-step home-domain checklist). |
| 2026-09-02 | Phase 2 review round 9 fixes merged (`8d726a7`): forwarder retries after an unanswered capability check (pure `planStartOutcome`), Windows device-stem rule on all three branch validators, one canonical IPv6 host-label rule shared by guest CLI / service (`ForwardHost.cs`) / extension with a 37-entry tri-language matrix, usage export bound to its captured instance + period. |
| 2026-09-02 | Phase 2 review round 10 fixes merged (`a5954b5`): `construct-` prefix reserved and stripping removed (one derivation rule everywhere), one instance-name rule in JS/PS/C# (alphanumeric first+last, 1–63; C# `\A…\z` parity fix), registry fingerprint-driven retargeting via one serialized transition (debounced `fs.watch` only when the file exists), Start & connect + `lifecycle.run` stale-target gates, instance-scoped narrow webview messages. Node 21 files, dotnet 510, pwsh instances 684. **Code work for this batch is complete; per the one-loop-per-change rule no further review runs without new changes.** |
| 2026-09-02 | Field test started on `standpc` (WSL 2.6.3, German Windows). Installer fixes from the field, each direct + tested: relative path params resolved against `$PWD` before the elevated relaunch and ACE identities read by SID (`6ceaf98`); parents hardened before children (`8aa8ca6`); LocalSystem task polled via the Schedule.Service COM API in its own folder, `SCHED_S_TASK_RUNNING` is not an exit code (`16405cc`); failed LocalSystem commands report their output (`b56e0e9`). Blocker: `WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED` → §4.10 / B10. |
| 2026-09-02 | B10 re-scoped (project owner): service stays LocalSystem, admin builds a pre-built ISO interactively; guest hostname from Hyper-V KVP. Service-account pair cancelled before any commit. |
| 2026-09-02 | B10 merged (`9fc3485`): `IsoOptions.Mode` (Prebuilt default / PerVm; Native, InGuest, HypervisorHost documented), `IIsoMediaBuilder` + `IIsoCatalog`/`FileIsoCatalog` (versioned media, sidecar, atomic pointer, prune skips media Hyper-V holds open), `PrebuiltIsoBuilder`, `admin iso build/status/prune`, installer builds the ISO as the admin between settings and service registration (`-SkipIsoBuild`, `-IsoBuildOnly`), every LocalSystem-WSL step and the task runner removed, `VM_HOSTNAME_SOURCE=hyperv-kvp` first-boot identity source (default media byte-identical), provision.sh belt-and-braces rename. dotnet 572, installer 286, 16 bash suites. |
| 2026-09-03 | §4.12 written: multi-instance client completion decided with the project owner (per-instance state file, VM-side commit marker, name-only targeting, wildcard public hostnames + guest-requested host forwards, one T3 Desktop with multi-link, cleanup action). Batches B11–B14 defined; **no implementation started**. |
| 2026-09-04 | Phase 6 (§4.12) B11–B14 merged (`this merge`): B11 registry for local VMs + name-only `-InstanceName` targeting, B12 per-instance state + `CONSTRUCT_COMMIT` marker + per-VM stale detection, B13 `PublicHostPattern` + guest-requested host forwards, B14 T3 Desktop multi-link (per-instance CA files with an orphan-thumbprint ledger, the `installed.json` version record, one Providers row per linked remote matched on host **and** port, Reprovision per instance over a new `reprovisionConstructInstance` IPC), **Remove instance** + **Remove Remote Host**, and the forwarder-slice / SMB-letter / temp-file de-singletoning. |
| next | Field test resumes on `standpc`: install with the pre-built ISO, verify `admin iso status`, first VM's hostname + `<name>.mshome.net`. |

Process notes: every package ran as an omniloop dev/reviewer pair (opus developer,
gpt-5.6-sol reviewer) in its own worktree; cross-package integration reviews ran on the
merged tree with the zero-change default path as the primary bar. Workflow budgets must
be ≥ 720 min because a usage-limit park does not pause the workflow-level timeout.
Process correction (2026-09-02, project owner): one review-fix loop per change. Rounds 6–10
re-ran a fresh integration reviewer on an unchanged tree after each fix pair had been
approved, which cannot converge on a diff this size. From here: a new review only when
new changes land; the round-10 fix pair is the last loop for this batch.
