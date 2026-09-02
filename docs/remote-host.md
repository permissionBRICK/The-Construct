# Remote host (`hyperv-remote`)

> **Status: implemented (batch B7).** Local Hyper-V stays the default and is completely
> unchanged — an install that never names a remote host behaves, and prints, exactly as
> it always has. Everything below is opt-in.

The Construct can put your agent VM on **somebody else's Hyper-V** — a shared box under a
desk, a lab server, a build machine — instead of your own PC. An admin installs the
`constructd` service on that machine once; after that every user creates, provisions and
manages **their own** VMs on it from the same `Auto-Install.ps1` and the same VS Code
control panel.

The point is **PC-independence**: once the VM runs on the remote host, nothing it needs
lives on your laptop. Close the lid, reboot, go home — the VM, its SSH forward and its
idle policy keep running, because the host service owns them
([plan §1](plans/modular-remote-architecture.md)).

---

## 1. What runs where

```
┌──────────── your PC ────────────┐        ┌──────── the remote Hyper-V host ────────┐
│ Auto-Install.ps1 / VS Code      │        │ constructd (Windows service, HTTPS)     │
│   · picks the mode, asks the    │        │   · users, tokens, quotas, audit        │
│     questions                   │ HTTPS  │   · builds the autoinstall ISO (WSL)    │
│   · lib/AgentVm.Remote.ps1 ─────┼───────▶│   · creates the VM on its own NAT switch│
│     (API client + credentials)  │Negotiate│  · waits for SSH, allocates a forward  │
│                                 │ /token │   · idle policy (save / shutdown)       │
│ Provision-AgentVM.ps1 ──────────┼────────┼─▶ VM  (ssh to <host>:<allocated port>)  │
│   · YOUR git creds, agent auth, │  SSH   │                                          │
│     backups — never touch the   │        │                                          │
│     service                     │        │                                          │
└─────────────────────────────────┘        └──────────────────────────────────────────┘
```

The split is deliberate ([plan §4.4](plans/modular-remote-architecture.md), "hybrid"):

| Step | Who does it | Why |
|---|---|---|
| Build the autoinstall ISO, create the VM, wait for the OS install, allocate the SSH port | **the service** | it owns the hypervisor, WSL and the port range |
| Run `bin/provision.sh`, install the agent stack, restore backups, wire *your* machine (ssh config, VS Code Remote-SSH, OpenCode, SMB) | **your PC**, over SSH | provisioning carries your git credentials, agent auth and backups. None of that may transit a shared service. |

The **guest payload is identical in every mode** — same ISO inputs, same `provision.sh`
contract, same `/opt/construct` layout. Only *who calls the hypervisor* and *what address
you dial* change.

---

## 2. Admin: set the host up once

1. **Install the service.** On the Hyper-V host, from a Construct checkout:

   ```powershell
   .\service\host\Install-ConstructHost.ps1
   ```

   It checks the prerequisites (Hyper-V, WSL for the ISO build), generates the
   self-signed TLS certificate, creates the internal NAT switch the VMs live on
   (the "Default Switch" is left alone so local-mode installs are untouched), and
   registers `constructd` as a Windows service. See
   [`service/README.md`](../service/README.md) for the configuration keys
   (`PublicHost`, `SshForwardPorts`, the idle defaults, the certificate).

   > The installer script ships with batch B6b. Until it lands, the service can be run
   > by hand — `dotnet run --project service/src/Constructd.Api` with the keys from
   > `service/README.md` — which is also how the end-to-end tests drive it.

2. **Enrol yourself as the first admin.** Set `Constructd:BootstrapAdmin` to your domain
   identity (`DOMAIN\you`) before the first start; the service seeds it as an admin when
   the user store is empty. On a host that cannot use Kerberos you can also set
   `Constructd:BootstrapAdminToken` once, issue yourself a real token, and remove it.

