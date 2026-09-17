# A Construct host on Proxmox VE

> **Status: implemented and field-tested on a single Proxmox VE 9 node (2026-09-17).**
> The same `constructd` service that runs on a Windows Hyper-V host runs on the Proxmox node
> itself with the Proxmox platform selected (`Constructd:Backend = proxmox`). Every client is
> unchanged: `Auto-Install.ps1 -Backend hyperv-remote`, the VS Code extension and the Companion
> talk to the same API and see the same instance registry entries; the only difference they can
> observe is a shorter feature list (no child VMs, no screenshot console, no host self-update).
> The design background is [docs/remote-host.md](remote-host.md); this page is the Proxmox specifics.

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

On the node, as root, from a Construct checkout:

```sh
bash service/host/install-construct-host.sh --package ./constructd-linux-x64 --public-host 203.0.113.184
```

`--package` is a linux-x64 publish of the service (a directory or a zip):

```sh
dotnet publish service/src/Constructd.Api -c Release -r linux-x64 --self-contained true -o ./constructd-linux-x64
```

The script is the Linux counterpart of `Install-ConstructHost.ps1` and does, in order:

0. **Inputs** — root, `pvesh`/`qm`, a Construct checkout (`--source` defaults to the one the script is in), sane port ranges.
1. **Directories** — the layout above.
2. **Storage and network** — the disk storage must offer `images` (default `local-lvm`); the image storage must be a *directory* storage (default `local`) and gets `import` and `snippets` content enabled; the bridge (default `vmbr0`) must exist.
3. **Cloud image** — downloads `https://cloud-images.ubuntu.com/<release>/current/…` into the image storage through Proxmox's own `download-url`, checksum-verified against Ubuntu's `SHA256SUMS`. Skipped when already cached.
4. **TLS** — a self-signed certificate (10 years, SAN = public host and node name) as PFX; kept on re-runs.
5. **Files** — the service into `/opt/construct/host`, the checkout into `/opt/construct/scripts`.
6. **`appsettings.Production.json`** — root-only, since it carries the PFX password. Every value the Proxmox platform reads is in it (section 4).
7. **systemd** — `constructd.service`, enabled, running as root because `qm`/`pvesh` need it.
8. **First admin** — `admin users add <name> --role Admin --max-vms 10` and one API token, printed once. Re-runs keep the token; `--rotate-token` issues a new one.
9. **Start and verify** — restarts the unit and waits for `/api/v1/health`.

Re-running the script is the update path: pass a new `--package` and the service is replaced in
place (the existing VMs keep running; their SSH forwards are re-established from the database
when the service comes back).

Options: `--admin`, `--storage`, `--image-storage`, `--bridge`, `--release`, `--listen-port`,
`--ssh-ports a-b`, `--app-ports a-b`, `--skip-image`, `--rotate-token`.

## 3. Enrol from a PC

Exactly like a Windows host. Without a Kerberos keytab on the node (section 5b) there is no
Windows sign-in, so the token is mandatory:

```powershell
.\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://203.0.113.184:7462 -ServiceAuth token -InstanceName work-vm
```

The installer shows the certificate fingerprint (compare it with what the install script
printed), asks for the admin token once (it goes to the DPAPI store), then creates the VM through
the service and provisions it over SSH — the hybrid split of [docs/remote-host.md](remote-host.md).
More users: `Constructd.Api admin users add <name> --max-vms 2` and `admin tokens issue <name>` on
the node.

The backend id stays `hyperv-remote` on the client: it means "a VM on a host running constructd",
which is what this is. The registry entry, the SSH alias, VS Code Remote-SSH and the config-sync
branch are all as for a Windows host.

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
dial the **service host** on an allocated port (`203.0.113.184:2201` → guest `:22`), because that is
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
Three steps, all scripted:

1. **On a domain controller**, as a domain admin (needs RSAT's ActiveDirectory and DnsServer modules and `ktpass`):

   ```powershell
   .\service\host\New-ConstructKerberosPrincipal.ps1 -HostFqdn test-proxmox.corp.example.com -Address 203.0.113.184 -KeytabPath C:\constructd.keytab
   ```

   It creates `svc-constructd` (random never-expiring password, AES-256), adds the SPN, adds the
   A record in the AD DNS zone when the name does not resolve yet, and writes the keytab. Re-runs
   change nothing unless `-RotateKeytab` (which resets the password: install the new keytab).

2. **On the node**, copy the keytab over and re-run the installer with the host's DNS name as the
   public host (the SPN, the certificate SAN and the URL clients type must all be that name):

   ```sh
   bash service/host/install-construct-host.sh --public-host test-proxmox.corp.example.com \
        --keytab /root/constructd.keytab --netbios-domain HOME --realm CORP.EXAMPLE.COM
   ```

   The keytab goes to `/etc/constructd/krb5.keytab` (root-only), the unit gets `KRB5_KTNAME`, a
   minimal `/etc/krb5.conf` is written when none exists (KDCs from DNS), and the settings gain
   `Negotiate: { Enabled, DomainName, Realm }`. Later re-runs keep all of it without the flags.

3. **On the node**, add people by their domain name, then they enrol without a token:

   ```sh
   /opt/construct/host/Constructd.Api admin users add 'HOME\alice' --max-vms 3
   ```

   ```powershell
   .\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://test-proxmox.corp.example.com:7462 -InstanceName work-vm
   ```

Kerberos names the user `alice@CORP.EXAMPLE.COM`; the service maps that onto `HOME\alice`
(`Negotiate:DomainName`, optionally restricted to `Negotiate:Realm`) so the same user record
serves a Windows host and this one. Tokens keep working alongside. If the node's address changes,
update the A record; the SPN and keytab are name-based and stay valid.

## 6. What is not there (yet)

- **Child VMs** (`construct vm …`), the **screenshot console** and the **host self-update** report
  `unsupported-capability`; the health endpoint does not list them, so the extension does not offer them.
- **Capacity enforcement** — the ledger observes (`HostAdmin:Capacity:Mode = Observe`) with a real
  inventory (`pvesh get /nodes/<node>/status|storage|qemu`), but nothing is refused for capacity.
- **NTLM fallback** — the Linux Negotiate handler speaks Kerberos; a PC that cannot get a ticket for
  the host's SPN (wrong URL name, no domain reachability) falls back to a token prompt.
- **Cluster** — one node; `Constructd:Proxmox:Node` names it, and VMs of the same name on other
  nodes are not this host's.
- **A Linux release asset** — the release workflow publishes Windows host packages; the Linux
  service is published from the checkout for now (section 2).

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

## 8. Troubleshooting

- `journalctl -u constructd -f` — the service log. Driver failures name the operation and the VM;
  the job's progress log (`GET /jobs/{id}`) carries `qm`'s own words.
- `qm list` / `qm config <id>` / `qm agent <id> network-get-interfaces` — what the node sees.
- A VM stuck without an address: the guest agent is installed by cloud-init at first boot, which
  needs the guest to reach the Ubuntu archives. `qm terminal <id>` shows the serial console.
- `ss -ltnp | grep Constructd` — the relay listeners (one per SSH forward and host app forward).
- Removing a VM by hand: `DELETE /vms/{name}` through the API (or the extension); a `qm destroy`
  behind the service's back leaves a registry record that `GET /vms/{name}/state` reports as `absent`.
