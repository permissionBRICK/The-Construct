# Remote host (`hyperv-remote`)

> **Status: implemented.** The service and its host installer, the `hyperv-remote` driver,
> the installer and extension flows, port forwards and the idle policy are all in place.
> Local Hyper-V stays the default and is completely unchanged — an install that never names
> a remote host behaves, and prints, exactly as it always has. Everything below is opt-in.
> Host administration, delegated child VMs, console input and host updates are also
> implemented and covered by Linux fakes/recording tests, but have **not** been deployed or
> field-validated through constructd on the Hyper-V host; use the
> [owner checklist](field-test-host-admin.md) before treating them as rollout-ready.

The Construct can put your agent VM on **somebody else's Hyper-V** — a shared box under a
desk, a lab server, a build machine — instead of your own PC. An admin installs the
`constructd` service on that machine once; after that every user creates, provisions and
manages **their own** VMs on it from the same `Auto-Install.ps1` and the same VS Code
control panel.

Remote installs also install [Construct Companion](companion.md) on the **user's PC**.
The VM runs on the remote host; client forwards, notifications and microphone
capture run on the user's desktop. Auto-Install performs this per-user step before
choosing local or remote mode. Updates and plain reprovision refresh it too. A PC
that only added a VM through VS Code gets a session install offer, with the same
`construct.installCompanion` command available manually. No local Hyper-V is required.

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
│     questions                   │ HTTPS  │   · builds the autoinstall ISO (.NET)    │
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
| Build that ISO, once, before any VM exists | **the host's administrator**, interactively | The native .NET tool publishes generic media through `constructd admin iso build`; no WSL or installed .NET runtime is needed. |
| Run `bin/provision.sh`, install the agent stack, restore backups, wire *your* machine (ssh config, VS Code Remote-SSH, OpenCode) | **your PC**, over SSH | provisioning carries your git credentials, agent auth and backups. None of that may transit a shared service. |

The **guest payload is identical in every mode** — same ISO inputs, same `provision.sh`
contract, same `/opt/construct` layout. Only *who calls the hypervisor* and *what address
you dial* change.

---

## 2. Admin: set the host up once

If you already have a local Construct VM, connect to it in VS Code and open
**Construct Settings → Make this PC a Construct host…**. Review the prefilled
address and optional AC wake setting, then approve Windows elevation. Setup installs
the host, makes your current Windows account its administrator, and adopts the
running VM automatically. Its data and SSH identity are preserved. See the
[conversion flow and recovery notes](agent-notes/local-host-conversion.md).

