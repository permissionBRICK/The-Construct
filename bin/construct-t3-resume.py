#!/usr/bin/env python3
"""Resume the T3 Code threads that a reprovision or reinstall interrupted.

A reprovision restarts the T3 Code server, and a reinstall replaces the whole VM.
Either one ends the turn a thread was running and kills the background work it had
started (Monitor watchers, background shells, waiters, dev servers). T3 forgets that
background work on restart, so the busy threads are recorded before anything stops
and told what happened once provisioning is done.

  snapshot [--reason reprovision|reinstall] [--out FILE]
      Record the threads that are busy right now: a turn is running, or background
      agents or watchers are alive (T3's backgroundLiveness). Threads that wait for
      an approval or an answer are left out. Merges into FILE (default: the pending
      file), keeping entries an earlier run left behind. A T3 server that does not
      answer only produces a warning: provisioning goes on either way.
  import FILE
      Merge a snapshot taken on another VM (the reinstall backup) into the pending
      file. Its threads count as interrupted.
  arm [--reboot-follows]
      Run when a provisioning run ends. Starts the resume service, or holds the
      resume for the next boot when the host reboots the VM after this run
      (--reboot-follows) or the VM needs a reboot (/var/run/reboot-required).
  resume [--now]
      What construct-t3-resume.service runs. Waits until no provisioning run is
      active and the T3 API answers, then sends each interrupted thread a message.
      --now ignores a hold for the next boot.
  status
      Print the pending file.

Uses T3's HTTP orchestration API (GET /api/orchestration/shell, POST
/api/orchestration/dispatch) on 127.0.0.1 with a bearer session minted through
`t3 auth session issue`.
"""

import argparse
import contextlib
import datetime
import fcntl
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

PENDING_FILE = os.environ.get("CONSTRUCT_T3_RESUME_FILE", "/var/lib/construct/t3-resume.json")
TOKEN_FILE = os.environ.get("CONSTRUCT_T3_RESUME_TOKEN_FILE", "/etc/construct/t3-resume-token")
CONFIG_FILE = os.environ.get("CONFIG_FILE", "/etc/construct/config.env")
PROVISION_MARKER = os.environ.get("CONSTRUCT_PROVISION_MARKER", "/run/construct/provisioning")
REBOOT_REQUIRED_FILE = os.environ.get("CONSTRUCT_REBOOT_REQUIRED_FILE", "/var/run/reboot-required")
BOOT_ID_FILE = os.environ.get("CONSTRUCT_BOOT_ID_FILE", "/proc/sys/kernel/random/boot_id")
UNIT = "construct-t3-resume.service"
T3_UNIT = "t3code-serve"

# How long `resume` waits for a running provision to finish and for the T3 API to
# answer, and how long T3 must have been up before messages go out (its own
# startup reconciliation of interrupted sessions runs first).
PROVISION_WAIT_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_PROVISION_WAIT", "3600"))
API_WAIT_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_API_WAIT", "600"))
# A snapshot right after the VM booted can meet a T3 server that is still starting.
SNAPSHOT_WAIT_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_SNAPSHOT_WAIT", "60"))
# Older entries are dropped instead of resumed: a backup reused for a later reinstall,
# or a VM that stayed off, must not tell a thread to continue work from days ago.
MAX_AGE_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_MAX_AGE_HOURS", "24")) * 3600
SETTLE_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_SETTLE", "20"))
POLL_SEC = float(os.environ.get("CONSTRUCT_T3_RESUME_POLL", "2"))
DISPATCH_ATTEMPTS = 3

BUSY_SESSION = {"starting", "running"}
BUSY_TURN = {"pending", "running"}
BACKGROUND = {"working", "monitoring"}


def log(*parts):
    print("construct-t3-resume:", *parts, flush=True)


def warn(*parts):
    print("construct-t3-resume:", *parts, file=sys.stderr, flush=True)


class ApiError(Exception):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


class Unsupported(ApiError):
    """The T3 server has no v1 HTTP orchestration API (the orchestration-v2 builds)."""


