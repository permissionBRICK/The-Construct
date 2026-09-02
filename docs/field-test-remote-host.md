# Field test — the remote host, on a home AD domain

A step-by-step checklist for the **first** real run of the
[remote host](remote-host.md) on a domain: install `constructd` on a Hyper-V box, enrol a
user, create and provision two VMs from a client PC, and exercise the things that only
break in the field — Kerberos, instance switching, mic passthrough, `construct expose`,
PC-independence and the idle policy.

Every step names the exact command or UI action, **what you should see**, and **where to
look when you don't**. Work through it in order; each step assumes the one before it
passed.

Names used throughout — substitute your own:

| Placeholder | Example |
|---|---|
| Hyper-V host | `buildbox` (`buildbox.home.example`) |
| Domain | `HOME` |
| Admin identity | `HOME\christoph` |
| Test user | `HOME\alice` |
| Service URL | `https://buildbox.home.example:7462` |
| The two VMs | `work-vm`, `lab-vm` |
| Construct checkout on the host | `C:\Construct` |

---

## 0. Before you start

- [ ] The host runs Windows with **Hyper-V** enabled and is **domain-joined**.
- [ ] The client PC is domain-joined too (that is what makes the Kerberos path testable)
      and has VS Code with the Construct control panel installed.
- [ ] A Construct checkout exists on the host at `C:\Construct` (the service invokes its
      `drivers\`, `lib\` and `bin\`).
- [ ] The .NET SDK is on the host **for the publish step only** — the published service is
      self-contained, so no runtime is needed afterwards.
- [ ] Decide the ports now: the API (`7462`), the SSH forward range (`2201-2299`) and the
      app forward range (`2300-2999`). **They must not overlap** — the installer refuses
      overlapping ranges, and so does the service at startup.
- [ ] Have somewhere to write the **admin token** down — it is printed **once** and only
      its hash is stored. The certificate value is not one-time (the installer prints it on
      every run and reuses the certificate), but you will want it to hand anyway.

> **Every raw API call in this checklist uses `curl.exe -k`, on purpose.** The service's
> certificate is **self-signed**, and this checklist does not install it as a trusted CA
> anywhere. Construct's own clients do not need one — they pin the fingerprint you confirm
> at enrolment — but `curl`, `Invoke-RestMethod` and `Invoke-WebRequest` know nothing about
> that pin, so without `-k` they fail the TLS handshake before any of the behaviour under
> test can be observed. `-k` is acceptable **here** because you verified the fingerprint out
> of band in step 1.3 and again at enrolment; it is not a pattern to copy into anything that
> matters. `curl.exe` is also what prints a 4xx *body* instead of throwing, which several
> steps depend on.
>
> If you would rather not use `-k`, install the exported certificate into
> `Cert:\LocalMachine\Root` on the machine you run the commands from first — then drop the
> flag everywhere below.

---

## 1. Host — install `constructd`

### 1.1 WSL, as *LocalSystem* sees it

The ISO build runs in WSL, and the service runs as **LocalSystem** — and WSL distros are
registered *per Windows user*, so a distro you can see as the administrator says nothing
about what the service will see.

```powershell
wsl --install -d Ubuntu        # if there is no distro at all; reboot when asked
```

The installer checks the LocalSystem view itself and fails with the exact remedy if the
distro is missing there. To have it do the `wsl --export` / `wsl --import`-as-SYSTEM dance
for you, add `-ProvisionWslForService` in step 1.3.

**Expect:** `wsl -l -q` lists a distro.
**If it fails:** the check runs through a one-shot scheduled task; it *fails closed*, so
"the LocalSystem probe could not run at all" stops the install rather than reporting
success. `-SkipPrereqs` is the deliberate override, not a workaround.

### 1.2 Publish the service

```powershell
dotnet publish C:\Construct\service\src\Constructd.Api -c Release -r win-x64 `
    --self-contained true -o C:\Construct\service\publish
```

**Expect:** `C:\Construct\service\publish\Constructd.Api.exe` exists.

### 1.3 Run the installer

```powershell
C:\Construct\service\host\Install-ConstructHost.ps1 `
    -ScriptsDir C:\Construct `
    -PublishDir C:\Construct\service\publish `
    -PublicHost buildbox.home.example `
    -AdminUser HOME\christoph `
    -IsoSourceUrl https://releases.ubuntu.com/24.04/ubuntu-24.04.3-live-server-amd64.iso
```

It self-elevates, and it supports `-WhatIf` on everything that changes the machine — worth
one dry run first.

In order it: validates the inputs → creates the service root and data directory → **locks
down** `-PublishDir`, `-ScriptsDir` and the service root (LocalSystem executes what it finds
there) → checks the prerequisites → creates the TLS certificate → adds three inbound
firewall rules (API port, SSH range, app range) → writes
`appsettings.Production.json` → **creates the first admin and issues its token before the
service starts** → registers `constructd` as LocalSystem → starts it → prints the
enrolment details.

**Expect** a final block with:

```
  Service URL   : https://buildbox.home.example:7462
  Certificate   : <40 hex characters>
  Admin token for HOME\christoph (shown once):
  <token>
```

**Copy the token now — that one really is shown once**, and only its hash is stored; a
re-run issues no new token unless you pass `-RotateAdminToken`. The **certificate value is
not** one-time: the installer reuses the existing certificate on later runs (a fresh one
would break every client's pin) and prints its thumbprint again each time, so you can always
read it back.

**⚠ The printed `Certificate` value is the SHA-1 thumbprint; the client pins and shows the
SHA-256 fingerprint.** They will not look alike, so publish the SHA-256 form to your users
— compute it on the host:

```powershell
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq '<the printed value>'
([BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData))) -replace '-', ':'
```

**Expect:** 32 colon-separated uppercase hex pairs — that is exactly the spelling the client
shows at enrolment (step 3.1).

**If the install fails:** it stops at the failing step and says which. Common ones —
overlapping port ranges (fix the arguments), an ancestor directory an untrusted account can
delete or a reparse point in the path (move the directory, or take responsibility with
`-SkipAclHardening`), or the WSL/LocalSystem check from 1.1.

### 1.4 Confirm it answers

```powershell
Get-Service constructd
curl.exe -k -H "Authorization: Bearer <admin token>" https://buildbox.home.example:7462/api/v1/whoami
```

**Expect:** the service is `Running`, and `whoami` returns your identity with
`"role":"admin"` and `"known":true`. (Enum values on the wire are **camelCase** —
`admin`/`user`, `running`/`saved`, `save`/`shutdown`/`off`, `client`/`host` — so a literal
`"Admin"` would be the wrong thing to look for.)
**If it fails:** `Get-EventLog -LogName Application -Source constructd -Newest 50` — the
service logs there when hosted as a Windows service. A service that starts and immediately
stops is usually a configuration refusal (a `ScriptsDir` that is not a Construct checkout,
overlapping ranges, or an unreadable portproxy table); all of them say so in the event log.

---

## 2. Admin — enrol the test user

There is **no self-registration**. Either path works; the CLI is the one that also works
when the API will not start.

```powershell
$constructd = "C:\Construct\service\publish\Constructd.Api.exe"

