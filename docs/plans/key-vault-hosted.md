# Key vault on hosted VMs — design and contract

Status: design for the follow-up to the Companion key vault ([user guide](../key-vault.md)).
This document is the contract the host service, the guest CLI and the Companion are built against.

## Decisions (project owner, 2026-10-02)

1. VMs on a host service (remote Hyper-V or Proxmox) use a vault **on that host**, so requests,
   approvals and scrubs work while the user's PC is off.
2. Each user chooses per host between **always available** (the host can open the vault at any
   time) and **locked** (the vault opens only while the user's own Companion has started or
   connected to the VM; the key then stays in host RAM until that VM stops, is saved, or idles
   out). A VM an operator starts without the user's PC stays locked.
3. A phone approves with a **stored device token**, not a passkey. One QR code pairs approvals and
   logs the phone into T3 Code.
4. One vault per **user**, managed centrally in the Companion and synchronized to every host the
   user has VMs on. Every primary VM of that user sees every secret's name and description;
   values still need approval. Child VMs get no vault access.

## Security constraint: approvals never come from the VM

T3 Code's web client is served by the T3 server **inside the VM**, and agents are root there. Any
approve button or pairing token that lives on T3's origin can be forged or read by an agent. So:

- The approval page and the device token live on the **host service's own origin**
  (`https://<host>:7462/vault/…`, or a configured public URL in front of it).
- The QR code opens the host's pairing page first. It stores the device token in that origin's
  `localStorage`, then forwards to the T3 pairing link. That is still one scan.
- T3 Code may at most show a "request pending" banner that links to the host page. It never
  approves anything itself.

## Key material

| Item | Where | Notes |
|---|---|---|
| Vault key `K` (32 random bytes) | Generated once by the Companion, kept in its DPAPI vault document | The same `K` for every host of that user. Shown once for import on a second PC. |
| Key check | Host, per user: `base64(AES-GCM(K, "construct-vault-check", aad="check"))` | Lets the host reject a wrong `K` on unlock or mode change. |
| Wrapped `K` | Host, per user, **only in always-available mode**: `K` encrypted with the host master key | Host master key: DPAPI LocalMachine on Windows, a root-only file on Proxmox (the pattern of `WindowsKeyCipher`). |
| Unlocked `K` | Host RAM only, per VM, in locked mode | Dropped when that VM is observed in any state other than `running`, or is stopped, saved, idled out, fenced for deletion or deleted, and on service restart. |

Entry payloads are `AES-256-GCM(K)`, encoded as `base64(nonce[12] || tag[16] || ciphertext)`.
The AAD is `"entry:<name>:<updatedAt>"` (no owner, so one payload serves every host of the
user) and the plaintext is the UTF-8 JSON
`{"username":"…","secret":"…"}`. Names, descriptions and `hasUsername` are stored in clear: agents
may read them without approval, and the host needs them to render approval prompts while locked.

## Vault entries and sync

An entry is `{name, description, hasUsername, payload, updatedAt (ms), updatedBy, deleted}`. A
deleted entry is a tombstone without a payload. Tombstones are kept for 30 days.

The merge rule is the same on every side: for each name, the entry with the larger `updatedAt`
wins; on a tie, the lexically larger `updatedBy` wins. The Companion syncs with each enrolled host
every five minutes, after every local change and on demand:

```
GET  /api/v1/vault/entries            -> {revision, entries:[…], mode, keyCheck|null, devices:[…]}
PUT  /api/v1/vault/entries {entries:[…]}  -> {revision, applied:n}   (host applies the merge rule per entry)
```

The full set is exchanged both ways: vaults hold dozens of entries, not thousands. The Companion
decrypts host-side entries with `K`, merges them into its own document, and pushes its
winners back. The host never needs `K` to merge.

`updatedBy` is `pc:<install id>` or `vm:<vm name>`.

## Host service API (all under `/api/v1`)

### User credential (the Companion, owner only; admins get no access to other users' vaults)