class NotJson(ApiError):
    """T3 serves its web app with HTTP 200 for paths it has no route for."""


# ── Machine state ─────────────────────────────────────────────────────────────

def read_config():
    values = {}
    try:
        with open(CONFIG_FILE, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip("'\"")
    except OSError:
        pass
    return values


def boot_id():
    try:
        with open(BOOT_ID_FILE, encoding="utf-8") as handle:
            return handle.read().strip()
    except OSError:
        return ""


def systemctl_show(prop):
    try:
        result = subprocess.run(["systemctl", "show", "-p", prop, "--value", T3_UNIT],
                                capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.SubprocessError):
        return ""
    return result.stdout.strip() if result.returncode == 0 else ""


def t3_enabled():
    """False when the T3 server is switched off (T3CODE=false) or not installed."""
    try:
        return subprocess.run(["systemctl", "is-enabled", "--quiet", T3_UNIT], timeout=15).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def t3_invocation():
    """systemd's id for the current run of the T3 server; it changes on every restart."""
    return systemctl_show("InvocationID")


def t3_uptime():
    """Seconds since the T3 server last started, or None when systemd cannot say."""
    raw = systemctl_show("ActiveEnterTimestampMonotonic")
    if not raw.isdigit() or raw == "0":
        return None
    return time.monotonic() - int(raw) / 1_000_000


def provisioning_active():
    """True while a provision.sh run holds the marker and its process still exists."""
    try:
        with open(PROVISION_MARKER, encoding="utf-8") as handle:
            pid = handle.read().strip()
    except OSError:
        return False
    if not pid.isdigit():
        return True
    return os.path.exists(f"/proc/{pid}")


def now_iso():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def parse_iso(value):
    """An aware datetime, or None for anything that is not an ISO timestamp."""
    if not value:
        return None
    try:
        parsed = datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=datetime.timezone.utc)


# ── Pending file ──────────────────────────────────────────────────────────────

@contextlib.contextmanager
def locked(path):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path + ".lock", "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def load(path):
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except FileNotFoundError:
        return None
    except (OSError, ValueError) as error:
        warn(f"ignoring unreadable {path}: {error}")
        return None
    if not isinstance(data, dict) or not isinstance(data.get("threads"), dict):
        warn(f"ignoring {path}: not a resume snapshot")
        return None
    return data


def save(path, data):
    if not data.get("threads"):
        with contextlib.suppress(FileNotFoundError):
            os.unlink(path)
        return
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    tmp = f"{path}.{os.getpid()}.tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2)
        handle.write("\n")
    os.replace(tmp, path)


def empty_snapshot():
    return {"version": 1, "resumeAfterBoot": None, "threads": {}}


def merge(into, entries):
    """Add entries for threads not recorded yet. An existing entry wins: it is older,
    and the interruption it recorded still happened."""
    added = 0
    for thread_id, entry in entries.items():
        if thread_id not in into["threads"]:
            into["threads"][thread_id] = entry
            added += 1
    return added


# ── T3 API ────────────────────────────────────────────────────────────────────

def api_base():
    override = os.environ.get("CONSTRUCT_T3_API")
    if override:
        return override.rstrip("/")
    port = read_config().get("T3CODE_PORT") or "5177"
    return f"http://127.0.0.1:{port}"