3. **Add a user and issue their token.**

   ```powershell
   # as the admin identity (Negotiate on a domain-joined machine)
   Invoke-RestMethod -UseDefaultCredentials -Method Post `
     -Uri https://buildbox.example.local:7462/api/v1/users `
     -ContentType application/json `
     -Body '{"name":"DOMAIN\\alice","role":"user","maxVms":2}'

   Invoke-RestMethod -UseDefaultCredentials -Method Post `
     -Uri 'https://buildbox.example.local:7462/api/v1/users/DOMAIN%5Calice/tokens' `
     -ContentType application/json -Body '{"label":"alice laptop"}'
   ```

   **There is no self-registration.** The token's plaintext is in the response *once* and
   is never stored or logged; hand it to the user over a channel you trust. A domain user
   who will authenticate with Kerberos needs no token at all — just the `POST /users`.

4. **Tell the users the URL and the certificate fingerprint.** The client shows the
   fingerprint at enrolment and asks for confirmation; publishing it out of band is what
   makes that confirmation meaningful (see §5).

---

## 3. User: create a VM on the host

### From the installer (`Auto-Install.ps1`)

On a **fresh machine** — no Construct VM and no instance registry yet — the installer now
opens with one extra question:

```
  How should this Construct VM run?
  > Local Hyper-V install   create the VM on THIS PC (the usual install)
    Remote host install     create it on a shared Construct host service
```

Pick **Local Hyper-V** and everything from there is byte-for-byte the install it always
was. Pick **Remote host** and the installer walks:

1. **Service URL** — `https://buildbox.example.local:7462` (a bare host name gets
   `https://` and the default port `7462`).
2. **Certificate fingerprint** — shown once, in full. Compare it with what your admin
   published and confirm. It is then **pinned** (§5) and enforced on every later call.
3. **Authentication** — Windows/Kerberos is tried first, silently, as the account you are
   logged in as. On a 401 you are offered:
   * **paste an API token** — stored DPAPI-encrypted for your account (§5), or
   * **domain user + password** — prompted, used for that run.
4. **`GET /whoami`** confirms who the host thinks you are, your role and your VM quota.
5. **Instance name** — a DNS label (`work-vm`), unique on that host *and* not already in
   your local registry.
6. The **usual questions** — RAM, disk, project profiles, git identity, agent password —
   exactly the same TUI screens as the local path.
7. `POST /vms` starts the create job; its progress lines stream into the same scrolling
   log the local install uses (ISO build → VM create → OS install wait → media detach →
   SSH forward allocated).
8. The **instance registry entry** is written
   (`%LOCALAPPDATA%\The-Construct\instances.json`) as soon as the endpoint is known —
   *before* provisioning, on purpose. That entry is this PC's only handle on a remote VM:
   locally a half-finished install still leaves a VM `Get-VM` can find, but a remote VM
   that was never recorded would be neither reachable nor re-creatable (the service
   refuses a second VM of the same name). Recording it first turns the one step that
   commonly fails — provisioning — into "run the installer again and pick *Reprovision*".
9. The installer then runs **the regular `Provision-AgentVM.ps1`** against the
   returned endpoint:

   ```
   Provision-AgentVM.ps1 -VmHost buildbox.example.local -SshPort 2201 `
       -HostAlias work-vm -LocalKeyName construct_work-vm_ed25519 `
       -ConfigBranch vm-work-vm `
       -ServiceUrl https://buildbox.example.local:7462 -InstanceName work-vm `
       -VmTokenB64 <one-time VM token, base64>
   ```

   Same provisioner, same steps, same output — just over `host:port` instead of
   `agent-vm.mshome.net:22`.
10. VS Code opens on the new VM exactly as it does locally.

Scripted (no prompts at all):

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote `
    -ServiceUrl https://buildbox.example.local:7462 -ServiceAuth negotiate `
    -InstanceName work-vm -VmCpuCount 4 -VmMemoryGB 8 -VmDiskGB 60 -Projects default
