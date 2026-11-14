# HTTPS forwards — links with the host's own certificate

A host forward (`construct expose <port> --to host`) publishes a VM port as plain TCP. Anything
served over it either has no TLS or uses the VM's own local CA, which your PC trusts after
provisioning but phones and other machines do not. An **HTTPS forward** fixes that without
putting a certificate's private key into a VM: the **host** holds the certificate, terminates TLS
on the forward's public port and relays the decrypted stream to the VM port.

```
browser ──TLS (host certificate)──▶ constructd :2310 ──plain or TLS, as the forward says──▶ VM :5178
```

The relay is byte-for-byte after the handshake, so WebSockets, streaming responses and uploads of
any size pass unchanged, and the `Host` header reaches the guest exactly as the browser sent it
(T3 Code compares it with the `Origin`).

Nothing of this is on by default. A host offers HTTPS forwards only after an admin has set a
certificate **and** allowed a user to use them. Until then the guest CLI does not even show the
option.

## 1. Admin: set it up

### The certificate

One certificate chain with its private key, usually a wildcard for the names VMs are advertised
under (`*.vms.example` together with `Constructd:PublicHostPattern = {name}.vms.example`, see
[remote-host.md](remote-host.md), "Per-VM public host names"). Without a pattern every VM is
advertised on `PublicHost`, and the certificate has to cover that name.

Set it in any of three ways. They all write the same store:

* **Host administration UI** (Companion or the VS Code panel), Configuration tab, "HTTPS
  certificate": pick a PEM chain and its PEM key, or a PFX and its password.
* **API**: `PUT /api/v1/host/https/certificate` with `{"certificatePem": "...", "privateKeyPem": "..."}`
  or `{"pfx": "<base64>", "password": "..."}`. `GET /api/v1/host/https` shows what is installed,
  which names it covers, the listeners and any warnings. `DELETE` removes it.
* **On the host itself**, with no API token:

  ```bash
  constructd admin https import --cert fullchain.pem --key privkey.pem
  constructd admin https import --pfx cert.pfx --password-file pfx.pass
  constructd admin https status
  constructd admin https remove
  ```

A pair is checked before anything is replaced: the key must belong to a certificate in the chain,
the certificate must not have expired, and the chain is stored leaf first whatever order it came
in. The files live in `Constructd:Https:CertificateDirectory` (default: `https/` next to the
database), readable by the service account only. The key never leaves the host and is never part
of an API answer.

**Renewal needs no restart.** The service notices new files within a few seconds of the next
connection, whichever process wrote them. A renewal job therefore only has to run
`constructd admin https import` on the host, for example as the forced command of an SSH key that
the machine issuing the certificate uses to push it:

```bash
# authorized_keys on the host:
# command="/usr/local/sbin/receive-cert",no-pty,no-port-forwarding ssh-ed25519 AAAA... cert-pusher
#!/bin/bash
set -euo pipefail
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
tar -xf - -C "$t" fullchain.pem privkey.pem
# Proxmox host install path; on a Windows host it is constructd.exe in the service folder.
exec /opt/construct/host/Constructd.Api admin https import --cert "$t/fullchain.pem" --key "$t/privkey.pem"
```

### Who may use it

HTTPS forwards are a **separate per-user permission**, off by default, next to "host forwards"
in the user settings (`PUT /api/v1/users/{name}` with `allowHttpsForwards`, or
`constructd admin users add … --https-forwards`). The host-wide "host forwards" switch
(`network.hostForwardsEnabled`) turns them off too.