def mint_token():
    try:
        result = subprocess.run(
            ["t3", "auth", "session", "issue", "--ttl", "365d", "--token-only",
             "--label", "construct-t3-resume", "--log-level", "none"],
            capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError) as error:
        raise ApiError(f"could not mint a T3 API session: {error}") from error
    token = result.stdout.strip()
    if result.returncode != 0 or not token:
        raise ApiError(f"could not mint a T3 API session: {(result.stderr or result.stdout).strip()[:200]}")
    os.makedirs(os.path.dirname(TOKEN_FILE) or ".", exist_ok=True)
    fd = os.open(TOKEN_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as handle:
        handle.write(token + "\n")
    return token


def read_token():
    try:
        with open(TOKEN_FILE, encoding="utf-8") as handle:
            token = handle.read().strip()
        if token:
            return token
    except OSError:
        pass
    return mint_token()


def api(method, path, body=None, retry_auth=True):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(api_base() + path, data=data, method=method, headers={
        "Authorization": "Bearer " + read_token(),
        "Content-Type": "application/json",
    })
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        # A reinstall replaces T3's database, and with it every session issued before.
        if error.code == 401 and retry_auth:
            mint_token()
            return api(method, path, body, retry_auth=False)
        detail = error.read().decode(errors="replace")[:200]
        raise ApiError(f"{method} {path} -> HTTP {error.code} {detail}".strip(), error.code) from error
    except (urllib.error.URLError, OSError) as error:
        raise ApiError(f"{method} {path} -> {getattr(error, 'reason', error)}") from error
    if not raw.strip():
        return None
    try:
        return json.loads(raw)
    except ValueError as error:
        raise NotJson(f"{method} {path} -> the reply is not JSON") from error


UNSUPPORTED = ("this T3 server does not offer the HTTP orchestration API "
               "(GET /api/orchestration/shell, POST /api/orchestration/dispatch) that the resume uses")


def shell():
    try:
        snapshot = api("GET", "/api/orchestration/shell")
    except ApiError as error:
        # orchestration-v2 servers answer 400 here (they require a protocol header), 404,
        # or T3's web app when the route is gone.
        if isinstance(error, NotJson) or error.status in (400, 404):
            raise Unsupported(f"{UNSUPPORTED}: {error}", error.status) from error
        raise
    threads = snapshot.get("threads") if isinstance(snapshot, dict) else None
    if not isinstance(threads, list) or any(
            not isinstance(t, dict) or "id" not in t or "session" not in t or "latestTurn" not in t
            for t in threads):
        raise Unsupported(f"{UNSUPPORTED}: its thread list has an unknown shape")
    return snapshot


# ── Snapshot ──────────────────────────────────────────────────────────────────

def classify(thread):
    """'running' (a turn is active), 'working'/'monitoring' (only background work is
    alive), or None for a thread that is idle, done or waiting on the user."""
    if thread.get("archivedAt") or thread.get("deletedAt"):
        return None
    if thread.get("hasPendingApprovals") or thread.get("hasPendingUserInput"):
        return None
    session = thread.get("session") or {}
    turn = thread.get("latestTurn") or {}
    if session.get("status") in BUSY_SESSION or turn.get("state") in BUSY_TURN:
        return "running"
    if thread.get("backgroundLiveness") in BACKGROUND:
        return thread["backgroundLiveness"]
    return None


def take_snapshot(reason):
    data = None
    deadline = time.monotonic() + SNAPSHOT_WAIT_SEC
    while data is None:
        try:
            data = shell()
        except Unsupported:
            raise
        except ApiError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(POLL_SEC)
    taken_at = now_iso()
    boot = boot_id()
    invocation = t3_invocation()
    entries = {}
    for thread in data["threads"]:
        state = classify(thread)
        if state is None:
            continue
        entries[thread["id"]] = {
            "title": thread.get("title") or "",
            "reason": reason,
            "state": state,
            "background": thread.get("backgroundLiveness") if thread.get("backgroundLiveness") in BACKGROUND else None,
            "takenAt": taken_at,
            "bootId": boot,
            "t3InvocationId": invocation,
            "interrupted": reason == "reinstall",
        }
    return entries


def cmd_snapshot(args):
    path = args.out or PENDING_FILE
    try:
        entries = take_snapshot(args.reason)
    except Unsupported as error:
        log(f"{error}; busy threads are not recorded")
        return 0
    except ApiError as error:
        warn(f"WARNING: could not read the T3 Code threads ({error}); "
             "threads this run interrupts will not be resumed")
        return 0
    # --out names a private file (the export's staging tree, which is packed whole),
    # so only the shared pending file takes the lock.
    with (contextlib.nullcontext() if args.out else locked(path)):
        data = load(path) or empty_snapshot()
        added = merge(data, entries)
        save(path, data)
    if entries:
        log(f"{len(entries)} busy T3 Code thread(s) recorded ({added} new): "
            + ", ".join(f"{e['title'] or tid} [{e['state']}]" for tid, e in entries.items()))
    else:
        log("no T3 Code thread is busy")
    return 0


def cmd_import(args):
    incoming = load(args.file)
    if not incoming or not incoming["threads"]:
        log("the imported snapshot lists no threads")
        return 0
    for entry in incoming["threads"].values():
        entry["interrupted"] = True
    with locked(PENDING_FILE):
        data = load(PENDING_FILE) or empty_snapshot()
        added = merge(data, incoming["threads"])
        save(PENDING_FILE, data)
    log(f"{added} T3 Code thread(s) from the backup will be resumed once provisioning is done")
    return 0


# ── Arm ───────────────────────────────────────────────────────────────────────

def was_interrupted(entry, boot, invocation):
    """A thread lost its turn and background processes when the VM was replaced or
    rebooted, or when the T3 server restarted since the snapshot."""
    if entry.get("interrupted"):
        return True
    if not boot or entry.get("bootId") != boot:
        return True
    return not invocation or entry.get("t3InvocationId") != invocation


def cmd_arm(args):
    boot = boot_id()
    with locked(PENDING_FILE):
        data = load(PENDING_FILE)
        if not data or not data["threads"]:
            return 0
        count = len(data["threads"])
        if not t3_enabled():
            save(PENDING_FILE, empty_snapshot())
            log(f"T3 Code is switched off; {count} recorded thread(s) are not resumed")
            return 0
        if args.reboot_follows or os.path.exists(REBOOT_REQUIRED_FILE):
            data["resumeAfterBoot"] = boot
            save(PENDING_FILE, data)
            log(f"{count} T3 Code thread(s) will be resumed after the reboot that ends this provision")
            return 0
        data["resumeAfterBoot"] = None
        invocation = t3_invocation()
        kept = {tid: e for tid, e in data["threads"].items() if was_interrupted(e, boot, invocation)}
        if not kept:
            save(PENDING_FILE, empty_snapshot())
            log("T3 Code kept running through this provision; no thread was interrupted")
            return 0
        data["threads"] = kept
        save(PENDING_FILE, data)
    result = subprocess.run(["systemctl", "start", "--no-block", UNIT], capture_output=True, text=True)
    if result.returncode != 0:
        warn(f"could not start {UNIT}: {result.stderr.strip()}")
        return 1
    log(f"resuming {len(kept)} interrupted T3 Code thread(s) in the background (journalctl -u {UNIT})")
    return 0


# ── Resume ────────────────────────────────────────────────────────────────────

DOING = {
    "running": "working on a turn",
    "working": "running background agents",
    "monitoring": "watching background tasks",
}


def compose_message(entry, rebooted, worktree_note):
    doing = DOING.get(entry.get("state"), "working")
    if entry.get("reason") == "reinstall":
        text = (f"[Construct reinstall] This VM was reinstalled while this thread was {doing}. "
                "The Construct rebuilt the VM, restored its saved configuration and cloned the "
                "repositories again. Every process and watcher this thread had running is gone, and "
                "local changes that were not pushed before the reinstall are lost.")
    else:
        cause = "The reboot" if rebooted else "Restarting the T3 Code server"
        text = (f"[Construct reprovision] This VM was reprovisioned{' and rebooted' if rebooted else ''} "
                f"while this thread was {doing}. {cause} ended that work and stopped every process "
                "this thread had started: Monitor watchers, background shells, waiters, dev servers "
                "and other child processes.")
    if worktree_note:
        text += " " + worktree_note
    return text + (" Check which of your processes and watchers are no longer running, restart the "
                   "ones you still need, then continue where you left off.")


def ensure_cwd(thread, projects):
    """Return (ok, note). A reinstall leaves T3's git worktrees behind, so a missing
    worktree is recreated from the thread's branch before the thread can run again."""
    project = projects.get(thread.get("projectId")) or {}
    root = project.get("workspaceRoot")
    worktree = thread.get("worktreePath")
    if not worktree:
        if root and not os.path.isdir(root):
            return False, f"its project folder {root} does not exist"
        return True, None
    if os.path.isdir(worktree):
        return True, None
    branch = thread.get("branch")
    if not (root and branch and os.path.isdir(root)):
        return False, f"its worktree {worktree} is gone and cannot be recreated"
    subprocess.run(["git", "-C", root, "worktree", "prune"], capture_output=True)
    result = subprocess.run(["git", "-C", root, "worktree", "add", worktree, branch],
                            capture_output=True, text=True)
    if result.returncode != 0:
        return False, (f"its worktree {worktree} is gone and branch {branch} could not be checked "
                       f"out again: {result.stderr.strip()[:200]}")
    return True, f"Its git worktree {worktree} was missing and has been recreated from branch {branch}."


def dispatch_resume(thread, text):
    command = {
        "type": "thread.turn.start",
        "commandId": str(uuid.uuid4()),
        "threadId": thread["id"],
        "message": {"messageId": str(uuid.uuid4()), "role": "user", "text": text, "attachments": []},
        "runtimeMode": thread.get("runtimeMode") or "approval-required",
        "interactionMode": thread.get("interactionMode") or "default",
        "createdAt": now_iso(),
    }
    if thread.get("modelSelection"):
        command["modelSelection"] = thread["modelSelection"]
    for attempt in range(1, DISPATCH_ATTEMPTS + 1):
        try:
            api("POST", "/api/orchestration/dispatch", command)
            return None
        except ApiError as error:
            if attempt == DISPATCH_ATTEMPTS:
                return str(error)
            time.sleep(POLL_SEC * attempt)
    return None


def skip_reason(entry, thread, boot, invocation):
    if thread is None or thread.get("archivedAt") or thread.get("deletedAt"):
        return "the thread no longer exists or was archived"
    if thread.get("hasPendingApprovals") or thread.get("hasPendingUserInput"):
        return "it is waiting for an approval or an answer"
    taken_at = parse_iso(entry.get("takenAt"))
    if taken_at is None:
        return "its snapshot entry has no valid time"
    if (datetime.datetime.now(datetime.timezone.utc) - taken_at).total_seconds() > MAX_AGE_SEC:
        return f"the snapshot is older than {MAX_AGE_SEC / 3600:g} hours"
    latest_user = parse_iso(thread.get("latestUserMessageAt"))
    if latest_user and latest_user > taken_at:
        return "someone sent it a message after the snapshot"
    if not was_interrupted(entry, boot, invocation):
        return "T3 Code kept running, so it was not interrupted"
    return None


def wait_until(predicate, timeout, what):
    deadline = time.monotonic() + timeout
    announced = False
    while True:
        if predicate():
            return True
        if time.monotonic() >= deadline:
            return False
        if not announced:
            log(f"waiting for {what}")
            announced = True
        time.sleep(POLL_SEC)


def api_ready():
    try:
        shell()
    except Unsupported:
        pass
    except ApiError:
        return False
    uptime = t3_uptime()
    return uptime is None or uptime >= SETTLE_SEC


def cmd_resume(args):
    boot = boot_id()
    data = load(PENDING_FILE)
    if not data or not data["threads"]:
        return 0
    if not args.now and data.get("resumeAfterBoot") and data["resumeAfterBoot"] == boot:
        log("the resume waits for the next boot")
        return 0
    if not t3_enabled():
        with locked(PENDING_FILE):
            save(PENDING_FILE, empty_snapshot())
        log(f"T3 Code is switched off; {len(data['threads'])} recorded thread(s) are not resumed")
        return 0
    if not wait_until(lambda: not provisioning_active(), PROVISION_WAIT_SEC, "the running provision to finish"):
        log("a provision is still running; its end starts the resume again")
        return 0
    if not wait_until(api_ready, API_WAIT_SEC, "the T3 Code API"):
        warn("the T3 Code API did not answer; the snapshot stays for the next attempt")
        return 1

    with locked(PENDING_FILE):
        if provisioning_active():
            log("a new provision started; its end starts the resume again")
            return 0
        data = load(PENDING_FILE)
        if not data or not data["threads"]:
            return 0
        # A provision that ended while this run waited may have held the resume for its reboot.
        if not args.now and data.get("resumeAfterBoot") and data["resumeAfterBoot"] == boot:
            log("the resume now waits for the next boot")
            return 0
        save(PENDING_FILE, empty_snapshot())

    try:
        current = shell()
    except Unsupported as error:
        warn(f"{error}; {len(data['threads'])} thread(s) not resumed")
        return 1
    except ApiError as error:
        warn(f"could not read the T3 threads ({error}); restoring the snapshot")
        with locked(PENDING_FILE):
            pending = load(PENDING_FILE) or empty_snapshot()
            merge(pending, data["threads"])
            save(PENDING_FILE, pending)
        return 1
    threads = {t["id"]: t for t in current["threads"]}
    projects = {p.get("id"): p for p in current.get("projects") or [] if isinstance(p, dict)}
    invocation = t3_invocation()
    resumed, failed, retry = 0, 0, {}
    for thread_id, entry in data["threads"].items():
        label = f"{entry.get('title') or thread_id} ({thread_id})"
        try:
            thread = threads.get(thread_id)
            reason = skip_reason(entry, thread, boot, invocation)
            if reason:
                log(f"skipping {label}: {reason}")
                continue
            ok, note = ensure_cwd(thread, projects)
            if not ok:
                warn(f"cannot resume {label}: {note}")
                failed += 1
                continue
            rebooted = entry.get("bootId") != boot
            error = dispatch_resume(thread, compose_message(entry, rebooted, note))
        except Exception as unexpected:  # one bad entry must not cost the others their resume
            error = f"{type(unexpected).__name__}: {unexpected}"
        if error:
            warn(f"could not resume {label}: {error}")
            failed += 1
            retry[thread_id] = entry
        else:
            log(f"resumed {label}")
            resumed += 1
    if retry:
        # Tried again on the next start of the service (the next provision's end or the
        # next boot), until the entries pass the age limit.
        with locked(PENDING_FILE):
            pending = load(PENDING_FILE) or empty_snapshot()
            merge(pending, retry)
            save(PENDING_FILE, pending)
    log(f"done: {resumed} resumed, {failed} failed, {len(data['threads']) - resumed - failed} skipped")
    return 1 if failed else 0


def cmd_status(_args):
    data = load(PENDING_FILE)
    if not data or not data["threads"]:
        print("No T3 Code threads are waiting to be resumed.")
        return 0
    if data.get("resumeAfterBoot"):
        hold = "after the next boot" if data["resumeAfterBoot"] == boot_id() else "on the next resume run"
    else:
        hold = "at the end of the next provision or on the next boot"
    print(f"{len(data['threads'])} T3 Code thread(s) will be resumed {hold}:")
    for thread_id, entry in data["threads"].items():
        print(f"  {thread_id}  {entry.get('state')}  {entry.get('reason')}  {entry.get('takenAt')}  {entry.get('title')}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(prog="construct-t3-resume", description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    snapshot = sub.add_parser("snapshot")
    snapshot.add_argument("--reason", choices=["reprovision", "reinstall"], default="reprovision")
    snapshot.add_argument("--out")
    importer = sub.add_parser("import")
    importer.add_argument("file")
    arm = sub.add_parser("arm")
    arm.add_argument("--reboot-follows", action="store_true")
    resume = sub.add_parser("resume")
    resume.add_argument("--now", action="store_true")
    sub.add_parser("status")
    args = parser.parse_args(argv)
    handler = {"snapshot": cmd_snapshot, "import": cmd_import, "arm": cmd_arm,
               "resume": cmd_resume, "status": cmd_status}[args.command]
    try:
        return handler(args)
    except ApiError as error:
        warn(str(error))
        return 1


if __name__ == "__main__":
    sys.exit(main())