1. **Install the service.** On the Hyper-V host, from a Construct checkout:

   ```powershell
   .\service\host\Install-ConstructHost.ps1
   ```

   It checks Hyper-V and OpenSSH, resolves the [native ISO tool](native-iso.md), and hardens the paths the
   service executes and trusts, generates the self-signed TLS certificate, opens three
   inbound firewall rules (the API port, the SSH forward range, the app forward range),
   **builds the autoinstall ISO with the native .NET tool**, and registers `constructd` as a
   Windows service. See
   [`service/README.md`](../service/README.md) for the configuration keys
   (`PublicHost`, `PublicHostPattern`, `SshForwardPorts`, the idle defaults, the
   certificate) and the full parameter table. If you want one host name per VM — and you do
   as soon as two VMs serve web UIs — add `-PublicHostPattern` and a wildcard DNS record
   (see *Per-VM public host names* below). Publish the service first
   (`dotnet publish service\src\Constructd.Api -c Release -r win-x64 --self-contained true
   -o <publish dir>`); no .NET runtime is then needed on the host. A host installed before
   the update API exists needs one carefully backed-up **manual first rollout** of the new
   service and matching scripts. Preserve the current settings rather than rerunning the
   installer with defaults; after that, use the Maintenance
   tab's updater. The exact first-rollout and rollback record is in
   [the host-admin field test](field-test-host-admin.md). `service/host/Uninstall-ConstructHost.ps1`
   is the companion.

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

   `admin iso build` uses the configured native executable (`Iso:NativeBuilderPath`).
   `Install-ConstructHost.ps1 -IsoBuildOnly` also refreshes that executable from local
   source or the pinned release before building.
   The service reuses published media, builds missing media on demand, and downloads and
   patches fresh media when a client selects Redownload. Configure `Iso:SourceUrl` for
   redownload; see [native ISO builds](native-iso.md). A rebuild never overwrites the ISO in
   place — Hyper-V holds an open handle on media a VM has attached — it writes
   `construct-autoinstall-<utc>.iso` next to it, with a sidecar recording when it was
   built, from which source ISO and SHA-256, and which bootstrap key fingerprint is inside;
   then the `current.pointer` swap makes it the one new VMs get. `.\service\host\Install-ConstructHost.ps1
   -IsoBuildOnly` does the same from the installer.

   > **Deferred media build.** With `-SkipIsoBuild`, the native tool is installed and
   > the first VM creation downloads and patches media on the host.

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

   `--max-vms` is the primary-VM count only. Child delegation is a separate allowance,
   edited in the Host administration **Users** tab or through
   `PUT /api/v1/users/{name}/allowance`. Null fields inherit `userDefaults`; the shipped
   defaults allow one retained child, sharing and `never` lifetimes, with no guessed
   per-user CPU/RAM/storage budget (host capacity still applies). For example:

   ```powershell
   $allowance = @{
     allowChildCreation = $true; maxRetainedChildren = 2
     cpuBudget = 8; ramBudgetBytes = 17179869184; storageBudgetBytes = 214748364800
     maxChildLifetimeSeconds = 14400; allowNeverLifetime = $false; allowSharing = $true
   } | ConvertTo-Json
   Invoke-RestMethod -UseDefaultCredentials -Method Put `
     -Uri 'https://buildbox.example.local:7462/api/v1/users/DOMAIN%5Calice/allowance' `
     -ContentType application/json -Body $allowance
   ```

   Host `userCaps` can only narrow those values; a per-primary override can only narrow
   delegation again. Policy is re-evaluated on every request, so disabling a user or
   lowering an allowance takes effect without replacing credentials.

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
| **Reprovision** | re-runs `Provision-AgentVM.ps1` against the instance's endpoint. Keeps all data. Requests released source from the service cache when supported, then provisions over SSH. |
| **Reinstall** | `DELETE /vms/{name}` → `POST /vms` → provision. Same typed-`yes` confirmation as the local path, and the same pre-wipe unsaved-work scan + config save. |
| **Export config** | pulls the VM's agent config back to this host. No changes to the VM. |
| **Remove instance** | `DELETE /vms/{name}` **and** removes everything this PC knows about the VM (see below). Needs the instance name typed back. `-KeepVm` (or the prompt when the host is unreachable) forgets it here and leaves the VM on the host. |
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
   per-instance state file (its settings, its provisioned commit, and the T3 origin and
   OpenCode url the provisioner recorded for it) and, **last and only if every step
   before it succeeded**, its registry entry.

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
| **The VM's scoped token** | `/etc/construct/vm-token` **inside the guest**, mode 0600 | written by `provision.sh` from `CONSTRUCT_VM_TOKEN_B64`. Legacy credentials authorise that VM's forwards/heartbeat plus identity/reporting. New primary credentials also discover effective delegation; they never grant user/admin access. |

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

**A lost token can be replaced.** An owner/admin user credential can call
`POST /vms/{name}/token` and deliver the replacement with
`Provision-AgentVM.ps1 -RotateVmToken -ServiceUrl <url> -InstanceName <name>`.
This explicit switch invalidates the old credential before provisioning and uses the same SSH stdin
secret channel. Ordinary reprovisioning keeps the existing token. The optional `-ServiceApiAuth`
hashtable selects the existing token/Negotiate authentication flow; the remote installer supplies it
from enrollment. Never put a plaintext token in an external command line.

### Host administration PowerShell client functions

These functions are in `lib/AgentVm.Remote.ps1` and reuse its certificate pinning and authentication:

| Function | Parameters and result |
|---|---|
| `Send-ConstructGuestReport` | `-BaseUrl`, `-VmName`, `-Event provisioned|reinstalled|attempt`, optional `-Outcome succeeded|failed`, `-ConstructCommit`, `-Auth`, `-Pin`, `-StoreDir`. Returns a boolean; errors are advisory and the HTTP timeout is five seconds. Empty host/VM makes no call. |
| `Request-ConstructVmTokenRotation` | `-BaseUrl`, `-VmName`, optional `-Kind primary|legacy` (primary default), `-Auth`, `-Pin`, `-StoreDir`. Returns the one-time `{vmToken,kind,issuedAt}` response. Failure throws a fixed error without transport details. |