Construct does not filter by source address. Whoever can reach the host's ports can open the
link; the application behind it (T3's pairing, for example) decides who gets in. Restrict sources
in the host's firewall if that is not enough.

### The shared HTTPS port

Host setting `https.sharedPort` (usually `443`, off by default). On that port the host routes by
the name the browser asks for:

| Name | Served |
|---|---|
| the host of `vault.webUrl` | the key vault's phone pages and the routes they call, nothing else |
| a VM's own public name (needs `PublicHostPattern`) | that VM's **main** HTTPS forward: `https://<vm>.vms.example/` |
| `<vm>-<name>` under the pattern | that HTTPS forward, by its name: `https://work-vm-app.vms.example/` |
| anything else | `404` |

The rows are checked in that order, and the first match wins. A VM's own name stays reserved even
while the VM has no main forward, and a forward never gets the vault's name or a VM's.

#### Forward names

Every HTTPS forward has a name and is served as `<vm>-<name>` on the shared port. The name is one
DNS label: lowercase letters, digits and hyphens, starting and ending with a letter or digit. It
defaults to the VM port (`work-vm-5173`), which stays the same when a forward is recreated, so
links and bookmarks keep working. `--name app` gives `work-vm-app`.

* **Why one name per forward:** browsers keep cookies and sessions per host name and ignore the
  port. Under one name, every web app of a VM, its main UI included, would see the others'
  cookies, and two instances of one app would log each other out. With a name each, they are
  separate sites.
* **One label, so no new certificate:** `work-vm-app.vms.example` is one level under the domain,
  like `work-vm.vms.example`, so the wildcard certificate (`*.vms.example`) and the wildcard DNS
  record that cover the VM's own name cover it too.
* **Unique on the host:** VM names may contain hyphens, so `work` + `vm-app` and `work-vm` + `app`
  are the same name. A request with an **explicit** name that another HTTPS forward, a VM or the
  vault already uses is refused with code `https-name-taken` (409, exit 7 from the CLI) and says
  who holds it. The default name never refuses a forward: when it is taken, the older forward
  keeps it and the newer one keeps its port address. A VM created later that happens to have such
  a name wins the same way.
* **At most 63 characters** for `<vm>-<name>`, the length of one label. An explicit name that is
  too long is refused; a default one that does not fit just is not used.
* **Fallbacks:** without a shared port, while its listener is not up (the port is in use, for
  example), without a pattern, or when the certificate does not cover the name, the forward is
  advertised with its own port, as before. That port address always works (`portUrl` when `url`
  is the named one). Checking a new name and creating the forward are not one atomic step: two
  requests at the same moment can both pass, and then the older forward gets the name.

The vault pages are relayed to an internal loopback endpoint of the service that answers only
`/vault/…`, `/api/v1/vault/approvals…`, `/api/v1/vault/files…` and `/api/v1/vault/device`, so the
rest of the API is not reachable under that name. Point `vault.webUrl` at a name the certificate
covers (for example `https://vault.vms.example`) to give phones a trusted approval page.

### Start on request

Host setting `https.startOnRequest` (on by default). When the guest does not answer, the browser
gets a small page that reloads itself every few seconds. If the VM is stopped or saved, the host
starts it on the owner's behalf first (audited as `vm.power` with
`initiator=https-start-on-request`, at most once a minute per VM). A running VM whose service is
not up yet just gets the waiting page.

## 2. Guest: publish a port

```bash
construct expose 5173 --https --upstream-plain             # prints https://work-vm-5173.vms.example/ (shared port)
construct expose 5173 --https --upstream-plain --name app  # prints https://work-vm-app.vms.example/
construct expose 5178 --https --upstream-tls --main        # prints https://work-vm.vms.example/
```

Without a shared port, or without `PublicHostPattern`, each prints its forward's own port address
instead, `https://work-vm.vms.example:2310/`.

`construct expose --help` shows these options only when the host would really serve them for this
VM: `GET /api/v1/vms/{name}/https` answers `available: true` only when the host has a certificate
that has not expired and covers the VM's public name, host forwards are enabled, and the VM's owner
has the permission. Otherwise it gives a `reason` (`no-certificate`, `certificate-expired`,
`certificate-name`, `host-forwards-disabled`, `not-allowed`, `primary-only`), and a create request
is refused with code `https-unavailable` (exit 7 from the CLI).