```

(`-VmCpuCount` is remote-only: on the local path `Create-AgentVM.ps1` decides the
processor count, so the parameter deliberately does nothing there.)

Passing any of `-Backend` / `-ServiceUrl` / `-InstanceName` skips the mode prompt. So does
an existing default instance, `-VmName`, `-Action`, and `-FromPanel` — **an existing
install never sees the new question.**

"Already installed" is decided twice over, because the mode is resolved *before* the
elevation prompt, as the ordinary desktop user: a Hyper-V probe through the driver
contract, **and** the VM's private key in your own profile
(`~\.ssh\agent_vm_ed25519`), which the provisioner writes on every successful run. The
second check is what makes the guarantee hold on a PC where `Get-VM` needs rights this
run does not have yet — membership of *Hyper-V Administrators* is granted during the
install but only applies after the next sign-in.

### From VS Code

* **The Construct: Add Remote Host** — URL → fingerprint confirmation → authentication →
  `whoami`. The host (URL + auth mode + the pinned fingerprint + the verified identity) is
  remembered in the extension's `globalState`, and the token — when you choose token auth —
  goes into VS Code **SecretStorage**. Nothing about a host is written to
  `instances.json` until a VM exists on it: the registry describes *VMs*, and an entry
  with no VM would show up in the instance picker as a machine you cannot reach.

  The **pinned fingerprint is written to the same file the PowerShell client reads**
  (`%LOCALAPPDATA%\The-Construct\remote\<hostslug>.pin`), so a host confirmed in VS Code
  is already trusted when `Auto-Install.ps1` runs in a console, and vice versa. The
  **token is not shared**: the extension keeps it in SecretStorage and the console keeps
  its own DPAPI copy, so a token-auth host added in VS Code asks for the token once more
  the first time a console run needs it. Neither store can read the other's, which is the
  point.
* **The Construct: New VM on Remote Host** — pick a known host, answer name / CPU / RAM /
  disk, and the command launches `Auto-Install.ps1`'s remote path in a host console
  (through the same `lifecycle.js` launcher every other action uses). The console does the
  create *and* the provisioning, because provisioning has to configure your PC too.

### The existing-VM menu for a remote instance

`Auto-Install.ps1 -InstanceName work-vm` on an instance that is already registered as
`hyperv-remote` offers:

| Choice | What it does |
|---|---|
| **Reprovision** | re-runs `Provision-AgentVM.ps1` against the instance's endpoint. Keeps all data. Never touches the service. |
| **Reinstall** | `DELETE /vms/{name}` → `POST /vms` → provision. Same typed-`yes` confirmation as the local path, and the same pre-wipe unsaved-work scan + config save. |
| **Export config** | pulls the VM's agent config back to this host. No changes to the VM. |
| **Quit** | nothing. |

There is no *Redownload* for a remote instance: the ISO is built on the host, and the
service decides when to refresh its source image.

---

## 4. Connecting, and what the panel shows

Nothing special. The provisioner writes an ordinary `~/.ssh/config` block:

```
Host work-vm
    HostName buildbox.example.local
    Port     2201
    User     root
    IdentityFile ~/.ssh/construct_work-vm_ed25519