& $constructd admin users add HOME\alice --role User --max-vms 2
& $constructd admin tokens issue HOME\alice --label "alice laptop"
& $constructd admin users list
```

**Expect:** `users list` shows `HOME\alice`, role `User`, quota `2`, `hostForwards=True`.
`tokens issue` prints a token **once** — copy it, only its hash is stored. (The CLI prints
the role's .NET name, `User`/`Admin`; the HTTP API serializes the same value camelCased as
`user`/`admin`. Both are correct — do not read the difference as a bug.)

Two VMs is the point of `--max-vms 2`: the quota is enforced by the insert itself, so a
third `POST /vms` is refused rather than racing through.

**If it fails:** exit code `2` is a usage error (an unknown option or a stray argument is
refused rather than ignored — a quota that did not take is worse than a refusal), `3` is
"no such user", `4` is "already exists". Everything the CLI does is audited with actor
`admin-cli`.

**Note for the Kerberos test:** a domain user who authenticates with Kerberos needs **no
token at all** — the `users add` is enough. Issue one anyway; step 4 uses it.

---

## 3. Client PC — enrol the host and create VM #1 (Kerberos)

Sign in to the client PC **as `HOME\alice`**. Kerberos here means *this Windows session's
identity*, so who you are logged in as is the test.

### 3.1 Start the installer in remote mode

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote `
    -ServiceUrl https://buildbox.home.example:7462 -ServiceAuth negotiate `
    -InstanceName work-vm
```

**Expect, in order:**

1. **No UAC prompt** — a remote install creates nothing locally, and the installer says
   *"Remote host install — no administrator rights are needed on this PC."*
2. The **certificate fingerprint**, printed in full, with a confirmation prompt. Compare it
   with the SHA-256 value from step 1.3 and confirm. It is then pinned to
   `%LOCALAPPDATA%\The-Construct\remote\<hostslug>.pin` and enforced on every later call.
3. *"Authenticated with this Windows session's identity."* — this is the Kerberos path
   passing. It is tried **silently and first**; you are only asked for anything on a 401.
4. A `whoami` line naming `HOME\alice`, role `user`, quota 2.
5. The usual install questions (RAM, disk, projects, git identity, agent password) — the
   same screens as a local install.
6. The create job streaming into the scrolling log: ISO build → VM create → OS install wait
   → media detach → SSH forward allocated, ending with
   `Endpoint: buildbox.home.example:2201`.
7. `Recorded the instance 'work-vm' in %LOCALAPPDATA%\The-Construct\instances.json` —
   **before** provisioning, deliberately.
8. `Provision-AgentVM.ps1` running against `buildbox.home.example:2201`, exactly as it would
   against `agent-vm.mshome.net:22`.
9. VS Code opening on the new VM.

**If the fingerprint does not match:** stop. Either the host's certificate changed (a
reinstall) or something is between you and it. Confirm with the admin before pinning.

**If step 3 gives a 401:** the host does not accept your Windows identity — not
domain-joined, no SPN for the service's host name, or you were never enrolled. Skip ahead:
the installer offers *API token* / *Domain account*, which is exactly step 4's test.

**If `whoami` says `known: false`:** you authenticated fine but nobody enrolled you — go
back to step 2. This distinction is why `/whoami` answers for unenrolled identities at all.

**If the create job fails:** the failure is reported, never masked, and the **partially
created VM is rolled back** (an orphan would hold its name, its disk and the host's RAM).
Look at `GET /api/v1/jobs/{id}` for the job's own progress lines, and at the host's
Application event log for the service's side. The service deliberately never repeats a
child process's error text, so expect *its* wording ("powershell.exe exited with 1", "timed
out after 30 minutes") plus the driver's streamed progress lines.

**If provisioning fails after the VM was created:** nothing is lost — the instance is
already in the registry (that is why it is written first). Re-run
`.\Auto-Install.ps1 -InstanceName work-vm` and pick **Reprovision**. Nothing on the host is
touched and nothing is created twice.

### 3.2 Sanity-check the VM

```powershell
ssh work-vm "hostname; grep '^CONSTRUCT_' /etc/construct/config.env"
```

**Expect:** the VM answers, and `config.env` carries

```
CONSTRUCT_EXTERNAL_HOST=buildbox.home.example
CONSTRUCT_EXTERNAL_SSH_PORT=2201
CONSTRUCT_SERVICE_URL=https://buildbox.home.example:7462
CONSTRUCT_INSTANCE_NAME=work-vm
```

Also check the guest token and the heartbeat timer:

```bash
ssh work-vm "ls -l /etc/construct/vm-token; systemctl status construct-idle-report.timer --no-pager"
```

**Expect:** the token file exists, mode `0600`, owned by root; the timer is **active**.
**If the timer is missing:** it is installed only when `CONSTRUCT_SERVICE_URL` is non-empty
— i.e. the guest thinks it is a local install. Check `config.env` above.

---

## 4. Create VM #2, and prove the other two credentials

Three credentials have to be exercised — Kerberos (step 3), a token, and manual domain
credentials — but only **one Windows profile** may own the VMs, because the instance
registry and the token store both live under `%LOCALAPPDATA%` of the account that ran the
installer. A VM created under a second account would be invisible to the first, and steps
5–8 could not switch between the two.

So: **stay signed in as `HOME\alice` and create both VMs there.** The token and credential
paths are proven separately, creating nothing.

| Sub-step | Windows account | Creates | Proves |
|---|---|---|---|
| 4.1 | `HOME\alice` | `lab-vm` | two VMs on one host; Kerberos again |
| 4.2 | `HOME\alice` | nothing | token authentication |
| 4.3 | a local (non-domain) account | nothing | the manual-domain-credential fallback |

### 4.1 Create `lab-vm` (Kerberos, same as step 3)

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote `
    -ServiceUrl https://buildbox.home.example:7462 -ServiceAuth negotiate `
    -InstanceName lab-vm
