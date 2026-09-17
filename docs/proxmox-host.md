# A Construct host on Proxmox VE

> **Status: implemented and field-tested on a single Proxmox VE 9 node (2026-09-17).**
> The same `constructd` service that runs on a Windows Hyper-V host runs on the Proxmox node
> itself with the Proxmox platform selected (`Constructd:Backend = proxmox`). Every client is
> unchanged: `Auto-Install.ps1 -Backend hyperv-remote`, the VS Code extension and the Companion
> talk to the same API and see the same instance registry entries; the only difference they can
> observe is a shorter feature list (no child VMs or screenshot console).
> Host self-update is implemented; its systemd handoff and rollback still need human field testing.
> The design background is [docs/remote-host.md](remote-host.md); this page is the Proxmox specifics.

## Quick start (four steps)

1. **Install Proxmox VE** on the machine (the stock installer, any storage layout; one bridge with
   DHCP on your LAN is all the network it needs). Make sure the node has a DNS name in your domain
   or add one in step 3.
2. **On the node**, as root, one line: it fetches the latest release, caches the Ubuntu cloud image,
   issues a certificate, installs and starts the service, and prints an admin token.

   ```sh
   curl -fsSL https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/service/host/install-construct-host.sh | bash
   ```

   Done already if tokens are enough for you: enrol from a PC with the printed command
   (`-ServiceAuth token`) and skip step 3.