```

so `ssh work-vm`, VS Code Remote-SSH, SMB and the control panel all work the way they do
for a local VM. The panel's **System** card names the backend and the host service for a
remote instance (the rows are hidden for `hyperv-local`, so a single-VM install's panel is
pixel-identical), and the instance picker lists local and remote VMs side by side.

**What the panel will NOT do for a remote instance:**

* **Checkpoints** are off — the remote driver reports `Checkpoints = $false`, so the
  automatic-checkpoint toggle and `Set-AgentVmCheckpoints.ps1` refuse rather than
  reconfiguring some *local* VM that happens to share the name.
* **Console** is `none` — there is no `vmconnect` to a machine you are not sitting at.
* **Suspend** *is* supported: the service's idle policy saves the VM (RAM freed, state on
  disk) and any power-start resumes it transparently.

---

## 5. Where the secrets live

| Secret | Where | Notes |
|---|---|---|
| **API token** (PowerShell) | `%LOCALAPPDATA%\The-Construct\remote\<hostslug>.token`, **DPAPI-encrypted, CurrentUser scope** | never plaintext on disk; only your Windows account on that machine can decrypt it. Delete the file to forget the token. |
| **API token** (extension) | VS Code **SecretStorage** | the OS credential store VS Code manages. |
| **Kerberos** | nothing stored | `Invoke-RestMethod -UseDefaultCredentials` uses the process identity. |
| **Domain password** | nothing stored | prompted per run, held in a `PSCredential` for that run only. |
| **Pinned certificate thumbprint** | `%LOCALAPPDATA%\The-Construct\remote\<hostslug>.pin` (plaintext — it is not a secret) | enforced on every call. |
| **The VM's scoped token** | `/etc/construct/vm-token` **inside the guest**, mode 0600 | written by `provision.sh` from `CONSTRUCT_VM_TOKEN_B64`. It authorises only that one VM's port forwards and its idle heartbeat. |

**The VM token is a one-time secret.** The create job hands it out on the **first**
authorised retrieval and never again — not on a re-poll, not on an SSE reconnect, not after
a service restart (`service/README.md`, "Jobs, the event stream and the one-time secret").
The installer takes it straight from the job result and passes it to
`Provision-AgentVM.ps1` as a **parameter value** (base64). It is never printed: not in the
echoed command line, not in the provisioning log, not in the env prefix that is shown.

**And it never reaches an argument list.** Every other value the provisioner sends the
guest rides in the `env …` prefix of the remote command — which is an *argument of
`ssh.exe`*, and arguments are readable by any process listing on your PC. So the token
takes a different route: it is written to the guest over **ssh's stdin** into a `0600`
file, the remote shell reads it back with a command substitution (`export
CONSTRUCT_VM_TOKEN_B64="$(cat …)"`), and the file is deleted the moment provisioning
ends, whatever its exit code. `bin/provision.sh` still reads the variable from its
environment exactly as before — the contract is unchanged, only the delivery is. If a
token is lost, issue a new one; that invalidates the previous one.

### TLS pinning, and why it looks different on PS 5.1 and PS 7

The service uses a **self-signed certificate**, so ordinary chain validation cannot work.
Instead the client pins the certificate's **SHA-256 thumbprint** at enrolment
(`Get-ConstructRemoteFingerprint` fetches and prints it; you confirm once) and enforces it
on every later call. The two PowerShell editions get there differently, because their HTTP
stacks differ:

* **Windows PowerShell 5.1** — `Invoke-WebRequest` goes through `ServicePointManager`, so
  the pin is enforced *inside the TLS handshake*:
  `[Net.ServicePointManager]::ServerCertificateValidationCallback` is set for the duration
  of the call (and restored in a `finally`) and compares the presented certificate's
  SHA-256 hash with the pin. A mismatched certificate fails the handshake — the request
  never happens.
* **PowerShell 7** — `Invoke-WebRequest` uses `SocketsHttpHandler`, which **ignores**
  `ServicePointManager`. So the client instead (a) opens a TLS connection itself, reads the
  presented certificate and compares it with the pin **before** the request, and (b) makes
  the request with `-SkipCertificateCheck`. The verification is real; it just happens one
  connection earlier. The window between the two is a same-host, same-second reconnect —
  materially, an attacker who can swap the certificate in that window can also swap it
  before the check on 5.1.

Either way: **no pin, no call.** An unpinned host is refused with instructions to run the
enrolment (fingerprint) step, and a changed fingerprint is a hard failure that names both
values rather than a prompt to click through — a certificate that changed is either a host
reinstall (in which case re-enrol deliberately) or the thing pinning exists to catch.

---

## 6. Idle policy

Remote VMs consume the host's RAM whether you are using them or not, so `constructd`
enforces a per-VM idle policy ([plan §4.7](plans/modular-remote-architecture.md),
`service/README.md` "Idle policy"). A VM is idle only when **both** signals agree,
continuously, for the whole timeout: no live connections through any of its forwards **and**
no in-guest activity. An agent running a long unattended job keeps the VM alive with zero
connections — that is the entire point of unattended agents.

The default action is `save` (Hyper-V `Save-VM`: state to disk, RAM freed, transparent
resume). Any power-start — the panel, `POST /vms/{name}/power {"action":"start"}` — brings
it back. Your admin sets the service-wide default and an optional cap; you set your own
VM's policy. The guest heartbeat that feeds the "in-guest activity" signal ships with
batch B8.

---

## 7. Troubleshooting

| Symptom | What it means |
|---|---|
| `401` right after Kerberos was tried | the host does not accept your Windows identity (not domain-joined, no SPN, or you are not enrolled). Use a token, or ask the admin to add you. |
| `whoami` answers with `known: false` | you authenticated fine, but nobody has enrolled you: ask the admin for a `POST /users`. |
| `Certificate fingerprint mismatch` | the host's certificate changed. Confirm with the admin, then delete `%LOCALAPPDATA%\The-Construct\remote\<hostslug>.pin` and re-enrol. |
| `409` from `GET /vms/{name}/endpoint` | the VM exists but has no SSH forward yet — it is still being created. |
| `403` on a VM that exists | it belongs to somebody else. Ownership is per user; an unknown VM answers `404`. |
| The VM was created but provisioning failed | the instance is already in the registry (§3, step 8). Run `Auto-Install.ps1 -InstanceName <name>` and pick **Reprovision** — nothing on the host is touched, and nothing is created twice. |
| The job fails and the VM disappears | creation rolls back deliberately (`service/README.md`): a partially created VM would keep consuming disk while holding its name. The original failure is reported, never masked. |
| Reinstall/Redownload refused in the panel | the installed host scripts predate the remote parameters, so the action would have hit a *local* VM. Update The Construct on this PC. |
| "this PC's instance registry would refuse …" | an identity clash with an instance you already have — the message names it and the field (a shared `configBranch`, `keyName`, `hostAlias`, `vmName`, or the same `sshHost` **and** `sshPort`). Before the VM is created nothing has happened; after it, the create is rolled back. See the section below. |
| `Refusing to talk to the Construct host service … over plain http` | you gave an `http://` URL for a host that is not this machine. There is nothing to pin and nothing to encrypt, so a token or a Windows credential would cross the network in clear. Both clients refuse before sending anything. Use `https`; plain http is accepted only for a service on `localhost` (which is how the tests drive the fake service). |
| A warning about sending a Windows credential over plain http | you pointed at a service on **this** machine over `http://`. That is allowed, but Kerberos/NTLM is not encrypted in transit there, so the client says so once. |