```

**Expect:**

1. No fingerprint prompt this time — the host is already pinned from step 3.
2. *"Authenticated with this Windows session's identity."*
3. `Endpoint: buildbox.home.example:2202` — the **same host, a different port**. This is the
   composite-endpoint rule doing its job: the registry treats `(sshHost, sshPort)` as one
   identity, so several VMs legitimately share a service host and are told apart by the
   forward they were allocated.
4. `Recorded the instance 'lab-vm' …`, then provisioning, then VS Code opening on it.

**Why Kerberos rather than the token here:** an instance created with `-ServiceAuth token`
is recorded as `service.auth: "token"`, and the **extension** then needs that same token in
VS Code *SecretStorage* to drive it — the installer's DPAPI copy is deliberately unreadable
to it. *Add Remote Host* only offers the token prompt after a `401`, so on a domain PC where
Kerberos works there is no supported way to put it there, and the panel would report the VM
as `unknown`. Both VMs on `negotiate` keeps steps 6–11 executable.

**If you get "this PC's instance registry would refuse 'lab-vm': …"** the message names the
clashing entry and the field (`configBranch`, `keyName`, `hostAlias`, `vmName`, or the same
`sshHost` **and** `sshPort`). Before the VM is created nothing has happened; after it, the
create is **rolled back** and the failure says so.

**If you get a quota refusal:** `--max-vms` was too low — step 2.

### 4.2 Token authentication (creates nothing)

`-ServiceAuth token` tries the **stored** token first and silently falls back to Negotiate
when there is none — so it only prompts for a paste after a `401`, which will not happen in
alice's domain session. Store the token explicitly instead, then use it:

**Do not put the token on the command line.** PowerShell echoes it and PSReadLine writes the
whole line to `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt`,
which would leak a credential the store exists to protect. Prompt for it instead:

```powershell
# 1. store alice's token from step 2, DPAPI-encrypted for this Windows account
. .\lib\AgentVm.Remote.ps1

$secure = Read-Host "Alice's API token" -AsSecureString     # not echoed, not in history
$bstr   = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    Save-ConstructRemoteToken -BaseUrl https://buildbox.home.example:7462 `
        -Token ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)   # wipe the unmanaged copy
    $secure.Dispose()
}

Get-ChildItem "$env:LOCALAPPDATA\The-Construct\remote\*.token"

# 2. authenticate with it against the instance that now exists
.\Auto-Install.ps1 -InstanceName lab-vm -ServiceAuth token
```

**Expect:**

1. Nothing echoed while you paste, and `Get-ChildItem` lists a `<hostslug>.token` file whose
   contents are **DPAPI ciphertext**, not the token — `Get-Content` on it shows base64-ish
   bytes only your Windows account can decrypt. (The plaintext exists briefly as a managed
   string inside the call, which .NET will not let you wipe; what matters here is that it
   never reaches the command line, the history file or the disk.)
2. The installer prints **"Authenticated with the API token stored for this host."** — the
   distinct line that proves the bearer token was used, not the Windows identity.
3. A `whoami` line naming `HOME\alice`, role `user`, quota 2.
4. The **existing-instance menu** — *Reprovision / Reinstall / Export config / Quit*. Choose
   **Quit**: authentication is what this step tests, and nothing should change.

**Observable result to record:** token authentication succeeded, nothing created or changed.

**If it says "The API token stored for this host was refused":** the token is wrong or has
been revoked — re-issue it (step 2). **If it prints the *Windows session* line instead**, no
token was stored, so this step did not test anything.

**On a client PC where Kerberos does not work**, the paste prompt is reachable directly:
run the installer with `-ServiceAuth token` and no stored token, and expect the
*API token / Domain account / Cancel* menu → **API token** → *"API token stored (encrypted
for your Windows account)."* Same credential, one screen earlier.

### 4.3 Manual domain credentials (a second account, creates nothing)

The credential menu only appears after a `401`, so this needs a session the host will not
accept. Sign in to the client PC with a **local (non-domain) Windows account**.

> That account has its **own** `%LOCALAPPDATA%`, so it has its own instance registry and its
> own token store. That is exactly why this step creates nothing — a VM made here would be
> invisible to `HOME\alice` and to every later step.

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote `
    -ServiceUrl https://buildbox.home.example:7462 -InstanceName credtest-vm
