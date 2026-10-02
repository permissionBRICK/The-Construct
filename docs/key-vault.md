# Key vault — secrets agents may use only with your approval

The key vault keeps tokens, passwords, keys and username/password pairs on **your PC**, inside
Construct Companion. Agents in a VM can see which secrets exist and what they are for, but they
read a value only after you approve it in a Companion dialog: for a number of uses, for a time,
or both. When the access ends, the Companion searches that VM for every copy of the value. Agent
logs are redacted automatically. Any other file is listed for you, and you decide per file.

- [Managing secrets](#managing-secrets)
- [What agents do](#what-agents-do)
- [Approvals and leases](#approvals-and-leases)
- [Scrubbing a VM](#scrubbing-a-vm)
- [Security model](#security-model)
- [Wire contract](#wire-contract)

## Managing secrets

Open **Key Vault** from the Companion tray menu (or run `ConstructCompanion.exe --vault`, or open
`construct://vault`). Toasts from the vault open it too.

| Tab | What it shows |
|---|---|
| **Secrets** | Name, username, description and how many VMs hold access. **Add…**, **Edit…**, **Delete**, **Copy secret** (cleared from the clipboard after 30 seconds unless something replaced it) and **Copy username**. |
| **Access** | Every active lease: VM, secret, uses left, end time, the agent's reason. **Revoke access** ends a lease at once. Below it, scrubs that wait for their VM to come online; **Discard pending scrubs for VM** forgets them (for a VM you removed). |
| **Activity** | What the vault did: approvals, expiries, scrubs and their results, warnings. |

A name uses letters, digits, `.`, `_` and `-` (up to 64 characters). Descriptions are what an
agent sees in `construct secret list`, so say what the secret is for. Values may span several
lines (SSH keys, certificates); **Load from file…** reads one, and a multi-line value is only
editable while **Show** is ticked. Values are limited to 32 KiB.

Editing the description or username keeps existing access. A new value or a rename ends every
lease on the old value, and those VMs are scrubbed of it.

The vault is stored in `%LOCALAPPDATA%\The-Construct-Vault\vault.dat`, encrypted with Windows
DPAPI for your Windows account. It is not synced to other PCs and is not part of the config
sync. If that file cannot be decrypted, for example after copying a profile to another PC,
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

A request opens a dialog on top of your other windows. It names the VM, each secret and its
description, the access asked for, the agent's reason and the requesting user. **Deny** has the
focus, and **Approve** becomes clickable after a second, so a keystroke meant for another window
cannot approve it. The agent waits up to ten minutes by default (`--wait`). When it gives up,
the dialog closes and counts as denied. Dialogs from several VMs queue one at a time.

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
multi-line value is searched line by line, without the shared PEM header lines. Values shorter
than six characters are not searched; the activity list says so.

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

## Security model

The vault keeps secrets off a VM until you approve, limits how long and how often a VM may read
them, and removes the copies that agents usually leave behind. It does not make an approved
secret safe from the agent that received it: agents run as root, so during a lease they can copy
the value anywhere, including off the VM. Approve only what the task needs, prefer short leases
and few uses, and revoke anything you did not expect.

- Requests come from inside the VM and are treated as untrusted. Names, sizes and every field
  are validated, and reasons and descriptions are shown as quoted text.
- Approval happens only on your PC. Nothing inside the VM can approve a request.
- Values cross the VM boundary only on SSH stdin streams and never appear in command lines or
  environment variables. On the VM they touch only the spool on tmpfs (a request from `add`, a
  response the CLI deletes as soon as it reads it, the scan's pattern files), never the disk.
  Unclaimed responses are deleted after two minutes.
- The Companion never logs values, and they never reach the webview panels or the local HTTP API.

Hosted VMs (a remote Hyper-V or Proxmox host) work the same way while the Companion runs on your
PC, because it reaches them over SSH. Approvals from another device, such as a phone, are not
available yet.

## Wire contract

`construct secret` writes one request per file into the VM's spool `/run/construct/vault`
(tmpfs, mode 0700). The Companion holds one SSH watch per online VM that claims requests (atomic
rename), answers each request, and writes the response back into the spool.

```
requests/<id>.json    {"v":1,"id","ts","op","names":[…],"uses","ttl","all","replace","reason",
                       "description","username","secret":<base64>,"deadline":<ms>,"source"}
responses/<id>.json   {"v":1,"id","status":"ok|denied|notFound|exists|invalid|error","message",
                       "secret":<base64>,"username","lease":{"usesLeft","expiresAt"},"names":[…],"items":[…]}
```

The CLI waits 15 seconds (`CONSTRUCT_VAULT_PICKUP_SEC`) for the request to be claimed, then until
its deadline for the answer. The host-run guest scripts live in
`companion/src/Construct.Companion.Core/Vault/GuestScripts/`: `vault-watch.sh` (claim loop),
`vault-respond.sh` (publish a response), `vault-scan.sh` (find files) and `vault-clean.sh`
(redact or delete). Their stdin is the only channel that carries values.