* `--https` implies `--to host`. The forward gets its own public port from the app range, like
  any host forward, and its link is its named address on the shared port, or
  `https://<vm public name>:<port>/` without one.
* `--upstream-plain` or `--upstream-tls` is **required** with `--https`: whether the VM port
  serves plain HTTP (a dev server, most apps) or speaks TLS itself (the host does not verify the
  guest's certificate). The host connects exactly that way. It never probes the port or falls back
  to the other protocol, so a slow start cannot be mistaken for the wrong one. A forward that says
  TLS to a port answering in plain HTTP gets a page that says so; the other way round the browser
  sees the app's own complaint (nginx: "plain HTTP request was sent to HTTPS port"). When the port
  changes, ask again with `--reuse` and the other flag: the old forward is closed and replaced, and
  its name and main address go to the new one.
* `--name <name>` names it ([Forward names](#forward-names)); the default is the VM port.
* `--main` marks the VM's main web UI, served under the VM's own name. One per VM: a second
  one is refused with code `main-taken` (409, exit 7) while another main forward is open, naming
  that one. Provisioning makes T3 Code the main forward.
* `--replace-main` (with `--main`) takes the address over anyway. The newest main forward is
  served; the replaced one keeps its own name and gets the VM's name back when the newer one is
  closed.
* `--reuse` keeps HTTPS and plain forwards of the same port apart; with `--name` it reuses only
  the forward of that name. It reuses an HTTPS forward only when it says the same about the port
  (`--upstream-plain`/`--upstream-tls`), and replaces it otherwise.

To have provisioning request one on every VM a project runs on, declare it in the project's
profile: `"forwards": [{"port": 5173, "https": true, "upstreamTls": false, "name": "app"}]` (see
[Project profiles § Port forwards](projects.md#port-forwards)). Provisioning asks with
`--https-if-available`, so the same profile opens a plain host forward where the host offers no
HTTPS. `--main` is left to T3 Code, which provisioning makes the main forward.

The API fields are `https`, `main`, `upstreamTls`, `name` and `replaceMain` on
`POST /vms/{name}/forwards`; `upstreamTls` (`true` or `false`) is required with `https: true`, and
a request without it is refused with a validation problem on `upstreamTls` (400). A host that
cannot serve HTTPS for the VM answers `https-unavailable` first, whatever the request says. A
forward stored before `upstreamTls` was required is plain HTTP. A forward in a list carries
`https: true`, `main`, `upstreamTls`, its
`name`, and `portUrl` next to `url` when `url` is a shared-port address.
`GET /vms/{name}/https` gives `mainAddress` and `namedAddress`
(`https://work-vm-{name}.vms.example/`) while the shared port serves them.
Only primary VMs can have HTTPS forwards.

### T3 Code

Provisioning asks for T3's forward with `--https-if-available --main --replace-main` and
`--upstream-tls` when T3's own HTTPS proxy is on, `--upstream-plain` when it is not. That is `--https` when it is available and a plain host forward when
it is not, so a host without a certificate behaves exactly as before. With an HTTPS forward the
advertised T3 origin (`T3CODE_PUBLIC_BASE_URL`, pairing links) is the host's https address: the
host name and port of T3's forward as the host service reports it (`T3CODE_PUBLIC_HOST` and
`T3CODE_PUBLIC_PORT` in `config.env`), so it stays right when T3 is not the VM's main forward. A VM
provisioned before the host had a certificate keeps its plain forward until it is reprovisioned;
the old forward stays, so existing pairings keep working, and can be closed with
`construct expose --close <id>`.

## 3. Platform notes

* **Proxmox**: the in-process relay hands HTTPS forwards to the TLS front instead of opening a raw
  listener on their port.
* **Hyper-V**: an HTTPS forward has no `netsh portproxy` rule; the service listens on the port
  itself (reconciliation removes a rule an older build left there).
* Live HTTPS connections count towards the VM's idle signal like any forward's.
* An older build reads an HTTPS forward as a plain host forward, which is the safe fallback for a
  rollback.