```

**Expect:**

1. The fingerprint prompt (this account has no pin of its own) — confirm it.
2. Negotiate is tried and refused, then the menu: *API token* / *Domain account* / *Cancel*.
3. Pick **Domain account** and enter `HOME\alice` plus the password.
4. *"Authenticated."*, then a `whoami` line naming `HOME\alice`, role `user`, quota 2. The
   password is used for **that run only** and is never written anywhere — which is why an
   instance created this way would record its auth mode as `negotiate`.
5. The first configuration question (RAM). **Press Ctrl+C here.** Nothing has been created:
   the service is not asked for a VM until every question is answered.

**Observable result to record:** the credential path authenticated and `whoami` resolved
`HOME\alice`, with no VM created on the host and nothing written to alice's registry.

**Verify that** before moving on: sign back in as `HOME\alice` and check the host has only
the two VMs —

```powershell
$auth = "Authorization: Bearer <alice's token>"
(curl.exe -sS -k -H $auth https://buildbox.home.example:7462/api/v1/vms | ConvertFrom-Json).name
```

**Expect:** `work-vm` and `lab-vm`, and no `credtest-vm`.

**If the run is not interactive** (piped input), it fails with a clear message instead of
looping. **If the menu never appears**, Negotiate succeeded — that account is not as
un-privileged as assumed, and this credential was not exercised.

---

## 5. Verify the instance registry

```powershell
Get-Content "$env:LOCALAPPDATA\The-Construct\instances.json"
```

**Expect** three instances' worth of truth:

- `agent-vm` may or may not be present — it is **implicit**. Absent is correct on a PC that
  never had a local VM.
- `work-vm` and `lab-vm`, each with `"backend": "hyperv-remote"`, a `service` object of
  `{ "url": "https://buildbox.home.example:7462", "auth": "negotiate" }`, `"vmName"`
  **equal to the instance name**, a stated `"sshHost"`, their own `sshPort` (2201 / 2202),
  `hostAlias` = the name, `keyName` `construct_<name>_ed25519`, `configBranch` `vm-<name>`,
  and `owner` `HOME\alice`.
- **No `credtest-vm`** — 4.3 ran under a different Windows account and created nothing.

Check the client-side wiring too:

```powershell
Get-Content "$env:USERPROFILE\.ssh\config" | Select-String -Context 0,5 'Host work-vm','Host lab-vm'
dir "$env:USERPROFILE\.ssh\construct_*_ed25519"
```

**Expect:** one `Host` block per instance, each with its own `HostName`, `IdentityFile`,
`IdentitiesOnly yes` and a `Port` line; one key file per instance. Only the block for an
alias is ever replaced, so the two coexist.

**If an instance is missing from the panel:** it did not load. The registry is read
fail-closed — an entry whose identity is unusable, whose `backend` is present-but-unusable
or only case-different from a real id, or that collides with another entry, is skipped
whole. The control panel toasts the reason and writes it to its log
(**The Construct: Show Logs**).

---

## 6. Switch instances in the extension

### 6.0 Tell the extension about the host

The extension keeps its **own** record of a remote host — the PowerShell client's DPAPI
token and the extension's SecretStorage cannot read each other, by design. Enrol it once:

- [ ] Run **The Construct: Add Remote Host** from the command palette and enter
      `https://buildbox.home.example:7462`.

**Expect:** *no* fingerprint prompt — the pin file the installer wrote in step 3 is the same
file the extension reads, so a host confirmed in the console is already trusted here. Then a
progress notification *"Signing in with your Windows account…"*, and a toast along the lines
of `Added the Construct host buildbox.home.example — signed in as HOME\alice (VM quota: 2).
Create a VM on it with "The Construct: New VM on Remote Host".`

**What this does and does not do:** it records the host (URL, auth mode, pin, verified
identity) in the extension's `globalState`, which is what **The Construct: New VM on Remote
Host** picks from. It writes nothing to `instances.json` — the registry describes *VMs*, and
a host with no VM would show up in the picker as a machine you cannot reach.

**If the fingerprint prompt *does* appear**, the pin file was not shared as intended — note
it, compare the value against the SHA-256 fingerprint computed in step 1.3 and confirm.

**Note on token-auth instances:** this command only offers the token prompt after a `401`.
On a domain PC where Kerberos works it will never ask, so it cannot populate SecretStorage —
which is why 4.1 created `lab-vm` on `negotiate`. An instance recorded as
`service.auth: "token"` on such a PC would show `unknown` in the panel with no supported way
to fix it from the UI; record that if you hit it.

### 6.1 Switch

- [ ] Open VS Code on the client PC. **Expect** a status-bar item `$(vm) work-vm` on the
      left, and an instance dropdown at the top of the control panel — both appear only
      because there is now more than one instance.
- [ ] Run **The Construct: Switch Instance** from the command palette. **Expect** a
      QuickPick listing both VMs with their `host:port` and backend, the current one ticked.
- [ ] Switch to `lab-vm`. **Expect** the panel to re-probe and show *that* VM's hostname,
      versions, projects and usage — not the previous VM's numbers under the new name — and
      the System card to name the backend `hyperv-remote` and the host service.
- [ ] Reload the window. **Expect** it to come back on `lab-vm`: the choice is per window
      and persisted.
- [ ] Open a second VS Code window and switch it to `work-vm`. **Expect** two windows
      driving two VMs at once.

**If a switch does not take:** check whether `construct.instance` is set in VS Code settings
— it *pins* every window, and the panel warns that it does rather than silently ignoring
the switch.
**Where to look:** **The Construct: Show Logs** (also `%TEMP%\construct-panel.log`) records
each instance change and every registry problem.

---

## 7. Mic passthrough across a switch

- [ ] On `work-vm`, turn **Voice input** on in the panel. **Expect** the substatus to report
      the shim installed and the gate patched; reload the window if the chat mic button
      isn't there yet (the panel offers the button for exactly this).
- [ ] Switch the window to `lab-vm`. **Expect** the tunnel to `work-vm` to be torn down, and
      `lab-vm`'s own saved preference to be evaluated — if it is on there, passthrough
      re-arms against `lab-vm` **without you flipping anything**.
- [ ] Switch back. **Expect** the same in reverse, and no tunnel left behind on the VM you
      left.
- [ ] Switch A→B→C quickly if you have a third instance. **Expect** exactly one live tunnel
      at the end, on the last destination.

**If nothing arms:** startup and switch arming are deliberately *silent* — flip the console
switch by hand to see the actual error. A missing recorder (ffmpeg/sox) or no capture device
raises one honest warning rather than pretending to work.
**Where to look:** the Construct output channel logs each handover step.

---

## 8. `construct expose` — the client path (the one agents use)

Do this **on each VM** in turn.

Start the test server **detached**, not backgrounded under your SSH login — step 10 shuts the
client PC down, which takes that login's process group with it:

```bash
ssh work-vm
# on the VM (you are root); any listening server will do
apt-get install -y python3 tmux >/dev/null
tmux new -d -s fieldsrv 'python3 -m http.server 5173 --bind 0.0.0.0'
ss -ltn 'sport = :5173'        # verify it is actually listening before going on
construct expose 5173 --label "field test"
```

**Expect:** `ss` shows a `LISTEN` row on `0.0.0.0:5173`, and the `expose` command **blocks
briefly** and then prints one link —
`http://localhost:5173/` — and exits `0`. The block is the point: it waits for the
extension to acknowledge that the port is really open, so the link it prints is live.

- [ ] Open that URL **on the client PC**. **Expect** the VM's directory listing.
- [ ] Check the panel's **Forwards** card. **Expect** a row `vm:5173`, target `client`,
      label `field test`, state `open`, with a working **▷**.
- [ ] Now do the same on `lab-vm` with the same port `5173`. **Expect** the link to say a
      *different* local port (e.g. `http://localhost:18800/`) because 5173 is already taken
      on your PC — and the Forwards card to show `vm:5173→18800`. This is exactly why the
      CLI waits for the ack instead of printing the VM port.
- [ ] `construct expose --list` on each VM. **Expect** its own forwards, with id, port,
      target, status, label and URL.
- [ ] `construct expose --close 5173`. **Expect** the tunnel to go away and the row to
      disappear.

**Exit codes to expect deliberately:**

- Close VS Code entirely and run `construct expose 5173` again. **Expect** exit code **6**
  and a message that the request is queued — *not* an error. Re-open VS Code on that
  instance and **expect** the forward to open by itself, on the same port if it can, so an
  already-printed link keeps working.
- With **two windows on one VM**, **expect both** to list the forwards and **both** to be
  able to close them. These are remote VMs: the host service owns the forward state, so
  there is no spool claim and no "served by another window" row. (That single-owner
  behaviour is **local-mode only** — worth a separate check on a local VM if you have one;
  see [Control panel § Forwards](control-panel.md#forwards-construct-expose).)

**Where to look when it fails (remote mode).** These VMs talk to the service, not to a
guest spool — there is nothing under `/etc/construct/forwards` to read here:

- on the VM: `construct expose --list` (it reads the service);
- on the service: `GET /api/v1/vms/work-vm/forwards` as an admin, which shows each forward
  with its ack inline (`status`, `localPort`, `url`), and `GET /api/v1/audit` for the
  create/ack/delete calls;
- on the PC: the Construct output channel (**The Construct: Show Logs**) logs every planner
  action and every tunnel it spawns.

Exit `1` is a local error (bad port, unknown option), `7` is a refusal by the service, `8`
means the service could not be reached or answered something unusable.

---

## 9. `construct expose --to host` — the LAN path

Do this **on `work-vm`** specifically — step 10 checks that this exact forward survives your
PC being off, so it has to belong to the VM whose test server is still running from step 8:

```bash
ssh work-vm
construct expose 5173 --label "field test host" --to host
```

**Expect:** a URL on the **host's** address, e.g. `http://buildbox.home.example:2300/`, from
the configured app range (`2300-2999`). Open it **from a third machine** on the LAN — that is
the whole difference from step 8. **Write the port down**; step 10 uses it.

- [ ] On the host: `netsh interface portproxy show v4tov4`. **Expect** a rule
      `0.0.0.0:2300 → <work-vm's IPv4>:5173`, alongside `work-vm`'s own SSH forward
      (`0.0.0.0:2201 → <work-vm's IPv4>:22`).
- [ ] With the panel on `work-vm`, **expect** the Forwards card to show that row marked
      `host` rather than `client`.

### 9.1 (Optional) The per-user denial

The `--to host` gate is on the **VM owner's** `AllowHostForwards` flag, so testing it needs a
VM owned by a user who does not have it. There is no admin command that flips the flag on an
existing user, so this needs a second identity and a VM of its own — do it only if you want
the gate covered:

1. **On the host**, create the denied user and issue a token:

   ```powershell
   & $constructd admin users add HOME\bob --role User --max-vms 1 --no-host-forwards
   & $constructd admin tokens issue HOME\bob --label "bob field test"
   ```

2. **On a second client PC, or a second Windows account on this one**, sign in as that
   account and create bob's VM:

   ```powershell
   .\Auto-Install.ps1 -Backend hyperv-remote `
       -ServiceUrl https://buildbox.home.example:7462 -ServiceAuth token `
       -InstanceName deny-vm
   ```

   **Why a separate Windows account:** the DPAPI token store is one file per *host*
   (`<hostslug>.token`) per Windows user, so pasting bob's token in alice's session would
   overwrite hers.

   **Expect:** the fingerprint confirmation, bob's token accepted, and
   `Endpoint: buildbox.home.example:2203`.

3. **On `deny-vm`:**

   ```bash
   construct expose 5173 --to host
   ```

   **Expect:** exit code **7** and the service's own explanation that host forwards are not
   allowed for this VM's owner. `construct expose 5173` (the client default) still works.

4. **The gate reads the VM owner's flag, not the caller's** — so an *admin* asking for the
   same forward is refused too. Check it with the admin's own token, from anywhere:

   ```powershell
   curl.exe -sS -k -w "\nHTTP %{http_code}\n" `
     -H "Authorization: Bearer <the ADMIN token from step 1.3>" `
     -H "Content-Type: application/json" `
     -X POST https://buildbox.home.example:7462/api/v1/vms/deny-vm/forwards `
     -d '{"vmPort":5173,"label":"admin override attempt","target":"host"}'
   ```

   **Expect** the response body followed by `HTTP 403` — an RFC 7807 problem document saying
   host forwards are not allowed for that VM's owner, **not** a created forward.

   (`curl.exe` rather than `Invoke-WebRequest`: it prints a 4xx body instead of throwing, and
   it takes `-k` for the self-signed certificate on both PowerShell editions — see the note
   in step 0. The JSON is plain inside PowerShell's single quotes; escaping the inner quotes
   would send literal backslashes and earn a `400` instead of the `403` under test.)

**Where to look:** the Application event log on the host for netsh failures, and
`GET /api/v1/audit` (admin) for the refusal — every mutating call is audited whatever the
outcome, refusals included.

---

## 10. PC-independence — forwards with your PC off

This is the requirement the whole remote mode exists for.

- [ ] Leave the **host** forward from step 9 in place.
- [ ] Confirm the test server is still detached and listening — it must not be a process
      hanging off your SSH login, or shutting the PC down kills the *server*, not just the
      forward, and the test fails for the wrong reason:

      ```bash
      ssh work-vm "tmux ls; ss -ltn 'sport = :5173'"
      ```

      **Expect:** the `fieldsrv` session and a `LISTEN` row on `0.0.0.0:5173`.
- [ ] **Shut the client PC down** (not just VS Code).
- [ ] From a third machine, open `http://buildbox.home.example:2300/`.

**Expect:** it still works. The forward is a netsh rule the service owns and reconciles at
startup; nothing about it lives on your PC.

- [ ] `ssh -p 2201 root@buildbox.home.example hostname` from that third machine. **Expect**
      the VM's SSH forward to answer too.
- [ ] Reboot the **host**, then repeat both. **Expect** them to still answer: netsh rules
      survive reboots, and the service reconciles its store against them at startup —
      re-adding what is missing, re-pointing a rule whose VM changed address, and deleting
      rules inside its ranges that nothing accounts for.

**Do not expect `--to client` forwards to survive this.** They are opened *on your PC* by
VS Code and live exactly as long as it does. Turning the PC on and opening VS Code re-opens
whatever is still queued — that is the correct behaviour, and it is the reason both targets
exist.

**Where to look:** `& $constructd admin forwards reconcile` reports how many rules it
repaired, and refuses to claim success when it cannot read the rule list at all.

---

## 11. Idle policy — and the job that must not be killed

### 11.1 Set a policy

In the panel's **Idle policy** card for `work-vm`, set **5 minutes** → **save**, and press
**apply**.

**Expect:** the value to stick, and — if the admin configured a cap — the input's maximum to
carry it with a hint saying so. Equivalent by API:

```powershell
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -X PUT `
  -H "Authorization: Bearer <alice's token>" `
  -H "Content-Type: application/json" -d '{"timeoutMinutes":5,"action":"save"}' `
  https://buildbox.home.example:7462/api/v1/vms/work-vm/idle-policy
```

**Expect** `HTTP 200` and the stored policy echoed back, including the admin cap and whether
the request was clamped.

### 11.2 Let it idle out

- [ ] Close every SSH session and VS Code window on `work-vm` and wait out the timeout plus
      the grace window (the guest's report interval × the missing-report grace multiple).

**Expect:** the VM's state to become **`saved`** — RAM written to disk and freed — and the
panel's power button to read **▶ Resume & connect** rather than "Start & connect".

- [ ] Press it. **Expect** the VM to resume transparently and your session to come back.

**Where to look:** `GET /api/v1/audit` shows the `vm.idle-save` entry with actor `system`.
The scheduler evaluates once a minute.

### 11.3 The test that actually matters: an unattended agent

The reporter has four probes, and the workload has to trip one of the two that survive a
disconnect. **Pick a workload deliberately** — an anonymous CPU burner whose output is
discarded trips *nothing*: `sha256sum` is not one of the recognized agent commands, and with
its output redirected the tmux window's activity clock never advances.

The two probes to aim at:

| Reason | What actually fires it |
|---|---|
| `tmux-activity` | any tmux window whose **output** moved within the report interval (`#{window_activity}`) |
| `agent-cpu:<name>` | a process named `claude`, `codex`, `opencode` or `t3` — or a `node`/`bun`/`python3` whose **command line names one of those stacks** — or **any descendant** of one, whose CPU ticks grew since the previous run |

- [ ] On `work-vm`, start a workload that trips **both**, then disconnect completely — no SSH
      session, no VS Code, nothing.

The CPU probe compares **CPU ticks burned since the previous run** against
`CONSTRUCT_IDLE_CPU_TICKS` (default 10 ticks = 0.1 CPU-seconds). A workload that sleeps
between iterations can fall under that in a short sampling window, so burn CPU
**continuously** — that is also what a real agent job looks like:

```bash
ssh work-vm
apt-get install -y python3 tmux >/dev/null
# Two things at once: "claude" on the command line makes python3 count as an agent (the
# same rule that attributes a real agent's child processes), and the periodic print keeps
# the tmux window's activity clock moving. The hashing loop never sleeps, so it burns
# ~100 ticks per second — far above the 10-tick threshold in any sampling window.
tmux new -d -s fieldtest \
  'python3 -c "
import hashlib, time
block = b\"x\" * 65536
last = 0.0
while True:
    for _ in range(200):
        hashlib.sha256(block).hexdigest()
    now = time.time()
    if now - last >= 1.0:
        print(now, flush=True)
        last = now
" claude-field-test'
tmux ls          # confirm the session exists before you leave
exit
```

> This pins one core for as long as it runs. That is deliberate — it is the signal being
> tested — but do not leave it running on a shared host after step 11.

- [ ] Confirm the heartbeat sees it, **without contaminating the result**. Running the probe
      over SSH would create an established connection on port 22 and report `ssh-session`,
      which proves nothing about a disconnected job — so point the SSH probe at a port
      nothing uses:

```bash
ssh work-vm "CONSTRUCT_IDLE_SSH_PORT=9 CONSTRUCT_IDLE_DRY_RUN=1 /usr/local/bin/construct-idle-report.sh"
```

**Expect** (the JSON is printed, not posted):

```json
{"busy":true,"reasons":["agent-cpu:claude","tmux-activity"]}
```

`ssh-session` must **not** appear — that is what the port override is for. If `agent-cpu` is
missing on the very first run, run it once more: the CPU probe compares against the previous
sample (a process with no previous sample is judged on its total CPU, so it should appear
immediately).

- [ ] Now disconnect and **wait well past the idle timeout plus the grace window**.

**Expect:** the VM stays **`running`**. With zero connections, the only thing keeping it
alive is that heartbeat — and that is the entire point of unattended agents.

- [ ] Kill the workload and wait again:

```bash
ssh work-vm "tmux kill-session -t fieldtest"
```

**Expect:** the next dry run reports `{"busy":false,"reasons":[]}` (with the same
`CONSTRUCT_IDLE_SSH_PORT=9`), and after the timeout the VM's state becomes **`saved`**.

**If the VM is saved while the workload runs — stop and report it.** A false `busy` costs
some host RAM; a false idle kills someone's unattended work, and that asymmetry is the whole
design.
**Where to look:** `journalctl -u construct-idle-report -n 50` on the VM (a failed POST is
logged and retried on the next tick), and `GET /api/v1/audit` on the host.

---

## 12. Uninstall

### 12.1 The VMs

**There is no "delete this VM" item in `Auto-Install.ps1`'s menu** — it offers Reprovision,
Reinstall, Export config and Quit. Removing a VM is an API call (owner or admin), it is
asynchronous, and the interesting behaviour — the fence — only exists **while the job runs**.
The removal starts by hard-powering the VM off, so that window is short and partly a race.
Set the observation up **before** you submit the delete.

#### Prepare two terminals

**Terminal A — the guest side.** Open an SSH session to `work-vm` now and start a loop that
keeps asking the service something, so you are watching rather than typing when the moment
comes:

```bash
ssh work-vm
while :; do construct expose --list >/dev/null 2>&1; echo "exit=$?"; sleep 2; done
```

**Expect** a steady stream of `exit=0` while the VM is healthy.

**Terminal B — the owner side.** Set the variables and keep the commands below ready to
paste:

```powershell
$base = "https://buildbox.home.example:7462/api/v1"
$auth = "Authorization: Bearer <alice's token>"
```

#### 1. Submit the delete (terminal B)

`-D -` dumps the response headers, so the status line and the `Location` header are visible
alongside the body; the body is the last line, which is where the job id is:

```powershell
$out = curl.exe -sS -k --http1.1 -D - -H $auth -X DELETE "$base/vms/work-vm"
$out
$jobId = ($out | Select-Object -Last 1 | ConvertFrom-Json).jobId
$jobId
```

**Expect:** a status line `HTTP/1.1 202 Accepted`, a `Location:` header pointing at
`/api/v1/jobs/<id>`, a body of `{"jobId":"…"}`, and `$jobId` printed.

(`--http1.1` is there so the status line is predictable. The service does not pin a protocol
version, so without it curl may negotiate HTTP/2 and print `HTTP/2 202` with no reason
phrase — the same result, spelled differently. If you drop the flag, look for **202** in the
status line rather than matching the text.)

#### 2. Run the fence checks immediately (terminal B)

The removal job is running now. Paste these straight away:

```powershell
# a fenced mutation - power
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth -H "Content-Type: application/json" `
  -X POST "$base/vms/work-vm/power" -d '{"action":"start"}'

# a fenced mutation - a new forward
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth -H "Content-Type: application/json" `
  -X POST "$base/vms/work-vm/forwards" -d '{"vmPort":5173,"target":"client"}'

# a READ - body and status together
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth "$base/vms/work-vm"
```

**Expect:** `HTTP 409` from both mutations, each with a problem document saying the VM is
being deleted; and `HTTP 200` from the read, whose body carries `"deleting": true`.

Two more fenced routes, if the job is still running — both need no prerequisite:

```powershell
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth -H "Content-Type: application/json" `
  -X PUT "$base/vms/work-vm/idle-policy" -d '{"timeoutMinutes":10,"action":"save"}'

curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth -H "Content-Type: application/json" `
  -X POST "$base/vms/work-vm/activity" -d '{"busy":true,"reasons":["fence check"]}'
```

**Expect:** `HTTP 409` from both. The fifth fenced route, `DELETE
/vms/work-vm/forwards/{id}`, behaves the same way but needs a forward id captured **before**
the delete (you cannot create one now — that is the `409` you just saw); it is stated here
rather than asked for.

**If you get `HTTP 404` instead**, the job finished before you pasted. That is a timing
miss, not a failure: re-run the whole of 12.1 against `lab-vm` with the commands already on
the clipboard, or accept it and note that the fence window was not observed.

#### 3. Watch terminal A (best effort)

**Expect, if you catch it:** the loop's output turns from `exit=0` to **`exit=8`** — the
VM's scoped token was revoked in the same write that set the fence, so the call now fails at
*authentication* (`401`) and never reaches the fence at all. Shortly after, the SSH session
itself dies as the VM is powered off.

**This one is a genuine race and may not be observable.** The removal job powers the VM off
almost immediately, so the session can drop before a single `exit=8` is printed. Losing the
session without seeing it is **not** a failure of the fence — record what you saw. The
deterministic half of the guarantee is the `409`/`200` pair in step 2.

#### 4. Poll the job to its terminal state (terminal B)

```powershell
do {
    Start-Sleep -Seconds 3
    $j = (curl.exe -sS -k -H $auth "$base/jobs/$jobId" | ConvertFrom-Json)
    $j.state
} while ($j.state -eq "queued" -or $j.state -eq "running")
```

**Expect:** the state ends as **`succeeded`**, having removed the VM, its forwards and its
SSH port.

#### 5. Confirm it is gone

```powershell
curl.exe -sS -k -w "\nHTTP %{http_code}\n" -H $auth "$base/vms/work-vm"
```

**Expect:** `HTTP 404`. On the host, `netsh interface portproxy show v4tov4` no longer lists
`work-vm`'s SSH forward or its host forwards.

**A note on deleting twice.** `DELETE /vms/{name}` itself carries **no** fence check: a
second delete while the first removal job is still running is **accepted** and queues a
**second** removal job. Jobs are not serialized per VM, so the two run concurrently and their
completions can race — the second may fail on a VM the first has already removed. Record what
you see rather than treating either outcome as the expected one.

**If the job fails:** `curl.exe -sS -k -H $auth "$base/jobs/$jobId"` carries the reason and
the progress lines; the host's Application event log has the service's side.

#### 6. Clean the client PC by hand

Nothing does this for you:

```powershell
# 1. drop the entry from the registry (edit the JSON; keep the rest intact)
notepad "$env:LOCALAPPDATA\The-Construct\instances.json"
# 2. drop the Host block from ~\.ssh\config (the one whose alias is the instance name)
notepad "$env:USERPROFILE\.ssh\config"
# 3. drop the key
Remove-Item "$env:USERPROFILE\.ssh\construct_work-vm_ed25519"
```

**Expect** the instance to disappear from the panel's picker on the next window (the
registry is re-read), and `ssh work-vm` to stop resolving.

The config-sync branch `vm-work-vm` is deliberately left in place — a re-created instance of
the same name resumes from it, and a stale branch is never merged unless that instance syncs
again. Delete it if you want:

```powershell
git -C "$env:LOCALAPPDATA\The-Construct\config" branch -D vm-work-vm
```

Repeat for `lab-vm` (and `deny-vm`, if you ran 9.1).

### 12.2 The client's trust in the host

The PowerShell client's two files are yours to delete:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\The-Construct\remote"                  # find the slug
Remove-Item "$env:LOCALAPPDATA\The-Construct\remote\<hostslug>.token"   # forget the token
Remove-Item "$env:LOCALAPPDATA\The-Construct\remote\<hostslug>.pin"     # forget the pin
```

**Expect** the next console run against that host to **ask for the fingerprint again** —
there is no pin, and an unpinned host is refused until you re-enrol.

**Do not expect a credential prompt.** Deleting the token does not make the client ask for
one: `-ServiceAuth negotiate` (the default) tries this Windows session's identity first, and
in alice's domain session that still succeeds — so the run prints *"Authenticated with this
Windows session's identity."* and carries on. Only a session whose Negotiate attempt is
refused with a `401` sees the *API token / Domain account / Cancel* menu (that is what §4.3
tested). What the token deletion actually removes is the *stored bearer credential*: a run
with `-ServiceAuth token` now skips straight to Negotiate instead of using it, which is the
observable difference from §4.2.

> **Open limitation — the extension's enrolment cannot be cleared.** *Add Remote Host*
> (§6.0) writes the host — URL, auth mode, pinned fingerprint, verified identity — to the
> extension's `globalState`, and a token (when one was pasted) into VS Code
> **SecretStorage**. There is **no "remove remote host" command**, and nothing in the
> extension removes either on deactivation or uninstall. Deleting the two files above does
> not touch them: by design, neither store can read the other's.
>
> So there is **no verifiable cleanup result to check here** — the honest outcome to record
> is *"the extension still lists the host after the client-side uninstall."* Confirm it:
>
> - [ ] Run **The Construct: New VM on Remote Host**. **Expect** the host to still appear in
>       the pick list.
>
> A `Remove Remote Host` command is the obvious follow-up. Do not go digging in VS Code's
> storage by hand to work around it.

### 12.3 The service

```powershell
C:\Construct\service\host\Uninstall-ConstructHost.ps1 -RemovePortProxies -RemoveCertificate
```

**Expect:** the service stopped and deleted, the firewall rules removed, and — with those
switches — the portproxy rules and the certificate gone too.

Three things it leaves alone unless asked: the **data directory** (`-RemoveData`; it is the
record of who had what, and a reinstall expects to find it), the **port-proxy rules**, and
the **certificate**. It never talks to Hyper-V: deleting a colleague's VM is not an
uninstall step, so **remove the VMs first** (12.1).

**Expect** `-RemovePortProxies` to report the real number removed — its parser reads netsh's
rows by shape, so a localized host that prints `*` for the wildcard listen address does not
make it claim "removed 0" while leaving every rule behind.

---

## Appendix — where to look, by symptom

| Symptom | Look here |
|---|---|
| The service won't start, or a job fails on the host | `Get-EventLog -LogName Application -Source constructd -Newest 50`. The service never repeats a child process's text, so expect *its* wording plus the driver's streamed progress lines. |
| A create/delete job's own story | `GET /api/v1/jobs/{id}` (progress lines are replayed, so attaching late still shows the whole log) or `GET /api/v1/jobs/{id}/events` for the live stream. |
| "Who did what, when" — including refusals | `GET /api/v1/audit?limit=…` as an admin. Every mutating call writes exactly one entry whatever the outcome; heartbeats are mutations too, so page it. |
| A client PowerShell run | The console itself. `401` = credential, `known:false` = not enrolled, fingerprint mismatch = the certificate changed, `409` from `…/endpoint` = the forward isn't allocated yet, `403` on a VM that exists = it belongs to someone else. |
| The control panel | **The Construct: Show Logs** (the Construct output channel), also written to `%TEMP%\construct-panel.log`. Set `construct.debug` to keep launched consoles open with `-NoExit`. |
| Forwards, from the VM | `construct expose --list` — in remote mode it reads the service, so this is the VM's real view. |
| Forwards, on the service | `GET /api/v1/vms/{name}/forwards` (each forward with its ack inline: `status`, `localPort`, `url`) and `GET /api/v1/audit` for the create/ack/delete calls. |
| Forwards, on the host machine | `netsh interface portproxy show v4tov4`; `& $constructd admin forwards reconcile`. |
| Forwards on a **local** VM (not this checklist) | `/etc/construct/forwards/{requests,acks,close}` plus the extension's `.owner` / `.owner.lock`. Remote VMs use no spool at all — there is nothing there to read. |
| The idle heartbeat | `journalctl -u construct-idle-report -n 50` on the VM; `CONSTRUCT_IDLE_DRY_RUN=1 construct-idle-report.sh` to see the JSON without posting it. |
| The guest's view of itself | `/etc/construct/config.env` (`CONSTRUCT_EXTERNAL_HOST`, `CONSTRUCT_EXTERNAL_SSH_PORT`, `CONSTRUCT_SERVICE_URL`, `CONSTRUCT_INSTANCE_NAME`) and `/etc/construct/vm-token` (mode `0600`). |
| Provisioning | The console it ran in; on the VM, `journalctl -u construct`. |

## See also

- [Remote host](remote-host.md) — the design, the auth model, pinning, the idle policy.
- [`service/README.md`](../service/README.md) — the API, the admin CLI, the installer's
  parameters, and what the Windows implementations actually run.
- [`construct expose`](expose.md) — the forward contract and the heartbeat.
- [Control panel](control-panel.md) — the instance picker, the Forwards and Idle-policy
  cards.