| Route | Body / reply |
|---|---|
| `GET /vault/entries`, `PUT /vault/entries` | sync, above |
| `PUT /vault/settings` | `{mode:"available"\|"locked", key?:base64 K, keyCheck?:…}`. Setting `available` requires `key`, which the host wraps and stores. `locked` deletes the wrapped key. The first call sets `keyCheck`. |
| `POST /vms/{name}/vault/unlock` | `{key: base64 K}` → `204`. Owner of a running primary VM only; the key is checked against `keyCheck`. A no-op (`204`) when the VM is already unlocked or the vault is always available. |
| `GET /vault/approvals` | Pending approvals of this user's VMs (below). |
| `POST /vault/approvals/{id}` | `{decision:"approve"\|"deny"}`. First answer wins: `409` when already decided. |
| `GET /vault/leases`, `DELETE /vault/leases/{id}` | Active leases; revoke. |
| `GET /vault/files`, `POST /vault/files/{id}` | Files waiting for a decision after a scrub; `{action:"keep"\|"redact"\|"delete"}`. |
| `GET /vault/devices`, `POST /vault/devices {label}`, `DELETE /vault/devices/{id}` | Paired devices. `POST` returns `{id, token}` once; the host stores only `SHA-256(token)`. |
| `GET /vault/activity` | Last 100 events of this user's vault. |

### Device credential (`Authorization: VaultDevice <token>`)

`GET /vault/approvals`, `POST /vault/approvals/{id}`, `GET /vault/files`, `POST /vault/files/{id}`,
and `GET /vault/device` (who am I: user, label). Nothing else: a device cannot read values, list
leases, sync or pair other devices.

### VM token (the guest)

| Route | Body / reply |
|---|---|
| `POST /vms/{name}/vault/requests` | The request document of the guest spool contract (`op`, `names`, `uses`, `ttl`, `all`, `replace`, `reason`, `description`, `username`, `secret` base64, `deadline`). Ops that need no user answer reply `200` with the response document at once. Otherwise `202 {id}`. |
| `GET /vms/{name}/vault/requests/{id}?wait=25` | Long poll, at most 25 s: `200` + response document when decided, `204` while still pending, `404` unknown or expired. |
| `GET /vms/{name}/vault/scrubs` | Due scrub jobs for this VM: `{jobs:[{id, step:"scan"\|"apply", patterns:[{index, pattern(base64)}], names:[…], files:[{path(base64), action}]}]}` |
| `POST /vms/{name}/vault/scrubs/{id}` | `{step:"scan", hits:[{path(base64), indexes:[…], size, type}], complete:true}` or `{step:"apply", results:[{path(base64), status, count, detail}]}` |

Response documents are identical to the guest spool contract (`status` ok / denied / notFound /
exists / invalid / error, plus `locked`, which the CLI maps to exit code 11).

The activity heartbeat reply (`POST /vms/{name}/activity`) gains `"vaultScrub": true` while a
job is due for that VM. The guest then runs the scrub once, in the background.

### Shapes

```jsonc
// GET /vault/entries
{"revision":12,"mode":"available","keyCheck":"<base64>|null","unlockedVms":["dev"],
 "entries":[{"name":"github-token","description":"…","hasUsername":false,"payload":"<base64>",
             "updatedAt":1730000000000,"updatedBy":"pc:3f…","deleted":false}]}
// PUT /vault/entries  {"entries":[…same shape…]}  -> {"revision":13,"applied":2}
// PUT /vault/settings {"mode":"locked","keyCheck":"<base64>"}            -> 204
//                     {"mode":"available","key":"<base64 K>","keyCheck":"…"} -> 204
// GET /vault/approvals
{"approvals":[{"id":"…","vm":"dev","op":"request|get|add|delete","title":"…","message":"…",
               "action":"Approve","names":["github-token"],"uses":3,"ttlSeconds":3600,
               "reason":"…","source":"root@dev","createdAt":ms,"deadline":ms}]}
// POST /vault/approvals/{id} {"decision":"approve"}  -> 204 | 409 {"code":"already-decided"} | 404
// GET /vault/leases
{"leases":[{"id":"…","vm":"dev","name":"…","usesLeft":2|null,"expiresAt":ms,"reason":"…","origin":"approved|once|added"}]}
// GET /vault/files
{"files":[{"id":"…","vm":"dev","path":"/root/repo/.env","names":["github-token"],"type":"text","size":30,"createdAt":ms}]}
// POST /vault/files/{id} {"action":"redact"} -> 204
// POST /vault/devices {"label":"Phone"} -> {"id":"…","token":"<43 chars base64url>"}
// GET /vault/devices -> {"devices":[{"id","label","createdAt","lastUsedAt"}]}
// GET /vault/device (device token) -> {"user":"…","label":"…"}
// GET /vault/activity -> {"events":[{"at":ms,"vm":"dev","text":"…","warning":false}]}
// GET /vms/{name}/vault/scrubs
{"jobs":[{"id":"…","step":"scan","patterns":[{"index":0,"pattern":"<base64>"}],"names":["github-token"]},
         {"id":"…","step":"apply","files":[{"path":"<base64>","action":"redact|delete","patterns":["<base64>"]}]}]}
```