## Several VMs on one host service, and what the registry refuses

**Several of your VMs can live on one host service.** The registry's endpoint identity is
the **composite `(sshHost, sshPort)`**, not the host alone: every VM on a service host
shares that host's address and is told apart by the SSH forward the service allocated it
(one port per VM out of the configured range, §4.4). Two entries are "one machine under
two names" — and both are then dropped on load — only when they share **host *and*
port**.

`Auto-Install.ps1` therefore asks the registry two different questions, both answered by
the shared rules in `lib/AgentVm.Instances.ps1` (never by a second copy of them):

* **before it asks the service for anything** — would this instance be refused for a
  reason that is already knowable? Its name, and the identities derived from it: `vmName`,
  `hostAlias`, `keyName`, `configBranch`. The endpoint is deliberately *not* judged here,
  because the service has not allocated the forward yet. (This is what used to refuse a
  perfectly good second VM on a shared host.)
* **right after the VM is created**, with the endpoint the service really returned (its
  advertised `PublicHost` can differ from the URL's host, and nothing exposes the port
  before the VM exists) — the full rule set. A conflict here means the VM could never be
  reached or rebuilt from this PC, so the create is **rolled back** (the same `DELETE` the
  reinstall path uses) and the failure names the entry that is in the way.

Two entry rules are worth knowing if you ever hand-edit `instances.json`, because a
`hyperv-remote` entry that breaks either one **does not load at all** (both readers refuse
it whole, with a problem the panel surfaces):

* **`sshHost` must be stated.** A remote endpoint only ever comes from the host service.
  An entry without one used to fall back to the local-Hyper-V convention
  `<name>.mshome.net:22` and stay actionable — pointing the picker and every SSH action at
  an unrelated machine on your own network.
* **`vmName` must be exactly the instance name.** The service addresses the VM by that
  name and so does a rebuild (`-InstanceName`), so an entry keyed `alias-vm` with
  `vmName: "service-vm"` would let Start and the power state act on one VM while
  Reinstall **deleted and recreated** the other.

Several *users* on one host are unaffected either way — each has their own PC and their own
registry — and so are several hosts.

## See also

* [`docs/drivers.md`](drivers.md) — the driver contract and the `hyperv-remote` section.
* [`service/README.md`](../service/README.md) — the API, authentication, jobs, config.
* [`docs/plans/modular-remote-architecture.md`](plans/modular-remote-architecture.md) —
  §4.2 driver contract, §4.3 registry, §4.4 the service, §4.5 installer UX, §4.7 idle.
* [`extension/ARCHITECTURE.md`](../extension/ARCHITECTURE.md) — the extension side.