After a parsed, clean guest provisioning result, `Provision-AgentVM.ps1` reports Construct commit
and successful provisioning time. `-ProvisionEvent reinstalled` records reinstall time separately;
the remote installer's reinstall path supplies it. Failed/invalid final guest results report an
attempt without changing earlier success facts. Failures before the final guest result is reached
cannot submit that completion hook. Reporting never changes provisioning's exit result and local
provisioning does not contact constructd. Guest timestamps are reports with provenance; a host boot
observation never means that a child OS was provisioned.

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
| The VM was created but provisioning failed | the instance is already in the registry (§3, step 8). Run `Auto-Install.ps1 -InstanceName <name>` and pick **Reprovision** — no VM is created twice; the service may populate its source cache. |
| The job fails and the VM disappears | creation rolls back deliberately (`service/README.md`): a partially created VM would keep consuming disk while holding its name. The original failure is reported, never masked. |
| Reinstall/Redownload refused in the panel | the installed host scripts predate the remote parameters, so the action would have hit a *local* VM. Update The Construct on this PC. |
| "this PC's instance registry would refuse …" | an identity clash with an instance you already have — the message names it and the field (a shared `configBranch`, `keyName`, `hostAlias`, `vmName`, or the same `sshHost` **and** `sshPort`). Before the VM is created nothing has happened; after it, the create is rolled back. See the section below. |
| `Refusing to talk to the Construct host service … over plain http` | you gave an `http://` URL for a host that is not this machine. There is nothing to pin and nothing to encrypt, so a token or a Windows credential would cross the network in clear. Both clients refuse before sending anything. Use `https`; plain http is accepted only for a service on `localhost` (which is how the tests drive the fake service). |
| A warning about sending a Windows credential over plain http | you pointed at a service on **this** machine over `http://`. That is allowed, but Kerberos/NTLM is not encrypted in transit there, so the client says so once. |

## 8. Administering the host from VS Code

An **administrator** of the host (a user with the `admin` role) gets a native
**Host administration** panel in VS Code; everybody else gets nothing of it — the module
is absent for a local install and for a remote identity that is not an admin, not merely
greyed out. It is the API client of `service/README.md`'s host-administration routes
(contract: `docs/plans/host-administration-contracts.md` §10) and never touches the
host's filesystem or assumes the service runs on this PC.

