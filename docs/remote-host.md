# Remote host (`hyperv-remote`)

> **Status: implemented.** The service and its host installer, the `hyperv-remote` driver,
> the installer and extension flows, port forwards and the idle policy are all in place.
> Local Hyper-V stays the default and is completely unchanged — an install that never names
> a remote host behaves, and prints, exactly as it always has. Everything below is opt-in.

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
│   · lib/AgentVm.Remote.ps1 ─────┼───────▶│   · creates the VM on the configured   │
│     (API client + credentials)  │Negotiate│    switch (Default Switch by default)  │
│                                 │ /token │   · waits for SSH, allocates a forward  │
│                                 │        │   · idle policy (save / shutdown)       │
│ Provision-AgentVM.ps1 ──────────┼────────┼─▶ VM  (ssh to <host>:<allocated port>)  │
│   · YOUR git creds, agent auth, │  SSH   │                                          │
│     backups — never touch the   │        │                                          │
│     service                     │        │                                          │
└─────────────────────────────────┘        └──────────────────────────────────────────┘
```

The split is deliberate ([plan §4.4](plans/modular-remote-architecture.md), "hybrid"):

| Step | Who does it | Why |
|---|---|---|
| Create the VM from the pre-built autoinstall ISO, wait for the OS install, allocate the SSH port | **the service** | it owns the hypervisor and the port range |
| Build that ISO, once, before any VM exists | **the host's administrator**, interactively | `wsl.exe` refuses to run as LocalSystem (`WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED`), and LocalSystem is the service's identity. The installer does it as *you*; `constructd admin iso build` repeats it later ([plan §4.10](plans/modular-remote-architecture.md)). |
| Run `bin/provision.sh`, install the agent stack, restore backups, wire *your* machine (ssh config, VS Code Remote-SSH, OpenCode) | **your PC**, over SSH | provisioning carries your git credentials, agent auth and backups. None of that may transit a shared service. |

The **guest payload is identical in every mode** — same ISO inputs, same `provision.sh`
contract, same `/opt/construct` layout. Only *who calls the hypervisor* and *what address
you dial* change.

---

## 2. Admin: set the host up once

1. **Install the service.** On the Hyper-V host, from a Construct checkout:

   ```powershell
   .\service\host\Install-ConstructHost.ps1
   ```

   It checks the prerequisites (Hyper-V, **your** WSL distro with `xorriso` + `whois` for
   the ISO build), hardens the paths the
   service executes and trusts, generates the self-signed TLS certificate, opens three
   inbound firewall rules (the API port, the SSH forward range, the app forward range),
   **builds the autoinstall ISO as you, through your WSL**, and registers `constructd` as a
   Windows service. See
   [`service/README.md`](../service/README.md) for the configuration keys
   (`PublicHost`, `PublicHostPattern`, `SshForwardPorts`, the idle defaults, the
   certificate) and the full parameter table. If you want one host name per VM — and you do
   as soon as two VMs serve web UIs — add `-PublicHostPattern` and a wildcard DNS record
   (see *Per-VM public host names* below). Publish the service first
   (`dotnet publish service\src\Constructd.Api -c Release -r win-x64 --self-contained true
   -o <publish dir>`); no .NET runtime is then needed on the host. Re-run the installer
   after publishing a new build — it updates binaries, settings and the service in place.
   `service/host/Uninstall-ConstructHost.ps1` is the companion.

   > **The VMs go on the switch you configure, not on a switch the service creates.**
   > `-SwitchName` (and `Constructd:SwitchName`) default to Hyper-V's **`Default Switch`**,
   > which is what a host with nothing else set up has. The plan's "the service creates its
   > own internal NAT switch at install" is **not implemented** — the setting is the seam
   > for it. If you want the service's VMs on a switch of their own, create it yourself and
   > pass `-SwitchName`.

2. **The first admin is created by the installer.** `-AdminUser` (default: the current
   user) is seeded as an admin and issued an API token *before* the service is started,
   and both the token and the certificate thumbprint are printed at the end. A re-run
   issues no new token unless you pass `-RotateAdminToken`. On a host that cannot use
   Negotiate at all, `Constructd:BootstrapAdminToken` in the settings file is the escape
   hatch: set it once, issue yourself a real token, remove it.

   > ⚠ **Do not publish the thumbprint the installer prints — it is not what clients
   > compare.** The installer prints the certificate's **SHA-1** thumbprint (40 hex
   > characters; that is the value `-CertThumbprint` selects a certificate by), while the
   > client pins and displays the **SHA-256** fingerprint (64 hex characters, colon-separated
   > pairs). Compute and publish the SHA-256 form:
   >
   > ```powershell
   > $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq '<printed value>'
   > ([BitConverter]::ToString(
   >     [Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData))) -replace '-', ':'
   > ```
   >
   > That output is exactly the spelling §3 step 2 shows the user.

3. **The autoinstall ISO.** The installer already built it (step 1) — one **generic** ISO
   that every VM on this host installs from. There is nothing per-VM in it: the guest reads
   the Hyper-V VM name out of the KVP data-exchange channel at first boot and adopts it as
   its hostname, which is what makes `<vm name>.mshome.net` resolve for the service's own
   reachability check and for the forwards' `connectaddress`.

   ```powershell
   # the published executable itself, with `admin` as the first argument
   $constructd = "C:\Construct\service\publish\Constructd.Api.exe"

   & $constructd admin iso status          # what is published, from which source ISO, which key
   & $constructd admin iso build --force   # new Ubuntu release, or a rotated bootstrap key
   & $constructd admin iso prune           # delete superseded ISOs nothing has attached
   ```

   `admin iso build` runs **as you**, through **your** WSL: `wsl.exe` refuses to run as
   LocalSystem, which is the service's identity ([plan §4.10](plans/modular-remote-architecture.md)).
   The service only consumes what is published. A rebuild never overwrites the ISO in
   place — Hyper-V holds an open handle on media a VM has attached — it writes
   `construct-autoinstall-<utc>.iso` next to it, with a sidecar recording when it was
   built, from which source ISO and SHA-256, and which bootstrap key fingerprint is inside;
   then the `current.pointer` swap makes it the one new VMs get. `.\service\host\Install-ConstructHost.ps1
   -IsoBuildOnly` does the same from the installer.

   > **No media, no VMs.** Creating a VM fails with *"No autoinstall ISO is available on
   > this host"* and the exact command to fix it. That is also what `-SkipIsoBuild` leaves
   > behind on purpose.

4. **Add a user and issue their token.** The admin CLI is the same executable and works
   the stores directly — no HTTP, no listener, no authentication beyond already being an
   administrator on that host:

   ```powershell
   & $constructd admin users add DOMAIN\alice --role User --max-vms 2
   & $constructd admin users add DOMAIN\bob   --role User --max-vms 2 --no-host-forwards
   & $constructd admin tokens issue DOMAIN\alice --label "alice laptop"
   & $constructd admin users list
   ```

   (The installer prints these two lines with the real path filled in when it finishes.)

   The HTTP API does the same as the admin identity, which is what you want from another
   machine:

   ```powershell
   Invoke-RestMethod -UseDefaultCredentials -Method Post `
     -Uri https://buildbox.example.local:7462/api/v1/users `
     -ContentType application/json `
     -Body '{"name":"DOMAIN\\alice","role":"user","maxVms":2}'

   Invoke-RestMethod -UseDefaultCredentials -Method Post `
     -Uri 'https://buildbox.example.local:7462/api/v1/users/DOMAIN%5Calice/tokens' `
     -ContentType application/json -Body '{"label":"alice laptop"}'
   ```

   **There is no self-registration.** The token's plaintext is shown *once* and is never
   stored or logged; hand it to the user over a channel you trust. A domain user who will
   authenticate with Kerberos needs no token at all — just the `users add`. `--max-vms`
   defaults to `0`, which means "may not create VMs", so a quota typed carelessly refuses
   rather than over-grants; `--no-host-forwards` denies that user
   [`construct expose --to host`](expose.md#the-two-targets).

5. **The host must not go to sleep under the VMs.** `constructd` holds a Windows power
   availability request (`PowerRequestSystemRequired`) for as long as any VM it manages is
   running, and releases it when none is — so the machine's sleep idle timer cannot take a
   colleague's VM down at three in the morning. It is on by default
   (`Constructd:Power:KeepHostAwake`, and independent of `Idle:SchedulerEnabled` even
   though both ride the same once-a-minute loop); `powercfg /requests` shows it on the host, as a
   SYSTEM `[PROCESS]` entry against `Constructd.Api.exe`. That covers the idle timer while
   the service is up, and nothing else, so the installer also prints this host's own sleep,
   hibernate and unattended-sleep timeouts and — with `-KeepHostAwake`, or by asking in an
   interactive run — sets the **AC** ones to *never* (`-SkipPowerSettings` skips the step
   entirely, and an unattended run without the switch changes nothing). Closing a laptop lid
   or picking "Sleep" from the menu still sleeps the host; the request is about the idle
   timer, not about overruling you.

6. **Tell the users the URL and the SHA-256 certificate fingerprint** (the value computed
   in step 2, not the thumbprint the installer printed). The client shows that fingerprint
   at enrolment and asks for confirmation; publishing it out of band is what makes the
   confirmation meaningful (see §5).

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
2. **Certificate fingerprint** — the **SHA-256** fingerprint, shown once, in full, as
   colon-separated hex pairs. Compare it with what your admin published and confirm. It is
   then **pinned** (§5) and enforced on every later call. (If what your admin gave you is
   40 hex characters, they published the installer's SHA-1 thumbprint by mistake — ask for
   the SHA-256 value; §2 step 2 says how to produce it.)
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
   log the local install uses (media selected → VM create → OS install wait → media detach →
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
   `agent-vm.mshome.net:22`. The installer spells the identity out because it has just
   fetched the endpoint; a later run against the recorded instance can say the same thing
   with one argument, which is what the control panel emits:

   ```
   Provision-AgentVM.ps1 -InstanceName work-vm
   ```

   `-InstanceName` resolves the endpoint, alias, port, key file, config-sync branch **and
   the host service's URL** out of the registry entry above (see [Installation § Targeting
   one VM by name](installation.md#targeting-one-vm-by-name)), so the guest is linked back
   to the service the entry names rather than to whatever it was last told. It is the same
   parameter the guest receives as `CONSTRUCT_INSTANCE_NAME` — and the name still reaches
   the guest only together with a service URL, i.e. only for a service-managed VM, which is
   why a local instance's env prefix is unchanged. The one-time VM token is never in the
   registry and a reprovision does not need one.
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

A remote install also **needs no administrator rights on your PC**: nothing is created
locally, so the installer says so and skips the elevation it does for a local install.

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
| **Remove instance** | `DELETE /vms/{name}` **and** removes everything this PC knows about the VM (see below). Needs the instance name typed back. |
| **Quit** | nothing. |

There is no *Redownload* for a remote instance: the ISO is built on the host, and the
service decides when to refresh its source image.

### Removing a remote instance, and forgetting the host

`Auto-Install.ps1 -Action remove-instance -InstanceName work-vm` (the menu choice above, or
**Settings → Remove instance** in the control panel) undoes what creating the VM did *on
this PC*, and — for a `hyperv-remote` instance — deletes the VM on the host as well:

1. `DELETE /vms/{name}` through the same driver contract *Reinstall* uses. This goes
   **first**: if the service refuses, nothing local is touched and the run stops, rather
   than leaving a half-forgotten instance whose VM is still running.
2. the `~/.ssh/config` block, the `known_hosts` entries and the private key for its alias;
3. its `remote.SSH.remotePlatform` entry, its OpenCode server entry (matched by the URLs
   this PC wrote for it *and* by its display name), its T3 certificate authority (file +
   Root store — the machine store through one narrowly scoped elevated command), its
   per-instance state file, the endpoints the provisioner recorded for it
   (`artifacts\t3code\remote-<name>.json` — its T3 origin and the OpenCode url it
   registered) and, **last and only if every step before it succeeded**, its registry
   entry.

Because the VM's disk goes with it, the instance name has to be **typed back** — in the
console when it is run interactively, and as `-ConfirmInstanceName <name>` when it is not
(the control panel collects it and passes it along; the script checks it again through the
same planner, because it is also run by hand). The action never elevates: every file it
edits belongs to the signed-in user.

The **host enrolment** is separate, and outlives its VMs. **The Construct: Remove Remote
Host** in VS Code clears the three places *Add Remote Host* wrote — the `globalState`
record, the API token in SecretStorage and the `.pin` file — and is **refused while any
registry entry still names that service URL**: those VMs are reached through the host, so
remove them first. It changes nothing on the host itself. The PowerShell client's own token
and pin are separate files (see §5); delete them by hand to forget the host in a console.

---

## 4. Connecting, and what the panel shows

Nothing special. The provisioner writes an ordinary `~/.ssh/config` block:

```
Host work-vm
    HostName buildbox.example.local
    User root
    IdentityFile C:\Users\<you>\.ssh\construct_work-vm_ed25519
    IdentitiesOnly yes
    Port 2201
```

(The `Port` line appears only for a non-22 port, which is why a local VM's block is
byte-identical to what it always was. Only the block for *this* alias is replaced, so
several instances coexist in one `~\.ssh\config`.)

So `ssh work-vm`, VS Code Remote-SSH and the control panel all work the way they do
for a local VM. The panel's **System** card names the backend and the host service for a
remote instance (the rows are hidden for `hyperv-local`, so a single-VM install's panel is
pixel-identical), the instance picker lists local and remote VMs side by side, and the
[**Forwards** card](control-panel.md#forwards-construct-expose) shows the ports
`construct expose` opened — including the `host` ones the service published.

**What does NOT reach a remote VM the way it does a local one:**

* **The SMB share.** The service publishes exactly two kinds of forward — the VM's own SSH
  port, and the host forwards somebody asked for with `construct expose --to host`. There
  is **no SMB forward**, so the `\\<host>\repo` UNC the guest prints (built from
  `CONSTRUCT_EXTERNAL_HOST`, i.e. the service host) points at an address where nothing is
  listening on 445, and `-MountRepoShare` has nothing to map. Reach the files over
  Remote-SSH instead. Known gap, recorded in the plan's IPv6/SMB follow-up.
* **Web ports other than OpenCode and T3.** Those two *are* mapped automatically: a
  service-managed VM requests one host forward for each of them at provision time (see
  *Per-VM public host names, and the web ports of a remote VM* below). Everything else —
  `code serve-web`, your own dev servers — is manual: use
  [`construct expose <port>`](expose.md), whose client target works identically in both
  modes and needs no host-forward policy.

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
environment exactly as before — the contract is unchanged, only the delivery is.

> **A lost VM token cannot currently be re-issued.** The service mints one in exactly one
> place — the VM creation job — and exposes no rotation route or admin verb. If the guest's
> `/etc/construct/vm-token` is destroyed, or the one-time delivery is lost, that VM's
> `construct expose` and its idle heartbeat stay broken until the VM is **deleted and created
> again** (`DELETE /vms/{name}` → `POST /vms` → provision, i.e. what *Reinstall* does).
> A reprovision alone does not help: it can only re-deliver a token it was given. Recorded as
> an open point in [`service/README.md`](../service/README.md).

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
VM's policy — in the control panel's [**Idle policy** card](control-panel.md#idle-policy-remote-vms),
which applies the cap locally so the number in the box is the number that takes effect.

The "in-guest activity" signal is the VM's own heartbeat: `construct-idle-report.timer`
posts `{busy, reasons[]}` every 60 s (`CONSTRUCT_IDLE_REPORT_INTERVAL_SEC`), reporting busy
for an SSH session, an agent process (or any of its **descendants**) burning CPU, recent
tmux window activity, or a provisioning run in flight. It is deliberately generous: a false
`busy` costs some host RAM until the next tick, a false idle kills someone's unattended job.
The timer is installed only when `CONSTRUCT_SERVICE_URL` is set — a local install gets no
new unit. Details in [`construct expose` § Activity heartbeat](expose.md#activity-heartbeat).

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

## Per-VM public host names, and the web ports of a remote VM

A remote VM sits on the host's own switch, so the only way to a port inside it is a forward
the service publishes. Two things follow, and they are why this section exists.

### Why one host name per VM

Two VMs' web UIs on one host would otherwise be `https://buildbox:2301` and
`https://buildbox:2302` — **the same origin as far as cookies are concerned**, because a
browser scopes cookies by host and ignores the port. Logging into the second T3 web GUI
logs you out of the first. The fix is a name per VM:

1. **One wildcard DNS record**, pointing at the service host:

   ```
   *.vpn.example.        A     10.0.0.7        ; or CNAME buildbox.example.local.
   ```

   Any DNS you already run works — the domain controller's zone, a home router, a VPN's
   resolver. Nothing about it is Construct-specific: every name under the wildcard has to
   resolve to the host the service runs on, because that is where the forwards are
   published. A **hosts file cannot do this**: it has no wildcards, so trying the feature
   out that way means one explicit line per VM (`10.0.0.7  work-vm.vpn.example`) on every
   client that opens the UI — fine for a first look, not a deployment.

2. **One service setting**, `Constructd:PublicHostPattern`:

   ```powershell
   .\service\host\Install-ConstructHost.ps1 -PublicHostPattern "{name}.vpn.example"
   ```

   `{name}` is substituted with the VM's name (`work-vm` → `work-vm.vpn.example`). It must
   appear **exactly once**, and the pattern must render a valid DNS name for *every* legal
   VM name — both the installer and the service check that at startup, by rendering the
   shortest and the longest name the one instance-name rule allows. A fixed part in the
   *same label* as the name (`vm-{name}.vpn.example`) is therefore refused: the longest
   instance name is already a full 63-character label. Put it in its own label
   (`{name}.vm.vpn.example`).

   ```powershell
   & $constructd admin host status      # what this host advertises, with an example rendering
   ```

**The certificate is untouched.** The API certificate stays bound to `-PublicHost` and
clients keep pinning it by fingerprint; the pattern only changes the names *VMs* are
advertised under. The T3 web GUI inside each VM serves its own HTTPS with its own local CA
(`bin/setup-t3-https.sh`), whose certificate now carries the VM's public name in its SANs —
which is what the provisioner imports into this PC's trust store.

Unset (the default) means every VM is advertised on `PublicHost`, exactly as before this
setting existed. The target design is one LAN address per VM (Proxmox); the wildcard is the
Hyper-V-era bridge.

### What the client does with it

`GET /vms/{name}/endpoint` now answers `{sshHost, sshPort, publicHost}`. **SSH is unchanged**
— it always dials `sshHost:sshPort`, i.e. the service host plus the forward it allocated.
`publicHost` is recorded in the instance registry (`docs/installation.md`, *The instance
registry*) and handed to `Provision-AgentVM.ps1` as `-PublicHost`, which passes it to the
guest as `CONSTRUCT_EXTERNAL_HOST`. The guest's T3 certificate SANs, its
`T3CODE_PUBLIC_BASE_URL` and every URL it prints then use the VM's own name.

### The forwards the guest asks for

At provision time a **service-managed** VM requests one host forward per enabled web service,
through the same `construct expose --to host` machinery an agent uses and with its own scoped
token (`docs/expose.md`):

| Service | VM port | Enabled when |
|---|---|---|
| OpenCode server | `OPENCODE_PORT` (4096) | `opencode` is in `AI_TOOLS` |
| T3 Code web GUI | `T3CODE_HTTPS_PORT` (5178), or `T3CODE_PORT` (5177) without HTTPS | `T3CODE=true` |

The requests are **get-or-create** (`--reuse`), so a reprovision never allocates a second
public port for the same VM port, and the forwards survive your PC being off. The result is
written to `/etc/construct/host-forwards` in the guest — one `KEY=<host>:<port>` line per
service, plus `T3_URL=<origin>` once T3 is up — which the provisioner reads back to:

* register the OpenCode server entry at `http://<publicHost>:<forwarded port>` (the VM's own
  `:4096` is not reachable from your PC at all), and
* print the forwarded T3 and OpenCode URLs in its closing summary.

T3's own advertised origin uses the forwarded port too: `provision.sh` requests the forward
*before* installing T3 and passes the public port to `bin/setup-t3-https.sh`, so
`T3CODE_PUBLIC_BASE_URL` — and the pairing links and DPoP proofs bound to it — point at the
port you can actually reach. That holds **with or without HTTPS**: with `T3CODE_HTTPS=false`
the forward is for the plain listener and the advertised origin is
`http://<publicHost>:<forwarded port>`, which is equally the only address a client can reach.
The guest banner, the panel's T3 entry and the provisioner's summary all follow that origin.

Because the forward is requested *before* T3 is set up, the forward alone does not say what
is listening on it: a request for the TLS port whose HTTPS setup then failed looks exactly
like a working plain forward. So the guest records the **effective** origin as a second line,
`T3_URL=`, taken from `T3CODE_PUBLIC_BASE_URL` and only when that origin really names the
forwarded port. The provisioner prints that line and nothing else — when the HTTPS setup did
not come up it says so instead of advertising a TLS port that serves nothing.

**If a forward is missing, no dead link is written.** A remote VM sits on the host's internal
switch, so `http://<sshHost>:4096` could never connect — that direct URL is only ever used on
a **local** install. On a service-managed VM the OpenCode entry is written **only** from a
real forward; otherwise it is omitted and the provisioner says which case it is:

| Case | What happens |
|---|---|
| forward allocated | the OpenCode server entry is `http://<publicHost>:<port>`, and the summary prints it |
| `--no-host-forwards` for the VM's owner | the guest records `denied`; the entry is omitted and the note points at `construct expose` |
| the request failed, or the status could not be read | the entry is omitted with "no forward was allocated"; re-run provisioning, or use a client forward for the session |

The extension's client forwarder (`construct expose` with the default `client` target) always
works and needs no host policy — it just lives only while VS Code is connected.

**A local Hyper-V VM does none of this**: NAT already reaches it at its own name, no request
is made, `/etc/construct/host-forwards` does not exist, and the OpenCode URL and the summary
text are exactly what they have always been.

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

* [`docs/field-test-remote-host.md`](field-test-remote-host.md) — the step-by-step first run
  on a domain, with the expected result of each step and where to look when it fails.
* [`docs/expose.md`](expose.md) — `construct expose`, the two forward targets, the guest
  spool and the service API behind them, and the activity heartbeat.
* [`docs/control-panel.md`](control-panel.md) — the instance picker, the Forwards card and
  the idle-policy card.
* [`docs/drivers.md`](drivers.md) — the driver contract and the `hyperv-remote` section.
* [`service/README.md`](../service/README.md) — the API, authentication, jobs, config,
  the admin CLI and `Install-ConstructHost.ps1`.
* [`docs/plans/modular-remote-architecture.md`](plans/modular-remote-architecture.md) —
  §4.2 driver contract, §4.3 registry, §4.4 the service, §4.5 installer UX, §4.7 idle.
* [`extension/ARCHITECTURE.md`](../extension/ARCHITECTURE.md) — the extension side.