Errors are RFC 7807 problem documents with a `code`, like the rest of the API. Each approval
`message` is the same text the Companion dialog shows, rendered by the host.

## Request handling on the host

The leases, defaults, caps, approval texts and scrub scheduling are the same as in the Companion
vault (see the user guide), with these differences:

- Approvals are pending records, answered by the first approver: the user's Companion (polling
  `GET /vault/approvals` every 3 s while any of that user's VMs is online and showing the same
  native dialog) or a paired device. The request's `deadline` expires the record.
- `get` needs `K`: in locked mode the VM must be unlocked, otherwise the reply is
  `locked`: "The key vault is locked for this VM: start or connect it from your PC's Construct
  Companion." `list` and `status` work while locked.
- `add` needs `K` as well, to encrypt the payload. `delete` needs approval but no `K` (it writes a
  tombstone).
- A scrub job keeps its own copy of the payload, so deleting or changing an entry cannot lose
  what has to be searched for. The patterns are computed when the job is delivered, which needs
  `K`: a locked VM's scrub waits until it is unlocked again.

## Scrubs on hosted VMs

The guest runs the same `vault-scan.sh` and `vault-clean.sh` from its Construct checkout
(`/opt/construct/repo`), rendered locally:

1. The heartbeat sees `vaultScrub`, and `construct secret _scrub` fetches the jobs.
2. A `scan` job: the guest runs the scan with the job's patterns on stdin and posts the hits.
3. The host classifies each hit with the agent-log rules (the same list as the Companion). Agent
   logs become an `apply` job with `redact`; other files wait for the user's decision in the
   Companion or on the phone page, then become `apply` jobs.
4. The guest applies them and posts the results; the host records them in the activity list and
   notifies the Companion (toast) on its next poll.

## Phone pairing and the approval page

The host serves `GET /vault/` (approvals and file decisions) and `GET /vault/pair` as static
pages embedded in the service. They make same-origin `fetch` calls with the device token from
`localStorage`, under a strict CSP, with no third-party resources.

1. In the Companion: **Key Vault → Hosts → Pair a phone…** asks which VM's T3 Code to log in to,
   mints a T3 pairing link for it the way **Open T3 Code** does (`T3Code.BuildPairingScript`; the
   link is single-use and valid for 10 minutes), calls `POST /vault/devices`, and shows a QR code
   (rendered natively by the Companion) for
   `<vault web base>/vault/pair#token=<token>&next=<url-encoded T3 pairing link>`. Both tokens sit
   in the fragment, so neither reaches a server log. Without a VM choice the QR pairs approvals only.
2. The phone opens the pairing page, stores the token, checks it with `GET /vault/device`, and
   forwards to `next`, which logs it into T3 Code.
3. The page at `/vault/` lists pending approvals with Approve / Deny and pending file decisions,
   and refreshes every 3 s.

`<vault web base>` defaults to `https://<PublicHost>:<service port>` and can be overridden with
the host setting `Constructd:VaultWebUrl` (for a reverse proxy with a public certificate). Both
host installers create a self-signed certificate, so a phone shows a warning once for that origin,
as it already does for T3 Code's VM-local CA. The pages and `/vault/device` are the only routes that
accept the device scheme; the pages themselves are anonymous static files.

## Companion changes

- The PC vault document gains `K`, per-entry `updatedAt`/`updatedBy`, tombstones, and per-host
  settings (`mode`).
- Sync engine per enrolled host (above). Local (non-hosted) VMs keep the SSH spool path, so
  the `VaultBroker` runs only for instances without a host service.
- Unlock: after the Companion starts a hosted VM, and whenever a hosted VM of a locked host is
  seen online, `POST /vms/{name}/vault/unlock` (idempotent).
- Approvals from hosts are shown in the same dialog as local ones; scrub file decisions in the
  same grid.
- The Key Vault window gains a **Hosts** tab: sync state, mode, paired devices (pair/revoke),
  "Show vault key" for a second PC, and "Import vault key".

## Delivery

1. Host vault and guest hosted mode: store, crypto, unlock, requests, leases, approvals through
   the Companion, sync, guest scrubs.
2. Phone: device tokens, pages, QR code in the Companion.
3. Optional: a T3 Code banner that links to the approval page.