**Opening it.** *The Construct: Host Administration* from the palette (it asks which
enrolled host when there are several), the **⚙ Host** button in the control panel header
(shown only when the active instance's host says you are an admin), or the *Host
administration: `<host>`* row in *Switch Instance*. Enrolment is enough — you do not need
a VM on the host yet; the Overview offers **Create first Construct VM here**, which is
the ordinary *New VM on Remote Host* flow with the host preselected. Hosts already
configured in the local VM registry are also discovered, including VMs created by
`Auto-Install.ps1`; they do not need a second **Add Remote Host** enrollment. The
existing certificate pin and authentication mode are reused, and admin status is
checked against the host.

**What it shows.** Overview (service version, health, capacity bars with the
`observe`/`enforce` badge, maintenance state, active jobs, overdue leases, unmanaged
VMs), VMs (every user's primaries and their children: kind, parent, sharing, power state,
resources, lease or **OVERDUE**, the operation in progress, and the guest's reported
Construct commit / provision / reinstall times — printed as *unknown* when nothing was
reported; a successful boot never counts as provisioned), Users (register/remove, role,
enable/disable, primary quota, host-forward permission, the delegation allowance —
empty = inherit the host default —, per-VM overrides, and token issue/revoke; a new
token is shown once with a Copy button and never stored), Media (the primary ISO catalog,
read-only; the child media inventory with delete and cleanup), Operations (jobs with
cancel, retry buttons for failed deletes and cleanups, the audit log), Configuration
(the host-config sections as JSON, validation problems shown next to the section) and
Maintenance (the host service's own update: check, stage, apply, resume, cancel,
resolve — see §11 of the contract).

The Admin-only `GET /host/iso-catalog` projects the primary source and patched catalog.
The Media tab loads child inventory independently: a failed or unavailable catalog read
shows a catalog-specific problem while child media remains usable. Source URLs omit
credentials, query strings and fragments. `constructd admin iso status` remains available
locally on the host.

**VM settings.** Each primary VM row offers **VM settings…**: CPU count, fixed RAM
(whole GB), idle timeout and idle action in one dialog, with current/pending hardware,
owner/host maxima and the idle cap. Apply saves changes; CPU/RAM require the row's
confirmed **Restart**, or a full stop followed by **Start**. Resuming saved state or
rebooting Ubuntu does not apply them. The guest sees its new RAM/CPU after the cold boot.
Idle changes take effect immediately; **Off** disables idle handling, and is unavailable
when the host forces it. Admins obey the owner's resource allowance and the host idle cap.
Capacity is checked again at start. If one save fails, already-saved fields remain saved;
the dialog reloads actual values and stays open for review/retry. Older hosts keep idle
editing and disable hardware fields they do not support. The Companion shares this dialog.

**What it deliberately lacks.** No guest update, provision, reinstall or redownload —
those stay in each instance's own panel and console. No child start/resume, console or
sharing for ordinary users in the panel; `construct vm …` inside the primary has them.

**Useful states instead of errors.** An unreachable host says so and offers Retry (with
the last successful read); a host whose service predates host administration says so
and tells you to update it *on the host* (`service/host/Install-ConstructHost.ps1`) —
nothing can be driven from here; a host missing one feature disables just that tab
("not available on this host version"); a rejected credential offers *Sign in again*;
an identity that is not enrolled or is disabled is told so; a role that changes to user
while the panel is open flips it to the ordinary-user state on the next call; while the
host is updating a banner shows the phase, every mutation is disabled and the panel
re-polls `/health` every five seconds.

**Every user: the Child VMs card.** Under a remote primary the control panel lists its
child VMs (name, state, lease expiry or *OVERDUE* with the last outcome, private or
*shared host-wide*) with exactly two actions. **Shut down** asks the guest to shut down
gracefully — the same request the panel's own Shutdown makes, never a save and never a
force-off — and reports the real outcome: a guest without integration services, or one
that does not power off within the host's timeout, is reported as *not* shut down.
**Delete** confirms with the child's name, its sharing state and "disk, saved state and
dedicated media are removed permanently". The card is hidden when the host's service has
no child VMs. Deleting a **primary** that has children from the admin panel shows the
cascade confirmation: every child, shared ones highlighted, the permanent disk removal,
and the instance name typed to confirm; if a child appears or changes meanwhile the list
is shown again. *Remove instance* (the console's `Auto-Install.ps1 -Action
remove-instance`) does **not** handle that confirmation yet: for a primary with children
it stops at the service's `409 cascade-confirmation-required`; delete such a primary from
the admin panel, or delete its children first.

## 9. Children, sharing and allowances

A remote primary receives a `primary` token when newly created. A primary migrated from
an older database keeps its existing token as `legacy`: old heartbeat and self-forwarding
continue unchanged, but `construct vm identity` reports that delegation is unavailable.
The owner upgrades it explicitly with
`Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken`; rotation invalidates the
old token immediately. The planned **Reprovision (upgrade VM credential)** menu item is
not wired into VS Code or Auto-Install yet; ordinary reprovisioning does not upgrade it.

Inside an upgraded primary, [`construct vm`](child-vms.md) is the complete child interface:
public-URL or resumable-upload media, explicit CPU/RAM/disk/lifetime, powered-off creation,
lifecycle and lease renewal, sharing, hardware/media changes, console screenshots/input,
jobs and child-target forwards. Children have exactly one primary parent, inherit its
human owner and receive no Construct credential. Child slots and storage remain charged
while Off or Saved; runtime RAM/CPU is released only after the hypervisor confirms a
terminal state.

Children begin `private`. `host` sharing lets registered users and their upgraded
primaries inspect and operate the child, use the console, and request an eligible client
forward. It does not reveal guest credentials, move ownership, permit deletion or
hardware/media changes, or provide network isolation. Every resource remains charged to
the owner. Deleting a primary always cascades to all of its children, including shared
ones, after an expiring scope preview and typed confirmation.

Finite lifetimes are wall-clock leases. Creation and every start/resume require an
explicit lifetime; restart, sharing, guest reboot and service restart do not renew it.
Expiry requests a graceful guest shutdown. If integration services are unavailable or
the timeout expires, the child stays running and `overdue`; there is no force-off, save or
delete fallback.

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

## Updating the host

After the first manual rollout of the update-capable service, an Admin can use the host
panel's Maintenance tab to check `main`, stage its release, and apply it. Checking
shows the pinned commit and compatibility result. Staging downloads and verifies the
package without stopping the service; applying drains conflicting host jobs, hands off
to a SYSTEM scheduled task, restarts the service, and verifies service/database health.

VM creation/deletion, media work, ISO builds and host reachability waits must finish
before replacement. New conflicting work receives `503 maintenance` with `Retry-After`.
PC-driven provisioning and ordinary guest activity do not block draining. The short
replacement/recovery window freezes all host mutations. Existing Hyper-V VMs are not
stopped by the updater. Settings, certificates, media, users, tokens, VM registrations
and unowned files are preserved.

Reconnect and read `/api/v1/host/updates/status` to recover the persisted result. Reuse
an operation key when retrying stage/apply; do not blindly repeat a mutation after a
connection failure. An interrupted/mixed installation stays in maintenance until the
Admin resumes or resolves it. `last-update.json` under the service data directory's
`updates` folder remains readable if the service cannot start.

Manual first deployment, retention, recovery fences and rollback
limits are documented in [Host releases and deployment](host-release.md). No real-host
update has been validated by the Linux test run. No signing-key setup is needed.

### Child networking

A child receives no Construct credential. Its parent primary (with a `primary`
token), its owner, an administrator or an eligible host-shared consumer requests
`POST /api/v1/vms/CHILD/forwards`. Use `target: "client"` (the default), `vmPort`,
and optionally `connectPort` and `via`. A user with multiple primaries must name
`via`; a primary token implies itself. A shared consumer uses their own primary's
SSH connection. An administrator accessing another owner's child must explicitly
name a primary they own.

Child forward responses carry `destination` with the child VM, requester,
relationship, `via`, address, port and `verified: false`. The extension polls
`GET /api/v1/vms/PRIMARY/forwards?via=PRIMARY`, opens an SSH local forward from that
primary to the child's reported address, then acknowledges on the child's route.
Only the `via` primary's human owner or an administrator may acknowledge. Each
request has its own row; shared consumers cannot overwrite other consumers' acks.
Owner/admin/parent listings can use `?includeChildren=true` on their primary.
Existing primary forwards retain their flat response shape and credential rules.

`GET /api/v1/vms/CHILD/addresses` reports KVP addresses; an empty list is normal
before guest networking/integration services work. Direct reporting can be disabled
with `network.directAddressReporting`. Child client destinations must share a
switch/subnet with `via`, match the recorded child incarnation and reporting adapter,
and exclude host, loopback, link-local, multicast, broadcast and conflicting managed
VM addresses. These checks prevent accidents; they do not prove IP ownership.
Unknown addresses produce an error forward that can recover when an address appears.
Periodic reconciliation clears stale acknowledgements on address change and removes
forwards whose requester, primary or sharing grant is no longer eligible.

Host forwarding is independently controlled by `network.hostForwardsEnabled` and
the **child owner's** `AllowHostForwards`, including shared and parent requests.
Even when both allow it, Hyper-V child host forwards return `409 address-unverifiable`
with `reason: "no-address-authority"`. Hyper-V cannot establish IP ownership from
KVP, MAC spoofing protection or neighbor-table entries. Primary host forwards keep
the existing endpoint mechanism.

Isolation is **none**. The service persists parent-child and shared-consumer rules
as **intended**, never enforced. The network reconciliation interface accepts VM
creation/deletion, sharing and address change events and repairs intended state
periodically. The lifecycle sharing endpoint must call `OnSharingChangedAsync`
after committing a change; that endpoint is a separate integration increment.
No firewall or Proxmox adapter is installed. See [the service networking seams](../service/README.md#child-network-adapters)
for the future adapter inputs and events.

The forward slot limit is per **target child**, shared by its owner and all shared
consumers. A shared consumer can occupy the child's available slots; the owner or
parent can remove unwanted forwards. Per-requester slot budgets are not implemented.

### Final-review compatibility notes

Observe mode records capacity decisions from periodic inventory and tolerates unavailable
primary storage placement with unknown-volume accounting. It still refuses unknown VM
state at create/start, active operations, incomplete configuration and foreign retained
disk ownership with structured 409 problems. Background observation is waited on instead
of causing a transient operation conflict. Primary start returns the state actually
observed; successful driver invocation alone does not promise Running.

GET state does not persist a refresh; list state follows reconciliation. An unmatched
primary delete name returns 404, nonowner deletion returns a coded 403, and replay of a
live delete job returns 200 with that job. Remote feature/identity probes are cached;
children are queried only after feature support is known. IPC JSON requests include
`Content-Length` when framing is otherwise absent so the SSH bridge preserves their body. The local provisioning flow and existing legacy-token authority
remain intact; these explicit remote API exceptions are recorded in the frozen contract.

If delivery fails after an explicit credential rotation, the previously installed token
has already been invalidated. Restore SSH reachability, then rerun
`Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken` to issue and deliver a new
credential. Ordinary reprovisioning does not recover the invalidated credential.

### Reprovision without uploading the checkout

With `source-cache` in the service's `apiFeatures`, the PC ensures its exact Construct commit
on the host, then the guest pulls the verified ZIP directly. The host downloads each released
commit once and preserves it across reprovisions and VMs. The PC still sends per-VM settings,
project profiles via config sync, git identity, keys, credentials and saved configuration through
the existing channels. The fetch uses the VM's own token in a private header file; first provision
stages that token over SSH stdin before fetching. Secret transport rules in §5 are unchanged.

`Provision-AgentVM.ps1 -InstanceName <name> -SourceMode auto` is the default. It uses the cache
for `constructRef=main` with a known released commit. When files differ, the guest fetches
that commit from the host and the PC uploads only the modified and added files, plus a deletion
list, in an overlay ZIP. Git checkouts include non-ignored untracked files even if git is
configured to hide them. Archive installs compare files with the per-file manifest at
`%LOCALAPPDATA%\The-Construct\source-manifests\<commit40>.sha256`; the existing local-artifact
exclusions still apply to added files, including legacy `projects/*.json` profiles.

An overlay may contain at most 5,000 changed files and 64 MiB of uncompressed data. Changes
that cannot be listed, larger overlays, or overlay packing/upload/application failures fall back
to the full upload with a reason. Old installs without a manifest upload until
`Update-Construct.ps1` runs once.

`-SourceMode upload` explicitly uses the old pack/scp/unpack path. `-IncludeGit` also uses it.
`-SourceMode cache` explicitly selects the commit without any local changes or overlay, and stops on
cache failure instead of falling back. These switches are available on `Provision-AgentVM.ps1`;
Auto-Install and panel reprovision commands use the default. Local Hyper-V provisioning retains
its original upload path.

On success the client prints `Construct source: host cache (commit …, … KB); nothing uploaded
from this PC.` With an overlay it instead reports `Construct source: host cache (commit …, … KB) + … differing file(s) (… KB) uploaded from this PC.` These sizes are compressed bytes. This refers to source transport. In auto mode it warns with the reason and
uploads if the service is old/disabled/unreachable, the commit is unreleased, the cache is full,
the job fails or times out, or the guest cannot verify/install the ZIP. The feature probe is
bounded to 10 seconds, ensure to 30 seconds, and job polls stop after three consecutive failures
(at most 94 seconds including sleeps). `-SourceEnsureTimeoutSec` defaults to 900 seconds for a
host that continues answering while downloading. A timeout leaves the host download running.
Guest transfer attempts have their own 600-second bound and one retry.

Admins inspect `GET /api/v1/host/source-cache`: `committedBytes` includes ready, downloading and
pending-deletion items. `source-cache-full` requires deleting unused entries with
`DELETE /api/v1/host/source-cache/<commit>`; pinned entries require `?force=true`.
`POST /api/v1/host/source-cache/cleanup` retries failed cleanup but never evicts ready source.
Source limits and directory are bootstrap `Constructd:HostAdmin:Source:*` settings.

The ZIP contains tracked source. Ignored host files (`runtime/`, `.env`, `*.local`, ISOs,
`.construct-tools/`, settings, backup, Python caches and `.claude/worktrees/`) and `.git` are
absent. Guest scripts use tracked files and the separately delivered live configuration.
