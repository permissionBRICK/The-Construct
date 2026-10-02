# Key vault — secrets agents may use only with your approval

The key vault keeps tokens, passwords, keys and username/password pairs on **your PC**, inside
Construct Companion. Agents in a VM can see which secrets exist and what they are for, but they
read a value only after you approve it in the Companion: for a number of uses, for a time,
or both. When the access ends, the Companion searches that VM for every copy of the value. Agent
logs are redacted automatically. Any other file is listed for you, and you decide per file.

VMs on a host service (remote Hyper-V or Proxmox) use a copy of the same vault kept on that host,
so they work while your PC is off, and you can approve from a paired phone. See
[Hosted VMs](#hosted-vms).

- [Managing secrets](#managing-secrets)
- [What agents do](#what-agents-do)
- [Approvals and leases](#approvals-and-leases)
- [Scrubbing a VM](#scrubbing-a-vm)
- [Hosted VMs](#hosted-vms)
- [Security model](#security-model)
- [Wire contract](#wire-contract)

## Managing secrets

Open **Key Vault** from the Companion tray menu or the control panel's **Key vault** card (or run
`ConstructCompanion.exe --vault`, or open `construct://vault`). Toasts from the vault open it too. The
window follows the design you chose for the control panel. It shows names, descriptions, usernames and
who holds what; values are only typed, copied and shown in native dialogs. In VS Code without the
Companion, the card only says where the vault is.

| Tab | What it shows |
|---|---|
| **Secrets** | Name, username, description and how many VMs hold access. **Add…**, **Edit…**, **Delete**, **Copy secret** (cleared from the clipboard after 30 seconds unless something replaced it) and **Copy username**. |
| **Access** | Every active lease: VM, secret, uses left, end time, the agent's reason. **Revoke access** ends a lease at once. Below it, scrubs that wait for their VM to come online; **Discard pending scrubs for VM** forgets them (for a VM you removed). |
| **Activity** | What the vault did: approvals, expiries, scrubs and their results, warnings, including those reported by your hosts. |
| **Hosts** | One section per enrolled host: sync state, the vault mode, paired phones (**Pair a phone…**, revoke), and **Show vault key…** / **Import vault key…** for a second PC. |

A name uses letters, digits, `.`, `_` and `-` (up to 64 characters). Descriptions are what an
agent sees in `construct secret list`, so say what the secret is for. Values may span several
lines (SSH keys, certificates); **Load from file…** reads one, and a multi-line value is only
editable while **Show** is ticked. Values are limited to 32 KiB.

Editing the description or username keeps existing access. A new value or a rename ends every
lease on the old value, and those VMs are scrubbed of it.

The vault is stored in `%LOCALAPPDATA%\The-Construct-Vault\vault.dat`, encrypted with Windows
DPAPI for your Windows account. It is not part of the config sync; hosts get their copy through
the vault's own sync (below). If that file cannot be decrypted, for example after copying a profile to another PC,
VMs get an error and the window offers **Start a new vault…**. That renames the unreadable file
and keeps it, so it can still be restored on the PC and account that created it.

## What agents do

The agent instruction file tells agents to request everything a task needs at the start, so you
can approve once and leave. The CLI documents itself (`construct secret --help`):

```console
$ construct secret list
NAME           ACCESS        DESCRIPTION
github-token   none          GitHub token for the release repo
staging-db     none          Staging database admin login

$ construct secret request github-token staging-db --for 2h --reason "release 4.2"
Approved: github-token, staging-db — until 15:30

$ git push https://x-access-token:$(construct secret get github-token)@github.com/org/repo.git
$ construct secret get staging-db --username
$ construct secret release --all
```

| Command | Approval | What it does |
|---|---|---|
| `list`, `status` | no | All secrets with descriptions (never values) / only those this VM holds now |
| `request <name>… [--uses N] [--for 2h] [--reason …]` | yes, once for all names | Pre-approves access. Without `--uses`/`--for` it asks for one hour. |
| `get <name> [--username\|--json]` | only without a lease | Prints the value (or username) and counts one use. Without a lease it asks for a single use. |
| `release <name>…` / `--all` | no | Ends this VM's access. The scrub starts a few seconds later. |
| `add <name> --description … [--username …] [--for 2h]` | no | Stores a secret the agent created, read from stdin or `--file`. This VM holds it for an hour (or `--for`), then it is scrubbed. |
| `add <name> … --replace` | yes | Overwrites an existing secret's value. VMs that saw the old value are scrubbed of it. |
| `delete <name>` | yes | Removes a secret from the vault. |

`get` prints the value exactly, with no newline added. Values never appear on a command line:
`add` reads stdin, and every value travels between the VM and the Companion only on stdin
streams. Exit codes: `0` ok, `1` usage error, `6` no Companion is connected to this VM, `7`
denied or not answered in time, `8` Companion error, `9` no such secret, `10` name already
taken, `11` the vault is locked for this VM (hosted VMs: start or connect the VM from your PC).

## Approvals and leases

A request appears in the Companion's key vault pop-out, next to the tray icon and on top of your
other windows, in the design you chose for the control panel. The pop-out does not take the
keyboard focus, so typing meant for another window cannot reach it. Each request names the VM
(and its host), each secret and its description, the access asked for, the agent's reason, the
requesting user and the time left. **Deny** works at once; **Approve** becomes clickable a second
after the request appears. Requests from several VMs are listed together, oldest first.

The pop-out stays until no request is left. Its **×** hides it until the next request arrives;
while requests wait, **Key vault requests (N)…** at the top of the tray menu, or a left click on
the tray icon, brings it back. While T3 Code Desktop is visible and shows a request in its banner
(below), the pop-out steps back for that request. The agent waits up to ten minutes by default
(`--wait`). When it gives up, the request leaves the list and counts as denied. A request answered
elsewhere (T3 Code Desktop, a paired phone, the host's approval page) leaves the list too.

A lease belongs to one VM and one secret:

- `--uses N` (1–1000) and/or `--for` (1 minute to 24 hours). Uses-only leases still end after
  24 hours. Approving a new request replaces that VM's existing lease on the secret.
- A `get` without a lease asks for a single use and is valid for ten minutes.
- A secret added by an agent is leased to that VM for one hour by default.
- A lease ends when its time is up, its last use is taken, the agent releases it, you revoke it,
  or the secret is deleted or gets a new value.

Leases and pending scrubs are saved with the vault, so they survive a Companion restart.

## Scrubbing a VM

When a lease ends, the Companion scrubs that VM: a few seconds after a release or revocation, at
once on expiry, and one minute after the last use (the command that used it may still be
running). The VM must be online with the Companion connected. Otherwise the scrub waits, and
the **Access** tab lists it. A failed scrub is retried five minutes later. If the same VM holds
the secret again before the scrub runs, the scrub waits for that lease to end.

The search runs on the VM, at low CPU and I/O priority. It covers `/root`, `/home`, `/tmp`,
`/var/tmp`, `/var/log`, `/etc`, `/opt` and `/srv` on their own file systems. It skips
dependency and build caches (`node_modules`, `.git/objects`, `.cache`, package caches, Rust
`target` directories marked with `CACHEDIR.TAG`) and ordinary files over 256 MiB. Agent data
directories are always searched, whatever their size. It looks for the value as written, its
JSON-escaped and URL-encoded forms, and, with a username, the HTTP Basic credential. A
multi-line value (a private key, a certificate) only counts when a file holds all of it, with LF
or CRLF line ends and without the whitespace around it. A single line of it is not enough: every
private key of one type starts with the same lines, so line matches would flag all keys on the
VM. Values shorter than six characters are not searched; the activity list says so.

**Agent logs are redacted without asking.** Every occurrence is overwritten in place with `*`
characters of the same length, so files that are still being appended to stay intact. SQLite
databases are cleaned through SQLite itself, with `secure_delete` on, followed by a checkpoint.
These locations count as agent logs, under `/root` or any `/home/<user>`:

- Claude Code: `.claude/projects/`, `.claude/history.jsonl`, `.claude/todos/`,
  `.claude/shell-snapshots/`, `.claude/debug/`, `.claude/file-history/`, `.claude/paste-cache/`,
  `.claude/session-env/`, `.claude/sessions/`, and `/tmp/claude-*`
- Codex: `.codex/sessions/`, `.codex/archived_sessions/`, `.codex/history.jsonl`, `.codex/log/`,
  `.codex/logs_*`
- OpenCode: `.local/share/opencode/storage/`, `.local/share/opencode/log/`,
  `.local/share/opencode/opencode.db*`
- T3 Code: `.t3/userdata/logs/`, `.t3/userdata/state.sqlite*`

**Every other file needs your decision.** A dialog lists each file with the secrets it contains
and its kind, and each file gets **Keep** (the default), **Redact** or **Delete file**. Deleting
a database also deletes its `-wal`, `-shm` and `-journal` files. A toast reports the result, and
the **Activity** tab keeps it.

If an app keeps a SQLite database open while it is scrubbed, old pages can stay in its
write-ahead log until the app checkpoints. The activity list warns when that happens. Git
history is not rewritten: a value committed to a repository is found in the working tree,
but its copies in `.git/objects` and on any remote are yours to handle.

## Hosted VMs

On a VM of a host service, `construct secret` talks to the host service instead of the
Companion, and the host keeps the leases, approvals and scrubs. Everything else works as above.

**One vault for all your hosts.** The Companion syncs your vault with every host you are
enrolled on: every five minutes, shortly after each change, and on **Sync now**. Each entry is
merged on its own; the newer edit wins, and a deletion counts as an edit. Secrets an agent adds on
a hosted VM appear on your PC after the next sync. Every one of your VMs sees every secret's name
and description; child VMs see nothing.

Values (and usernames) are encrypted with a vault key that the Companion creates and keeps in
your PC's vault. Names and descriptions are stored readable on the host, so agents can list them
and approval prompts can describe them while the vault is locked. To use the same vault from a
second PC, copy the key with **Show vault key…** on the first PC and **Import vault key…** on the
second.

**Two modes per host** (Hosts tab):

| Mode | What the host can do | When to use it |
|---|---|---|
| **Always available** (default) | The host keeps the vault key, wrapped with its own master key (Windows DPAPI on Hyper-V hosts, a root-only key file on Proxmox). Requests, approvals from a phone and scrubs work while your PC is off. | You trust the host's administrators with the values. |
| **Locked to my PC** | The host never stores the key. Your Companion sends it when it starts the VM or connects to it while the VM runs. The host keeps it only in memory, and only for that VM, until the VM stops, is saved, idles out or the service restarts. A VM someone else starts without your PC stays locked: agents can list secrets, but `get` and `add` fail with exit code 11. | The host is shared, or you only work with your PC at hand anyway. |

**Approving from a phone.** **Pair a phone…** asks which VM's T3 Code the phone should open, and
shows a QR code. Scanning it opens a short pairing page on the host service. That page stores a
device token in the browser and forwards to T3 Code's own pairing link, so one scan both signs
the phone in to T3 Code and pairs it for approvals. The T3 Code part of the code is valid for about
10 minutes and works once. Later, open the approval page on the phone, `https://<host>:7462/vault/`
or the [approval page address](#your-own-proxy-for-t3-code-and-the-approval-page) of the host, to
see pending requests and file decisions. The Companion shows the same requests at the same time;
the first answer counts.

**Noticing a request in T3 Code.** While `construct secret` waits for an answer, T3 Code shows a
banner on every open client (phone or PC): "Key vault: github-token waiting for your approval". On
a hosted VM it has an **Approve** button that opens the host's approval page with that request
highlighted. On a local VM it points you to the Companion's pop-out on your PC. In a browser the banner
only links; approving still happens on the host's page or in the Companion. It disappears when the
request is answered or times out. Nothing alerts you while no T3 Code page is open.

**Approving inside T3 Code Desktop.** T3 Code Desktop on the PC where the Companion runs asks the
Companion for its pending approvals, local and hosted alike, and shows each one in the banner with
the Companion's own text and **Deny** / **Approve** buttons. An answer there counts like one in the
Companion's pop-out: the request leaves the pop-out, and a hosted VM's answer goes to the host.
Whichever answer comes first counts; a later one is refused as already answered.

While its window is visible and the banner is not closed, T3 Code Desktop tells the Companion on
every poll which requests it shows. The pop-out then gives a new request five seconds to appear in
T3 Code Desktop. It shows the request if T3 Code Desktop does not list it by then, or eight seconds
after T3 Code Desktop last listed it (minimized, closed, or the banner closed). With no such report
in the last ten seconds, the pop-out shows a request at once. The tray menu and a left click on the
tray icon bring the pop-out up at any time.

The device token lives only on the approval page's address, never in T3 Code. T3 Code is
served from inside the VM, where an agent could read anything stored for its page. The host's
certificate is self-signed unless a proxy with a trusted certificate stands in front of it (next
section), so the phone warns about it once; the pairing dialog says so while that is the case.
Revoke a lost phone on the Hosts tab.

### Your own proxy for T3 Code and the approval page

A phone uses two addresses: T3 Code's, which the QR code logs it in to, and the approval page's.
Out of the box both are addresses of the host (a forward such as `https://<host>:2301` and
`https://<host>:7462`) with certificates the phone does not trust. If you reach them through your
own reverse proxy with a trusted certificate, record both addresses:

- **T3 Code, per VM.** In the control panel: Settings → Access & services → **T3 Code address via
  your own proxy**, then **apply**. Agents on the VM can record it too, which is handy when you
  ask one to set up the proxy:

  ```bash
  construct config set t3-proxy-url https://t3.example.net:8443
  construct config get t3-proxy-url
  construct config unset t3-proxy-url
  ```

  The value lives only in the VM's `/etc/construct/config.env` (`T3CODE_PROXY_URL`). The panel
  shows what is stored there, including a value an agent set, and **apply** writes it there over
  SSH at once. Provisioning never writes it, and a reinstall carries it over. T3 Code binds a
  pairing link to the address it was minted for, so the pairing script mints one more link for
  this address: the phone QR code uses it instead of the host forward, and **Open T3 Code**
  offers it first.
- **The approval page, per host.** A host administrator sets it in Host Administration →
  Configuration → **Key vault** (`vault`), key `webUrl` (approval page address), for example
  `{"webUrl": "https://vault.example.net"}`. It is used for QR codes, for the **Approve** link of
  the T3 Code banner, and for the address phones open later. It takes precedence over the
  service option `Constructd:VaultWebUrl`, which in turn replaces `https://<host>:7462`. Only an
  `https` origin without a path is accepted. Agents and VM tokens cannot change it.

**Keep the two origins apart.** The approval page keeps the phone's device token in the
browser's storage for its origin (scheme, host and port). A T3 Code page on the same origin
could read it, and T3 Code is served from inside the VM. Use separate host names, such as
`t3.example.net` and `vault.example.net`, or at least separate ports. The Companion refuses to
show a QR code whose two addresses share an origin, and the pairing page does not follow a T3
Code link on its own origin.

**What the approval page's proxy should pass.** Phones need only the pages under `/vault/` and
three device routes: `/api/v1/vault/approvals` and `/api/v1/vault/files` (each also with `/<id>`
for an answer) and `/api/v1/vault/device`. Keep the rest of the host service's API (sign-in, admin and VM
routes) off the proxy. The host service presents its own self-signed certificate. Pin it upstream
instead of turning verification off: every VM of that host has a copy in
`/etc/construct/service-ca.pem`. An nginx example:

```nginx
server {
    listen 443 ssl;
    server_name vault.example.net;
    ssl_certificate     /etc/ssl/vault.example.net/fullchain.pem;
    ssl_certificate_key /etc/ssl/vault.example.net/privkey.pem;

    # The host service, verified against its own certificate.
    proxy_ssl_verify              on;
    proxy_ssl_trusted_certificate /etc/nginx/construct-host.pem;  # copy of service-ca.pem
    proxy_ssl_name                host.example;                   # a name in that certificate
    proxy_ssl_server_name         on;

    location = /vault { return 301 /vault/; }
    location /vault/ { proxy_pass https://host.example:7462; }
    location = /api/v1/vault/device { proxy_pass https://host.example:7462; }
    location ~ ^/api/v1/vault/(approvals|files)(/[^/]+)?$ { proxy_pass https://host.example:7462; }
    location / { return 404; }
}
```

The T3 Code proxy forwards everything, WebSocket upgrades included, to the VM's T3 Code address
(the forward **Open T3 Code** shows, or the VM's own port on a directly reachable VM). Its
upstream certificate comes from the VM's local CA, `/etc/construct/tls/ca.crt` on the VM.

**Scrubs on hosted VMs** run inside the VM, so they need neither your PC nor SSH. When a lease ends,
the host queues a scan. The VM's minute heartbeat picks it up and runs the same scan, and agent
logs are redacted at once. Other files wait for your decision in the Companion or on the phone,
and are handled on a later heartbeat. Only one scan per VM runs at a time. A scan that never
reports back is handed out again after 30 minutes. A locked VM's scrub waits until it is unlocked.

## Security model

The vault keeps secrets off a VM until you approve, limits how long and how often a VM may read
them, and removes the copies that agents usually leave behind. It does not make an approved
secret safe from the agent that received it: agents run as root, so during a lease they can copy
the value anywhere, including off the VM. Approve only what the task needs, prefer short leases
and few uses, and revoke anything you did not expect.

- Requests come from inside the VM and are treated as untrusted. Names, sizes and every field
  are validated, and reasons and descriptions are shown as quoted text.
- Approval happens only on your PC or a phone you paired. Nothing inside the VM can approve a
  request, and the phone's device token never touches a page the VM serves.
- Values cross the VM boundary only on SSH stdin streams and never appear in command lines or
  environment variables. On the VM they touch only the spool on tmpfs (a request from `add`, a
  response the CLI deletes as soon as it reads it, the scan's pattern files), never the disk.
  Unclaimed responses are deleted after two minutes.
- The Companion never logs values, and they never reach a webview or the local HTTP API. The Key
  Vault window's page gets names, descriptions, usernames and access details, and the pop-out's page
  the pending requests' texts; both are handled inside the Companion process, so nothing on the local
  HTTP API can list or change the vault.
- Pending approvals are the exception: the local HTTP API lists them (the request's text, the VM,
  the secret names, the deadline) and accepts an approve or deny for each. It answers only on
  `127.0.0.1` and only with the bearer token in `endpoint.json` in your Windows profile, so any
  program running as your Windows account can approve a pending request, just as it could click
  **Approve** in the pop-out. Such answers appear in the **Activity** tab as given "from another app
  on this PC". T3 Code Desktop uses this to approve inline; it keeps the token in its main process and
  gives its window only the list and the two answers. Reporting requests as shown in another app
  only keeps them out of the pop-out, which is less than such a program could do by answering them.
  Values, usernames, the vault key, device tokens and pairing links are never part of it, and
  nothing else of the vault is reachable there.

On hosted VMs, the host service holds the vault copy and answers the VM, so the host is trusted
with the values in **always available** mode. In **locked** mode it can read them only while your
Companion has unlocked that VM. Paired phones can approve requests and decide about files, but
they cannot read values, leases or the vault itself.

## Wire contract

While the user is being asked, the CLI also keeps a note for T3 Code's banner:
`/run/construct/vault-pending/<id>.json` (directory 0755, file 0644, published atomically, removed
when the request is answered, times out or the CLI exits). It contains no secret:
`{"v":1,"id","vm","op","names":[…],"reason","deadline":<ms>,"approveUrl":"https://<host>/vault/#request=<id>"|null}`.
On hosted VMs the link comes from the host's `202` reply (`approveUrl`); on local VMs it is `null`.
T3 Code treats the files as untrusted input and only ever renders a link from them.


`construct secret` writes one request per file into the VM's spool `/run/construct/vault`
(tmpfs, mode 0700). The Companion holds one SSH watch per online VM that claims requests (atomic
rename), answers each request, and writes the response back into the spool.

```
requests/<id>.json    {"v":1,"id","ts","op","names":[…],"uses","ttl","all","replace","reason",
                       "description","username","secret":<base64>,"deadline":<ms>,"source"}
responses/<id>.json   {"v":1,"id","status":"ok|denied|notFound|exists|invalid|error","message",
                       "secret":<base64>,"username","lease":{"usesLeft","expiresAt"},"names":[…],"items":[…]}
```

The Companion's local API (`http://127.0.0.1:<port>`, bearer token from `endpoint.json`) has three
routes for pending approvals:

```
GET  /v1/vault/approvals       200 {"approvals":[{"id","instance","vm","kind":"local"|"host","host":<slug>|null,
                                    "requestId":<the CLI's id>|null,"hostRequestId":<the host's id>|null,
                                    "op","title","message","action","deny","names":[…],"createdAt":<ms>,"deadline":<ms>|null}]}
POST /v1/vault/approvals/{id}  {"decision":"approve"|"deny"} -> 204 | 400 | 404 {"code":"not-found"}
                               | 409 {"code":"already-decided"} | 502 {"code":"host-failed"}
POST /v1/vault/approvals/displayed  {"ids":[<id>,…]} -> 204 | 400 {"code":"invalidIds"} | 401
```

`requestId` is the id of the pending note above, and `hostRequestId` the `request=` id of its
`approveUrl`. Errors are RFC 7807 problems. `502` means the host did not take a hosted VM's answer;
the activity list has the reason.

`displayed` takes 0 to 50 ids, each up to 128 characters of `A-Za-z0-9._~-`. T3 Code Desktop posts
it on every poll while its window is visible, with the ids it shows (an empty list when it shows
none). The Companion marks each listed pending id as shown elsewhere for eight seconds and ignores
unknown or answered ids. Every accepted call also counts as a report from a visible app. While the
last report is younger than ten seconds, the pop-out gives a new request five seconds from its
arrival and then shows it unless its mark is current; otherwise it shows the request at once.

The CLI waits 15 seconds (`CONSTRUCT_VAULT_PICKUP_SEC`) for the request to be claimed, then until
its deadline for the answer. The host-run guest scripts live in
`companion/src/Construct.Companion.Core/Vault/GuestScripts/`: `vault-watch.sh` (claim loop),
`vault-respond.sh` (publish a response), `vault-scan.sh` (find files) and `vault-clean.sh`
(redact or delete). Their stdin is the only channel that carries values.
