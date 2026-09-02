# `construct expose` — self-serve port forwards from the VM

`construct expose <port>` is how an agent (or a person) on the VM answers the question
*"what URL does the user open?"*. It asks for a forward, waits until the forward is live,
and prints one link.

```console
# on the VM
$ npm run dev            # listening on 0.0.0.0:5173
$ construct expose 5173 --label "vite dev"
http://localhost:5173/
```

The link is the thing to hand to the user. Everything below is the contract behind it.

- [The two targets](#the-two-targets)
- [Command reference](#command-reference)
- [Exit codes](#exit-codes)
- [Configuration](#configuration)
- [Local mode: the guest forward spool](#local-mode-the-guest-forward-spool)
- [Remote mode: the host service API](#remote-mode-the-host-service-api)
- [The VM token](#the-vm-token)
- [Activity heartbeat](#activity-heartbeat)

## The two targets

Plan §4.6 defines two separately configurable forward targets. They answer different
questions, and only one of them is meant to be routine.

| | `--to client` (default) | `--to host` |
|---|---|---|
| Where the port opens | on the **user's PC**, tunnelled over the SSH connection VS Code already holds | on the **VM host's LAN address** |
| Who sees it | only that machine | anything that can reach the host |
| Needs | VS Code with the Construct extension connected | nothing (survives the user's PC being off) |
| Typical link | `http://localhost:5173/` | `http://buildbox.example.local:31234/` |
| Use it for | dev servers, previews, notebooks — everything an agent starts for the user | webhooks, a teammate, another machine |

`--to client` is the default on purpose: it exposes nothing to the LAN, works the same in
local and remote mode, and is what "show this to the user" actually means. `--to host` is
opt-in, and on a remote install the admin can disable it per user — the service then answers
`403` and the CLI says so (exit code 7).

The agent-facing system prompt (`config/systemprompt.md`) deliberately mentions **only**
`construct expose <port>`. Host forwarding is documented here and in `--help`, so agents do
not reach for LAN exposure by habit.

The CLI's own legacy output is left alone for the same zero-change reason the rest of this
batch is: `construct help` (which `construct notify --help` also prints) and the diagnostics
for the existing verbs are byte-identical to what they were before this verb existed, and a
test diffs them against the previous commit. The verb documents itself through
`construct expose --help`, this file, and the system-prompt paragraph.

## Command reference

```
construct expose <port> [--label <text>] [--to client|host] [--wait <sec>]
construct expose --list
construct expose --close <id|port>
construct expose --help
```

| Form | What happens |
|---|---|
| `construct expose 3000` | Requests a client forward for VM port 3000, waits (default 30 s) until it is open, prints the link. |
| `construct expose 3000 --label "api"` | Same, with a label that shows up in `--list` and in the extension's UI. |
| `construct expose 3000 --to host` | Requests a host forward. Local mode prints the VM's own externally reachable URL (NAT already reaches it); remote mode asks the service to publish a port and prints the URL it returns. |
| `construct expose --list` | Lists this VM's forwards with id, port, target, status, label and URL. |
| `construct expose --close 3000` | Closes the forward for VM port 3000. An id works too; a port that has several forwards is ambiguous and is rejected. |

Notes:

- The port must be 1–65535. It is the port **inside the VM** — bind your server to
  `0.0.0.0` (or at least to `127.0.0.1`, which the SSH tunnel can still reach) before
  exposing it.
- A client forward that never gets picked up (no VS Code attached) is **not** an error that
  throws the request away: the request stays queued in the spool and opens as soon as a
  client connects. The CLI says so and exits 6 so a script can tell the difference.
- Local `--to host` forwards are stateless — the VM is already reachable at that address, so
  there is nothing to allocate, nothing to close and nothing for `--list` to show.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success — the forward is open (or the list/close completed). |
| 1 | Usage or local error: bad port, unknown option, unknown id, spool not writable. |
| 6 | No Construct client is attached. The request stays queued and opens later. |
| 7 | The host service refused the request (e.g. host forwards disabled for this user, or the per-VM forward cap is reached). |
| 8 | The host service could not be reached, or answered something unusable. |

## Configuration

All keys live in `/etc/construct/config.env` (three-level precedence: explicit environment
value > saved value in `config.env` > built-in default; an empty explicit value keeps the
saved one). Every default below is "today's behavior", so an existing local install needs no
config change at all.

| Key | Default | Meaning |
|---|---|---|
| `CONSTRUCT_SERVICE_URL` | *(empty)* | Base URL of the `constructd` host service, e.g. `https://buildbox.example.local:7462`. **Empty means local mode**: no service exists, forwards go through the guest spool. |
| `CONSTRUCT_INSTANCE_NAME` | lowercased `hostname` | This VM's instance name on the service, i.e. the `{name}` in `/api/v1/vms/{name}/…`. |
| `CONSTRUCT_EXPOSE_DEFAULT_TARGET` | `client` | Target used when `--to` is not given. |
| `CONSTRUCT_EXPOSE_WAIT_SEC` | `30` | How long `expose` waits for a client forward to come up before reporting it as queued. |
| `CONSTRUCT_SERVICE_CA_FILE` | *(empty)* | Path to a PEM certificate used to verify the service (`curl --cacert`). Set this when the service uses the self-signed certificate generated at install time. Empty = the system trust store. |
| `CONSTRUCT_SERVICE_AUTH_SCHEME` | `VmToken` | `Authorization` scheme for the VM token. `constructd` registers the VM-scoped token under the `VmToken` scheme; set `Bearer` only for a service that expects it there. |
| `CONSTRUCT_EXTERNAL_HOST` | *(empty → `$(hostname).mshome.net`)* | Reused from B2: the address a local `--to host` URL is built from. |
| `CONSTRUCT_IDLE_REPORT_INTERVAL_SEC` | `60` | Heartbeat period for the activity reporter (see below). Must be 5–3600 seconds; anything else (notably `0`, which systemd reads as a *disabled* timer) falls back to 60 in both `provision.sh` and the reporter. |

Two paths are overridable from the environment for testing and unusual layouts:
`CONSTRUCT_FORWARDS_DIR` (default `/etc/construct/forwards`) and `CONSTRUCT_VM_TOKEN_FILE`
(default `/etc/construct/vm-token`).

## Local mode: the guest forward spool

With no `CONSTRUCT_SERVICE_URL` there is no service to talk to — the VM runs on the user's
own Hyper-V. The CLI therefore writes its request into a spool directory that the VS Code
extension watches over the SSH connection it already holds, the same proven pattern as the
notification spool (`construct notify` → `extension/src/notify.js`).

**This spool is a wire protocol** (plan §4.8 rule 4): the extension's client-forwarder module
is the other end of it, and changing a shape here is a versioned decision, not an
implementation detail. Every document carries `"v": 1`.

### Directories and permissions

```
/etc/construct/forwards/            0755 root:root
├── requests/                       0755 root:root   written by the CLI
├── acks/                           0755 root:root   written by the extension
└── close/                          0755 root:root   written by the CLI
```

Root-owned and **not** world-writable, unlike `/run/construct/notify` (1777). The reasoning:
a forward request is an instruction to open a port on the user's PC, so the set of principals
that may write one is deliberately narrower than "anything that can raise a toast". On this
VM agents run as root, and the extension reads and writes the spool over SSH as root, so
root-only is both sufficient and simple. A non-root caller gets a clear error naming the
directory. If a future setup needs a non-root agent user, the intended change is a dedicated
group with `0775`/`setgid` on the three directories — not `1777`.

The directories are created by `provision.sh` (silently, on every provision) and re-created
on demand by the CLI if they are missing, so a VM provisioned before this feature existed
still works.

The spool lives in `/etc/construct` rather than `/run` on purpose: unlike a notification, a
forward request should survive a VM reboot — the dev server is usually restarted too, and the
extension re-opens the tunnel for anything still queued.

### Request — `requests/<id>.json`, written by the CLI

```json
{"v":1,"id":"1756742400-a3f1","vmPort":5173,"label":"vite dev","target":"client","createdAt":"2026-09-01T09:20:00Z"}
```

- `id` — `<unix-seconds>-<4 hex>`, unique per request, filename-safe, and the handle used by
  `--close` and `--list`.
- `label` — free text, control characters stripped, capped at 100 characters. May be empty.
- `target` — always `client`; host targets are not spooled in local mode.
- Written to `requests/.tmp.<pid>.<rand>` and published with `mv`, so a watcher never reads a
  half-written file (same guarantee as the notification spool).

### Ack — `acks/<id>.json`, written by the extension

```json
{"v":1,"id":"1756742400-a3f1","status":"open","localPort":5173,"hostLabel":"christoph-pc","message":""}
```

- `status` — `open` when the port is listening on the user's PC, `error` when it could not be
  opened.
- `localPort` — the port that was actually opened there. It is usually the same number as
  `vmPort`, but the extension may pick another one if that port is busy, which is exactly why
  the CLI waits for the ack instead of guessing the link.
- `hostLabel` — optional. When present the CLI prints `http://<hostLabel>:<localPort>/`
  instead of `http://localhost:<localPort>/` (useful when the user's PC has a name other
  machines use). Omit it for a loopback-only tunnel.
- `message` — optional; shown to the user when `status` is `error`.

The extension should write the ack atomically as well (temp file + rename) and should
overwrite an existing ack for the same id when the tunnel is re-established.

### Close — `close/<id>.json`, written by the CLI

```json
{"v":1,"id":"1756742400-a3f1","closedAt":"2026-09-01T10:05:00Z"}
```

`construct expose --close` writes this file and removes `requests/<id>.json` and
`acks/<id>.json` itself, so `--list` is immediately correct even with no client attached. The
extension tears the tunnel down when it sees the close document and then removes it.

### Lifecycle summary

```
CLI                         spool                        extension
 expose 5173  ──────────►  requests/<id>.json  ──────►  opens localhost:<port>
             ◄──────────   acks/<id>.json      ◄──────  writes ack
 prints the link
 --close     ──────────►   close/<id>.json     ──────►  closes the tunnel, removes it
             (removes request + ack)
```

## Remote mode: the host service API

When `CONSTRUCT_SERVICE_URL` is set, the VM is hosted by `constructd` (see
`service/README.md`) and the spool is not used: the service owns forward state, which is what
makes forwards survive the user's PC being off. The CLI talks to three routes, always with
the VM token:

| Step | Call |
|---|---|
| create | `POST {url}/api/v1/vms/{instance}/forwards` with `{"vmPort":5173,"label":"vite dev","target":"client"}` → `201 {id, vmPort, publicPort, target, label, created, url}` |
| wait / list | `GET {url}/api/v1/vms/{instance}/forwards` → the array of the same objects |
| close | `DELETE {url}/api/v1/vms/{instance}/forwards/{id}` → `204` |

- **Host target**: the service materializes a LAN port and answers with `url` — the CLI
  prints it and exits.
- **Client target**: the service records the forward and relays it to the owner's extension,
  which opens the port on the user's PC. The CLI polls the list until the entry for its id
  reports a link, then prints it. The entry is read leniently: a `url`, or a
  `localPort` (plus optional `hostLabel`) as in the spool ack, or a `status` of `error` with
  a `message`. Anything else is "not open yet".
- **403** → exit 7 with the service's own explanation (host forwards disabled for the owner,
  or the per-VM forward cap reached). **Transport failure, 401, 5xx, a 2xx whose body is not
  the promised JSON shape** → exit 8. This holds *while polling* too: a failed or unreadable
  list ends the command with 7/8, never with the "queued" answer (exit 6) — the request may
  well be open, and telling an agent to keep waiting would be a lie. Only a list that
  genuinely does not carry the forward yet keeps the wait running.

All calls use `curl --fail-with-body --silent --show-error` over TLS. `curl` is invoked with
`-H @<file>`: the `Authorization` header is written to a `0600` file in a private temp
directory, never passed as an argument, so the token never appears in `ps`, in a shell
history or in an error message. `CONSTRUCT_SERVICE_CA_FILE`, when set, is passed as
`--cacert`.

## The VM token

`/etc/construct/vm-token` — mode `0600`, owned by root, one line, no trailing newline
required. It is the per-VM scoped credential from plan §2/§4.6: it authorizes **only** that
one VM's forward management and its activity heartbeat, and nothing else — not `/whoami`, not
another VM's forwards, not any user-level route.

It is written by `provision.sh` when the host passes `CONSTRUCT_VM_TOKEN_B64` (base64, to
survive the SSH/PowerShell layers intact, like `GIT_USER_NAME_B64`). The token is never
echoed, never logged, never written to `config.env` and never placed on a command line; the
provisioning step reports only that a token was installed.

Rotation: issue a new token on the service and re-provision. A VM whose token file is missing
or unreadable in remote mode gets a clear error from `expose` (exit 8) and a logged warning
from the heartbeat — never a stack trace and never the token itself.

## Activity heartbeat

`bin/construct-idle-report.sh`, run by the `construct-idle-report.timer` systemd timer every
`CONSTRUCT_IDLE_REPORT_INTERVAL_SEC` seconds (default 60), answers plan §4.7's second idle
signal: **an agent running a long unattended job must keep the VM alive even with zero
connections**.

The timer is installed and enabled by `provision.sh` **only when `CONSTRUCT_SERVICE_URL` is
non-empty**. A local install gets no new unit — there is no service to report to, and idle
policy is not enforced locally (plan §4.7).

Each run evaluates four probes and posts the result to
`POST {url}/api/v1/vms/{instance}/activity` with the VM token:

```json
{"busy":true,"reasons":["ssh-session","agent-cpu:claude","tmux-activity"]}
```

| Reason | Probe |
|---|---|
| `ssh-session` | An established TCP connection to port 22 (`ss -tn state established '( sport = :22 )'`), or a login in `who` when `ss` is missing **or fails**. A probe that cannot answer falls through to the next source; it never counts as "nobody is connected". |
| `agent-cpu:<name>` | An agent process — `claude`, `codex`, `opencode`, `t3`, or a `node`/`bun`/`python3` whose command line names one of those stacks — **or any of its descendants** whose `utime+stime` in `/proc/<pid>/stat` grew by more than `CONSTRUCT_IDLE_CPU_TICKS` (default 10 ticks ≈ 0.1 CPU-seconds) since the previous run. Descendants matter: an agent running a test suite sits at ~0% itself while its children do the work, so the CPU of a process is attributed to the nearest ancestor that is an agent. The previous sample is kept in `/run/construct/idle-state.json`. |
| `tmux-activity` | A `tmux` window whose `#{window_activity}` is newer than the report interval. (`#{pane_activity}` is a valid format field that resolves to an *empty string* on tmux 3.x — using it would report a busy detached agent as idle.) Skipped when `tmux` is not installed. |
| `provisioning` | `/run/construct/provisioning` exists **and** the PID it contains is still alive. `provision.sh` writes the marker at the start of a run and removes it at the end; the PID check is what keeps a run killed mid-flight (dropped SSH, reboot) from pinning the VM as busy forever. |

`busy` is `true` when any reason fired. The heartbeat is deliberately generous — a false
`busy` costs some host RAM until the next tick, a false idle kills someone's unattended job.
That asymmetry decides the "we cannot tell yet" case: a process the reporter has **no
previous sample for** (the first run after a reboot, or one that appeared since the last
run) is judged on all the CPU it has burned so far rather than on a delta, so a working
agent reads as busy from its very first heartbeat. An explicit `busy: false` buys the guest
no grace window in the service — only silence does — so guessing `false` there could hand a
VM to the idle scheduler while an agent is mid-job.

Failures are never fatal: no service URL configured means the script exits 0 without doing
anything (that is the local default path), and a failed POST is logged to the journal and
retried by the next tick. The token is passed to `curl` through the same `0600` header file
as `expose`.

Environment overrides (used by `test/idle-report.test.sh`, and available for debugging):
`CONSTRUCT_IDLE_STATE_FILE`, `CONSTRUCT_IDLE_PROC_DIR`, `CONSTRUCT_IDLE_SS`,
`CONSTRUCT_IDLE_WHO`, `CONSTRUCT_IDLE_TMUX`, `CONSTRUCT_IDLE_CURL`,
`CONSTRUCT_PROVISION_MARKER`, `CONSTRUCT_IDLE_CPU_TICKS`, `CONSTRUCT_IDLE_SSH_PORT`,
`CONSTRUCT_IDLE_DRY_RUN` (print the JSON instead of posting it).

## Related

- `docs/plans/modular-remote-architecture.md` §4.6 (forwards), §4.7 (idle), §4.8 (module rules)
- `service/README.md` — the host service's API and authentication
- `docs/remote-access.md` — how the VM is reached in the first place