3. **Windows sign-in** (optional; skip it if tokens are enough). Two ways to the same result, the
   choice is yours:

   - **Scripted** (below): one PowerShell script run on the domain controller does everything.
   - **Manual** ([§5b, "The manual way"](#the-manual-way)): you create the service account, the SPN,
     the DNS record and the keytab yourself, one documented command at a time, and never run a
     foreign script against your directory. Then continue with the `users add` line at the end of
     this step.

   **Scripted:** on the domain controller, as a domain admin in an elevated PowerShell: it creates
   the service account, its SPN and the DNS record, writes the keytab, copies it to the node and
   finishes the host over SSH (asks for the node's root password).

   ```powershell
   Invoke-WebRequest https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/service/host/New-ConstructKerberosPrincipal.ps1 -OutFile .\New-ConstructKerberosPrincipal.ps1
   .\New-ConstructKerberosPrincipal.ps1 -HostFqdn pve1.corp.example.com -Address 192.0.2.10 -InstallOnHost root@pve1.corp.example.com
   ```

   **Either way**, then add the people who may use it, by domain name:

   ```sh
   ssh root@pve1.corp.example.com /opt/construct/host/Constructd.Api admin users add 'CORP\alice' --max-vms 3
   ```
4. **On a PC** with The Construct installed, the normal remote command, with your Windows account:

   ```powershell
   .\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://pve1.corp.example.com:7462 -InstanceName work-vm
   ```

   Confirm the certificate fingerprint the node printed in step 2. Without step 3 add
   `-ServiceAuth token` and paste the admin token when asked.

To update a host that supports self-update, open the extension's **Maintenance** tab and click
**Update**. See Updating below. To install the first supporting build or repair an installation,
repeat step 2 with `--host-release latest`. To try an unreleased branch, add `--ref <branch>`;
the installer builds the service on the node and fetches a .NET SDK once.

## 1. What the node ends up running

| Piece | Where | What it does |
|---|---|---|
| `constructd` (systemd unit `constructd.service`, root) | `/opt/construct/host` | The host service on `https://<node>:7462`: users, tokens, VM registry, jobs, idle policy, audit — the API in [service/README.md](../service/README.md) |
| The Construct checkout | `/opt/construct/scripts` | `bin/`, `keys/`, `config/` … what guests are provisioned from and served through the source cache |
| Data | `/var/lib/constructd` | `constructd.db` (SQLite), `iso/`, `media/`, `source/` |
| TLS | `/etc/constructd/tls.pfx` (+ `tls.pass`) | A self-signed certificate for the public host; clients pin its fingerprint at enrolment |
| The VM image | `<image-storage>:import/construct-ubuntu-<release>-cloudimg-amd64.qcow2` | The Ubuntu cloud image every VM is cloned from |
| Per-VM seeds | `<image-storage>:snippets/construct-<vm>-user.yaml` | cloud-init user data the service writes per VM and deletes with it |

Guests are ordinary QEMU VMs on the node: `qm config <id>` shows them, the Proxmox UI lists them
(tagged `construct`), and nothing about them is special except that `constructd` owns their
lifecycle. **Do not rename or renumber them behind the service's back** — it addresses them by name.

## 2. Install (or update) the host

The one-liner of the quick start is `service/host/install-construct-host.sh`, the Linux counterpart
of `Install-ConstructHost.ps1`. Release packages supply their matching scripts unless `--source`
explicitly selects a checkout. The source checkout supplies bootstrap keys on a fresh install;
existing keys are preserved when installing release scripts. Run bare, the installer fetches
the release's pinned source itself. The service binary comes from the release's
`construct-host-<commit>-linux-x64.zip` (checksum-verified against the manifest), from `--package`
(a `dotnet publish -r linux-x64 --self-contained true` output, directory or zip), or is built on the
node with `--build` / for a non-main `--ref`. In order it does:

0. **Inputs** — root, `pvesh`/`qm`, the small tools it needs (installed if missing), sane port ranges, the public host (the node's FQDN when it resolves, else its address).
1. **Directories** — the layout above.
2. **Storage and network** — the disk storage must offer `images` (default `local-lvm`); the image storage must be a *directory* storage (default `local`) and gets `import` and `snippets` content enabled; the bridge (default `vmbr0`) must exist.
3. **Cloud image** — downloads `https://cloud-images.ubuntu.com/<release>/current/…` into the image storage through Proxmox's own `download-url`, checksum-verified against Ubuntu's `SHA256SUMS`. Skipped when already cached.
4. **TLS** — a self-signed certificate (10 years, SAN = public host and node name) as PFX; kept on re-runs.
5. **Files** — the service into `/opt/construct/host`, the checkout into `/opt/construct/scripts`.
6. **`appsettings.Production.json`** — root-only, since it carries the PFX password. Every value the Proxmox platform reads is in it (section 4).
7. **systemd** — `constructd.service`, enabled, running as root because `qm`/`pvesh` need it. Sleep, suspend and hibernate are masked and logind ignores the lid, the suspend keys and idleness, so a laptop node stays up under its guests (the Windows installer's `powercfg` step).
8. **First admin** — `admin users add <name> --role Admin --max-vms 10` and one API token, printed once. Re-runs keep the token; `--rotate-token` issues a new one.
9. **Start and verify** — restarts the unit and waits for `/api/v1/health`.

Re-running the script is the install/repair path: `--host-release latest` (or a new `--package`, or
`--build`) replaces the service in place; the existing VMs keep running and their SSH forwards are
re-established from the database when the service comes back. Without such a flag a re-run keeps
the installed service and only refreshes scripts and settings.

Options: `--admin`, `--storage`, `--image-storage`, `--bridge`, `--release`, `--listen-port`,
`--ssh-ports a-b`, `--app-ports a-b`, `--skip-image`, `--rotate-token`, `--repo`, `--ref`,
`--host-release`, `--build`, `--package`, `--source`, and the Kerberos trio `--keytab`,
`--netbios-domain`, `--realm` (section 5b).

## 3. Enrol from a PC

Exactly like a Windows host. Without a Kerberos keytab on the node (section 5b) there is no
Windows sign-in, so the token is mandatory:

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://192.0.2.10:7462 -ServiceAuth token -InstanceName work-vm
```

The installer shows the certificate fingerprint (compare it with what the install script
printed), asks for the admin token once (it goes to the DPAPI store), then creates the VM through
the service and provisions it over SSH — the hybrid split of [docs/remote-host.md](remote-host.md).
More users: `Constructd.Api admin users add <name> --max-vms 2` and `admin tokens issue <name>` on
the node.

The backend id stays `hyperv-remote` on the client: it means "a VM on a host running constructd",
which is what this is. The registry entry, the SSH alias, VS Code Remote-SSH and the config-sync
branch are all as for a Windows host.

### Updating

In the extension's host administration panel, open **Maintenance** and click **Update**.
The service verifies the Linux release, drains active jobs, then launches
`construct-host-update-<updateId>` through `systemd-run`. That unit survives the service stop.
It backs up owned files and the database, replaces the service and matching scripts, restarts
`constructd`, and checks the pinned loopback health response and `admin db check --json`.
Failed health checks roll back; failed recovery stays in maintenance for an administrator.
The certificate pin comes from the configured `CertPath`/`CertPassword` PFX.

Logs are `/var/lib/constructd/updates/updater.log` and
`journalctl -u construct-host-update-<updateId>`. Recovery state is in the same directory's
`last-update.json`; `handoff.json` contains a private health credential and must not be shared.
The installer writes `/opt/construct/host/install.json` with hashes of owned files, so the
first self-update has a file ledger. Releases without the Linux metadata report `no-linux-asset`.
An installation without a ledger needs an installer repair before its first self-update;
the Linux updater refuses to infer ownership by scanning unknown host files.
Raw local publishes without release metadata receive an `unknown` ledger version/commit;
the running binary still reports its compiled commit. A build from a checkout records its commit.

Use `install-construct-host.sh --host-release latest` for the first supporting build or for
repair. It accepts both older service-only archives and the new release layout. `--source`
keeps explicit checkout scripts; otherwise a release package supplies its own scripts. Settings,
certificate and service registration remain installer responsibilities. Self-update preserves them.

## 4. How a VM comes to be

A create request (`POST /vms`) runs the same job as on Windows; only the platform steps differ:

| Step | Windows host | Proxmox host |
|---|---|---|
| Install media | autoinstall ISO from the catalog (or per VM through WSL) | **one cloud-init snippet per VM**: hostname, seed user `construct` with passwordless sudo and a locked password, the bootstrap public key, `qemu-guest-agent` |
| Create | `Create-AgentVM.ps1` → Gen-2 VM, fresh VHDX, ISO attached | `qm create` cloning the cached cloud image (`--scsi0 <storage>:0,import-from=<image>`), a cloud-init drive (`--ide2 <storage>:cloudinit`), `--cicustom user=<snippet>`, `--agent enabled=1`, `cpu host`, bridged DHCP; then `qm disk resize` to the requested size and `qm start` |
| Wait for SSH | the driver's socket poll on `<name>.mshome.net` | the guest agent reports the DHCP address (`network-get-interfaces`); SSH is probed on it |
| Detach media | eject the ISO | nothing (the cloud-init drive is inert after first boot) |
| Endpoint | `PublicHost:<forward>` via `netsh portproxy` | `PublicHost:<forward>` via an **in-process TCP relay** (section 5) |
| Power | `Start-VM` / `Stop-VM` / `Save-VM` | `qm start` / `qm shutdown --forceStop 1` / `qm suspend --todisk 1` |
| Remove | `Remove-VM` + disk chain | `qm stop` (if running) + `qm destroy --purge 1 --destroy-unreferenced-disks 1` + the seed snippet |

The whole thing takes about two and a half minutes on an old laptop: the image import is most of
it, first boot plus `apt-get install qemu-guest-agent` the rest. The client then provisions the
guest with `bin/provision.sh` exactly as it would any other Construct VM — the guest payload is
identical.

Guests have `cpu: host`, so nested virtualization is available when the node's `kvm_intel`/`kvm_amd`
module has `nested=1` (the default on Proxmox 9).

## 5. Networking

Guests take a DHCP lease on the node's bridge, so they are ordinary LAN machines. Clients still
dial the **service host** on an allocated port (`192.0.2.10:2201` → guest `:22`), because that is
what the API contract promises and what keeps a PC's SSH config valid across a guest's lease
changes: the relay looks the guest's current address up through the guest agent when a connection
arrives (cached for a minute), so a rebooted guest with a new address is reached without anyone
editing anything.

The relay is the service's own listener, not a kernel forward: no `iptables`/`nftables` rules, no
`ip_forward`, no masquerading, and the idle policy's "no client connections" signal is the relay's
own live connection count. `construct expose --to host` forwards are relayed the same way.

Ports: the API on `7462`, SSH forwards `2201-2299`, app forwards `2300-2999` by default. Proxmox's
own firewall is off by default; if you enable it, allow those on the node.

## 5b. Windows sign-in (Kerberos)

A Windows host accepts your domain account because the service runs as a domain identity. A Linux
host can do the same once it owns a Kerberos identity of its own: a service account in Active
Directory with the service principal name `HTTP/<host FQDN>`, and a keytab for it on the node.

There are two ways to get there. The **scripted way** is one command on a domain controller and is
what a home lab or a small team wants. The **manual way** lists every change the script would make,
one command at a time with what it does, for a domain admin who will not run somebody else's
PowerShell against the directory. Both end in the same state; pick one.

Whichever way: the SPN, the certificate name and the URL clients type must all be the **same DNS
name** (`pve1.corp.example.com` below). Kerberos tickets are issued for a name, never for an
address.

### The scripted way

1. **On a domain controller**, as a domain admin (needs RSAT's ActiveDirectory and DnsServer modules and `ktpass`):

   ```powershell
   .\service\host\New-ConstructKerberosPrincipal.ps1 -HostFqdn pve1.corp.example.com -Address 192.0.2.10 -InstallOnHost root@pve1.corp.example.com
   ```

   It creates `svc-constructd` (random never-expiring password set by `ktpass`, AES-256, UPN equal
   to the principal so the key salt matches), adds the SPN, adds the A record in the AD DNS zone
   when the name does not resolve yet, writes the keytab, and with `-InstallOnHost` copies it to
   the node and runs step 2 there over SSH. Re-runs change nothing unless `-RotateKeytab` (which
   resets the password: install the new keytab). Without `-InstallOnHost` the keytab is left at
   `-KeytabPath` for you to carry over.

2. **On the node** (what `-InstallOnHost` does for you), with the host's DNS name as the public
   host:

   ```sh
   bash service/host/install-construct-host.sh --public-host pve1.corp.example.com \
        --keytab /root/constructd.keytab --netbios-domain CORP --realm CORP.EXAMPLE.COM
   ```

   The keytab goes to `/etc/constructd/krb5.keytab` (root-only), the unit gets `KRB5_KTNAME`, a
   minimal `/etc/krb5.conf` is written when none exists (KDCs from DNS), and the settings gain
   `Negotiate: { Enabled, DomainName, Realm }`. Later re-runs keep all of it without the flags.

3. **On the node**, add people by their domain name, then they enrol without a token:

   ```sh
   /opt/construct/host/Constructd.Api admin users add 'CORP\alice' --max-vms 3
   ```

   ```powershell
   .\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://pve1.corp.example.com:7462 -InstanceName work-vm
   ```

### The manual way

Everything below is what `New-ConstructKerberosPrincipal.ps1` does, as plain commands. Run the
Windows ones on a domain controller (or any machine with RSAT) as a domain admin; names are the
example values, replace them with yours. Nothing here touches the node until step 6.

| Value | Example | Where it is used |
|---|---|---|
| Host FQDN | `pve1.corp.example.com` | DNS record, SPN, certificate, client URL |
| Realm (the AD domain, upper case) | `CORP.EXAMPLE.COM` | principal name, `krb5.conf` |
| NetBIOS domain | `CORP` | how the service names users (`CORP\alice`) |
| Service account | `svc-constructd` | owns the SPN and the keytab |
| Principal | `HTTP/pve1.corp.example.com@CORP.EXAMPLE.COM` | the identity in the keytab |

1. **DNS: make the name resolve to the node.** Skip this when your DNS lives elsewhere (a
   DHCP-registered name, an external DNS appliance); only the result matters. In the AD-integrated
   zone:

   ```powershell
   Add-DnsServerResourceRecordA -ZoneName corp.example.com -Name pve1 -IPv4Address 192.0.2.10 -TimeToLive (New-TimeSpan -Hours 1)
   Resolve-DnsName pve1.corp.example.com
   ```

   Do **not** use a CNAME: the Windows Kerberos client requests a ticket for the canonical name it
   resolves, and that name must be the SPN. A short TTL makes a later address change cheap; the SPN
   and the keytab are name-based and survive it.

2. **Create the service account.** An ordinary user object, no group memberships, no logon rights
   needed: it exists only to hold a key. The Kerberos encryption type must be AES-256 (RC4 is
   refused by the Linux side), the password must never expire (a rotated password invalidates the
   keytab), and the account must be enabled (a disabled account makes the KDC answer as if the SPN
   did not exist).

   ```powershell
   New-ADUser -Name svc-constructd -SamAccountName svc-constructd -Description 'Construct host (Kerberos service identity)' -Enabled $true -PasswordNeverExpires $true -CannotChangePassword $true -AccountPassword (Read-Host -AsSecureString 'temporary password')
   Set-ADUser -Identity svc-constructd -KerberosEncryptionType AES256
   ```

   The temporary password is replaced in step 5. If your policy forbids `PasswordNeverExpires`,
   plan for a keytab rotation (steps 5 and 6) before every expiry instead.

3. **Register the SPN.** The service principal name is what a client asks the KDC for when it
   opens `https://pve1.corp.example.com`. It must exist exactly once in the forest.

   ```powershell
   setspn -Q HTTP/pve1.corp.example.com          # must find nothing
   setspn -S HTTP/pve1.corp.example.com svc-constructd
   ```

   `-S` refuses a duplicate; if `-Q` finds the SPN on another object (a decommissioned server, a
   computer account), remove it there with `setspn -D` first.

4. **Set the UPN to the principal.** This is the one non-obvious step. For AES keys the key is
   derived from the password *and a salt*, and Active Directory salts a user account with
   `<REALM><userPrincipalName without realm>`. The keytab written in step 5 and the key the KDC
   holds must use the same salt, so the UPN is set to the principal before the password is set:

   ```powershell
   Set-ADUser -Identity svc-constructd -UserPrincipalName 'HTTP/pve1.corp.example.com@CORP.EXAMPLE.COM'
   ```

   (`ktpass +setupn` below does the same thing; setting it explicitly first makes the state visible
   in `Get-ADUser`.)

5. **Write the keytab.** `ktpass` sets the account password and writes a keytab containing the
   AES-256 key derived from it. `/pass +rndPass` lets it generate the password, which nobody needs
   to know; use `/pass *` to be prompted instead. `/mapop set` replaces (rather than adds to) the
   account's SPN mapping, `/ptype KRB5_NT_PRINCIPAL` is the principal type Linux expects, and
   `+DumpSalt` prints the salt so you can check it matches step 4.

   ```powershell
   ktpass /princ HTTP/pve1.corp.example.com@CORP.EXAMPLE.COM /mapuser CORP\svc-constructd /crypto AES256-SHA1 /ptype KRB5_NT_PRINCIPAL /pass +rndPass /mapop set +setupn +DumpSalt /out C:\temp\constructd.keytab
   Get-ADUser svc-constructd -Properties msDS-KeyVersionNumber, ServicePrincipalNames, UserPrincipalName, Enabled, KerberosEncryptionType
   ```

   `ktpass` reports progress on stderr, which PowerShell may colour red; the exit code decides.
   Note the key version number (`msDS-KeyVersionNumber`): the keytab carries the same `kvno`, and
   the two drift apart if the password is ever changed by other means. Treat the keytab like the
   account's password: it **is** the account's key. Carry it over an authenticated channel and
   delete the copy on the controller afterwards.

   Re-running `ktpass` later (a rotation) sets a new password and bumps the kvno; install the new
   keytab on the node right after, the old one stops working at that moment.

6. **Install the keytab on the node.** Copy it over and run the installer with the Kerberos
   flags once; it stores the keytab root-only at `/etc/constructd/krb5.keytab`, remembers realm and
   domain in `/etc/constructd/kerberos.env` for later runs, and turns on the `Negotiate` settings
   (`service/README.md`, rows `Negotiate:*`).

   ```sh
   scp C:\temp\constructd.keytab root@pve1.corp.example.com:/root/constructd.keytab
   bash service/host/install-construct-host.sh --public-host pve1.corp.example.com \
        --keytab /root/constructd.keytab --netbios-domain CORP --realm CORP.EXAMPLE.COM
   shred -u /root/constructd.keytab
   ```

   The node needs `/etc/krb5.conf` naming the realm; the installer writes a minimal one (KDCs
   found through DNS SRV records) only when none exists. Without the installer, the same state is:
   the keytab at a root-only path, `Environment=KRB5_KTNAME=<path>` in `constructd.service`, and
   `Constructd:Negotiate:{Enabled:true, DomainName:"CORP", Realm:"CORP.EXAMPLE.COM"}` in
   `appsettings.Production.json`.

7. **The TLS certificate.** The installer issues a self-signed certificate for the public host
   (SAN = the FQDN) and clients pin its SHA-256 fingerprint at enrolment; Kerberos does not depend
   on it. To use a certificate from your own CA instead, place it before running the installer and
   it is kept as long as its SAN names the public host:

   ```sh
   # /etc/constructd/tls.pfx  (key + chain, PKCS#12), /etc/constructd/tls.pass (its password, one line),
   # /etc/constructd/tls.crt  (the leaf certificate, PEM) -- all 0600 root
   ```

   Clients then still pin the fingerprint (`Auto-Install.ps1` shows it once); a CA-trusted chain
   just spares them the confirmation prompt.

8. **Verify** from a domain-joined PC, in the user's own session (not an elevated or system one:
   those fall back to NTLM, which the Linux side rejects on purpose):

   ```powershell
   klist get HTTP/pve1.corp.example.com      # a ticket for the SPN; 0xc000018b = SPN not found or account disabled
   curl.exe --negotiate -u : https://pve1.corp.example.com:7462/api/v1/whoami
   ```

   `whoami` answers for any authenticated identity, enrolled or not: `name` is `CORP\<user>`,
   `scheme` is `Negotiate`, and `known` turns `true` once the user is added on the host (step 9).
   A `name` of the right shape with `known: false` already proves the keytab. On the node,
   `journalctl -u constructd` shows the negotiated principal, and `klist -k /etc/constructd/krb5.keytab`
   lists the key with its kvno.

9. **Add users** by domain name and let them enrol, exactly as in step 3 of the scripted way.

Kerberos names the user `alice@CORP.EXAMPLE.COM`; the service maps that onto `CORP\alice`
(`Negotiate:DomainName`, optionally restricted to `Negotiate:Realm`) so the same user record
serves a Windows host and this one. Tokens keep working alongside. If the node's address changes,
update the A record; the SPN and keytab are name-based and stay valid.

## 6. What is not there (yet)

- **Child VMs** (`construct vm …`) and the **screenshot console** report
  `unsupported-capability`; the health endpoint does not list them, so the extension does not offer them.
- **Capacity enforcement** — the ledger observes (`HostAdmin:Capacity:Mode = Observe`) with a real
  inventory (`pvesh get /nodes/<node>/status|storage|qemu`), but nothing is refused for capacity.
- **NTLM fallback** — the Linux Negotiate handler speaks Kerberos; a PC that cannot get a ticket for
  the host's SPN (wrong URL name, no domain reachability) falls back to a token prompt.
- **Cluster** — one node; `Constructd:Proxmox:Node` names it, and VMs of the same name on other
  nodes are not this host's.

## 7. Settings reference (`Constructd:Proxmox`)

| Key | Default | Meaning |
|---|---|---|
| `Backend` | `hyperv` | `proxmox` selects this platform |
| `Proxmox:Node` | this machine's host name | the node addressed |
| `Proxmox:Storage` | `local-lvm` | VM disks and cloud-init drives (`images` content) |
| `Proxmox:ImageVolume` | `local:import/construct-ubuntu-noble-cloudimg-amd64.qcow2` | the cached cloud image |
| `Proxmox:SnippetStorage` / `SnippetDir` | `local` / `/var/lib/vz/snippets` | where per-VM seeds go |
| `Proxmox:Bridge` | `vmbr0` | guest network |
| `Proxmox:CpuType` | `host` | QEMU CPU type |
| `Proxmox:QmPath` / `PveshPath` | `qm` / `pvesh` | the commands |

Everything else (`PublicHost`, port ranges, idle policy, `Iso:SeedUser`, `Iso:BootstrapPublicKeyPath`,
persistence) is the common configuration documented in [service/README.md](../service/README.md).

RAM headroom defaults to `max(1 GiB, total RAM / 8)` on Proxmox. Hyper-V uses
`max(4 GiB, total RAM / 8)`. The stored host setting `capacity.ramHeadroomBytes`
overrides either default, including when set to zero.

The host administration Overview shows measured RAM usage, split into running VM
residency and host usage, with physical free RAM left empty. It also shows swap when
configured. VM commitments are a separate number that can exceed 100% and turn hot;
they do not fill the usage bar. Proxmox defaults to Observe mode, so the card says
"admission not enforced (observe mode)". The headroom marker appears only in Enforce
mode. Changing the headroom default does not enable capacity enforcement.

The `memoryPressure` host policy saves idle VMs to disk when measured RAM use exceeds 90%
or swap use exceeds 50%, even in Observe mode. It continues until RAM falls below 80% and
swap is no longer above its threshold. Users resume saved VMs; the policy never resumes or
shuts them down. A VM with a connection, a fresh busy heartbeat, provisioning or a running
job is never touched, and an effective idle policy of Off or timeout 0 exempts it.
The closest idle timeout goes first, with larger guest resident `mem` breaking ties.
The service waits at least 60 seconds and requires a new measurement between saves;
recently started or saved VMs get a 10-minute cooldown. Admins can adjust or disable the
policy in Host Administration's Configuration tab. The RAM card shows pressure and the
last save, the VM list shows "saved (memory pressure)", and each attempt is audited as
`vm.pressure-save`. If every VM is busy, the host stays under pressure and the panel says
there are insufficient idle VMs. This policy does not change ballooning or KSM settings.

## 8. Troubleshooting

- `journalctl -u constructd -f` — the service log. Driver failures name the operation and the VM;
  the job's progress log (`GET /jobs/{id}`) carries `qm`'s own words.
- `qm list` / `qm config <id>` / `qm agent <id> network-get-interfaces` — what the node sees.
- A VM stuck without an address: the guest agent is installed by cloud-init at first boot, which
  needs the guest to reach the Ubuntu archives. `qm terminal <id>` shows the serial console.
- `ss -ltnp | grep Constructd` — the relay listeners (one per SSH forward and host app forward).
- Removing a VM by hand: `DELETE /vms/{name}` through the API (or the extension); a `qm destroy`
  behind the service's back leaves a registry record that `GET /vms/{name}/state` reports as `absent`.
