#!/usr/bin/env python3
"""construct-agent-sign-in.py -- a coding agent's own sign-in without a terminal (design 11.8, S34).

The Construct engine starts it over SSH, as root, when the user clicks Sign in on one of the VM's
agents. It runs that agent's sign-in command under a pseudo-terminal and reports what the terminal
shows as JSON lines on stdout; each line on stdin is input from the user, which goes to the terminal.

  python3 /opt/construct/repo/bin/construct-agent-sign-in.py --session ID AGENT
  python3 /opt/construct/repo/bin/construct-agent-sign-in.py --stop --session ID
  python3 /opt/construct/repo/bin/construct-agent-sign-in.py --status

AGENT is claude-code, codex, opencode or antigravity. The sign-in commands:

  claude-code   claude auth login            a link, then "Paste code here"
  codex         codex login --device-auth    a link and a one-time code to enter there
  opencode      opencode auth login          OpenCode's own list of integrations, then its sign-in
  antigravity   agy acp --login              a link, then the authorization code to paste

Antigravity keeps the terminal's sign-in in ~/.gemini/antigravity-cli/antigravity-oauth-token and
T3 Code gives it a private home (~/.t3/userdata/providers/antigravity/<sha256 of the instance id>,
the CLI started with --gemini-dir there and --app-data-dir antigravity-acp). Both read the same
token file format, so after a sign-in the token is copied into T3 Code's home as well: one sign-in
serves both.

stdin, one JSON object per line:
  {"text": "..."}          typed into the terminal, then Enter (empty: Enter alone)
  {"key": "up" | "down"}   an arrow key, for a list the sign-in shows
End of stdin ends the session: the sign-in and everything it started are stopped.

stdout, one JSON object per line:
  {"type": "start", "agent": "codex"}
  {"type": "screen", "text": "..."}   the terminal's text now, whole, as the CLI wrote it
  {"type": "end", "outcome": "done" | "failed" | "stopped", "text": "...",
   "signedIn": true | false, "version": "1.2.3"}   (the last two when known)

--status prints one JSON object: each installed agent, by the ids its version report uses (claude,
codex, opencode, antigravity), and whether it is signed in, by the cheapest check its CLI has. An
agent whose check did not answer is left out.

Nothing is logged here: the output can carry one-time codes.

Exit status: 0 when the sign-in ended well, 3 when it did not, 2 for a usage error, 127 when the
agent is not installed.
"""
import codecs
import errno
import fcntl
import hashlib
import json
import os
import pty
import re
import select
import shutil
import signal
import stat
import struct
import subprocess
import sys
import termios
import time

RUN_DIR = os.environ.get("CONSTRUCT_SIGN_IN_RUN_DIR", "/run/construct/sign-in")
T3_HOME = os.environ.get("T3CODE_HOME", os.path.join(os.path.expanduser("~"), ".t3"))
# A sign-in nobody finishes ends by itself.
TIMEOUT = float(os.environ.get("CONSTRUCT_SIGN_IN_TIMEOUT_SEC", "1200"))
# Wide enough that no CLI wraps a sign-in link; the text is kept without padding.
ROWS, COLS = 50, 400
MAX_TEXT = 32 * 1024
# Input waiting for a CLI that does not read; more is dropped.
MAX_PENDING = 64 * 1024

AGENTS = {
    "claude-code": ("claude", "claude"),
    "codex": ("codex", "codex"),
    "opencode": ("opencode", "opencode"),
    "antigravity": ("agy", "antigravity"),
}
KEYS = {"up": b"\x1b[A", "down": b"\x1b[B"}
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]+)?")


def emit(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


# ---- The terminal: just enough of one to read what a sign-in shows, lists that redraw included.

class Term:
    """Lines of text and a cursor. Colours and modes are dropped; cursor moves and erases are kept,
    so a list that redraws itself reads as it looks. No line wraps (the terminal is wide)."""

    MAX_LINES = 400

    def __init__(self, rows):
        self.rows = rows
        self.lines = [[]]
        self.r = self.c = 0
        self.saved = (0, 0)
        self.state = "text"
        self.seq = ""

    def text(self):
        out = "\n".join("".join(line).rstrip() for line in self.lines).strip("\n")
        return out[-MAX_TEXT:]

    def _top(self):
        return max(0, len(self.lines) - self.rows)

    def _row(self, r):
        r = max(self._top(), r)
        while len(self.lines) <= r:
            self.lines.append([])
        self.r = r
        if len(self.lines) > self.MAX_LINES:
            drop = len(self.lines) - self.MAX_LINES
            del self.lines[:drop]
            self.r -= drop

    def _put(self, ch):
        line = self.lines[self.r]
        if len(line) < self.c:
            line.extend(" " * (self.c - len(line)))
        if self.c < len(line):
            line[self.c] = ch
        else:
            line.append(ch)
        self.c += 1

    def feed(self, data):
        for ch in data:
            self._char(ch)

    def _char(self, ch):
        state = self.state
        if state == "text":
            if ch == "\x1b":
                self.state = "esc"
            elif ch == "\r":
                self.c = 0
            elif ch in "\n\x0b\x0c":
                self._row(self.r + 1)
            elif ch == "\b":
                self.c = max(0, self.c - 1)
            elif ch == "\t":
                self.c = (self.c // 8 + 1) * 8
            elif ch >= " " and ch != "\x7f":
                self._put(ch)
        elif state == "esc":
            self.state = "text"
            if ch == "[":
                self.state, self.seq = "csi", ""
            elif ch == "]":
                self.state = "osc"
            elif ch in "P_^X":
                self.state = "str"
            elif ch in "()*+-./#%":
                self.state = "skip1"
            elif ch == "7":
                self.saved = (self.r, self.c)
            elif ch == "8":
                self._row(self.saved[0])
                self.c = self.saved[1]
            elif ch == "M":
                self._row(self.r - 1)
            elif ch in "DE":
                self._row(self.r + 1)
                if ch == "E":
                    self.c = 0
            elif ch == "c":
                self.lines, self.r, self.c = [[]], 0, 0
        elif state == "skip1":
            self.state = "text"
        elif state in ("osc", "str"):
            # Ends with BEL or ST (ESC \); an OSC 8 link keeps its visible text, which is the link.
            if ch == "\x07":
                self.state = "text"
            elif ch == "\x1b":
                self.state = state + "-esc"
        elif state in ("osc-esc", "str-esc"):
            self.state = "text" if ch == "\\" else state[:-4]
        elif state == "csi":
            if "\x40" <= ch <= "\x7e":
                self.state = "text"
                self._csi(self.seq, ch)
            elif len(self.seq) < 64:
                self.seq += ch

    def _csi(self, seq, final):
        if seq[:1] in ("?", ">", "<", "=") or "$" in seq or " " in seq:
            return
        parts = [int(p) if p.isdigit() else 0 for p in seq.split(";")] if seq else []
        n = parts[0] if parts and parts[0] > 0 else 1
        line = self.lines[self.r]
        if final == "A":
            self._row(self.r - n)
        elif final in "Be":
            self._row(min(self.r + n, self._top() + self.rows - 1))
        elif final in "Ca":
            self.c += n
        elif final == "D":
            self.c = max(0, self.c - n)
        elif final == "E":
            self._row(self.r + n)
            self.c = 0
        elif final == "F":
            self._row(self.r - n)
            self.c = 0
        elif final in "G`":
            self.c = n - 1
        elif final == "d":
            self._row(self._top() + n - 1)
        elif final in "Hf":
            row = parts[0] if parts and parts[0] > 0 else 1
            col = parts[1] if len(parts) > 1 and parts[1] > 0 else 1
            self._row(self._top() + row - 1)
            self.c = col - 1
        elif final == "J":
            mode = parts[0] if parts else 0
            if mode == 0:
                del line[self.c:]
                del self.lines[self.r + 1:]
            elif mode == 1:
                top = self._top()
                for i in range(top, self.r):
                    self.lines[i] = []
                line[:self.c + 1] = [" "] * min(len(line), self.c + 1)
            else:
                top = self._top()
                self.lines[top:] = [[] for _ in range(self.r - top + 1)]
        elif final == "K":
            mode = parts[0] if parts else 0
            if mode == 0:
                del line[self.c:]
            elif mode == 1:
                line[:self.c + 1] = [" "] * min(len(line), self.c + 1)
            else:
                line.clear()
        elif final == "P":
            del line[self.c:self.c + n]
        elif final == "X":
            line[self.c:self.c + n] = [" "] * len(line[self.c:self.c + n])
        elif final == "s":
            self.saved = (self.r, self.c)
        elif final == "u":
            self._row(self.saved[0])
            self.c = self.saved[1]


# ---- The agents.

def opencode_v2():
    try:
        out = subprocess.run(["opencode", "--version"], capture_output=True, text=True, timeout=20, stdin=subprocess.DEVNULL).stdout
    except (OSError, subprocess.SubprocessError):
        return True
    match = VERSION.search(out)
    return not match or not match.group(0).startswith("1.")


def sign_in_command(agent):
    """The agent's own sign-in: argv and extra environment."""
    if agent == "claude-code":
        return ["claude", "auth", "login"], {}
    if agent == "codex":
        return ["codex", "login", "--device-auth"], {}
    if agent == "opencode":
        # OpenCode 2 would otherwise start its per-user background service for this.
        return ["opencode", "auth", "login"] + (["--standalone"] if opencode_v2() else []), {}
    # The terminal's sign-in; file storage, since a VM has no keyring (the CLI reads the file then).
    return ["agy", "acp", "--login"], {"AGY_ACP_FORCE_FILE_STORAGE": "1"}


def home():
    return os.path.expanduser("~")


def antigravity_token():
    return os.path.join(home(), ".gemini", "antigravity-cli", "antigravity-oauth-token")


T3_ANTIGRAVITY = ("userdata", "providers", "antigravity")


def t3_antigravity_homes():
    """T3 Code's Antigravity homes, by name under T3_ANTIGRAVITY: the one of Construct's provider instance and
    any other T3 Code made (real directories only, never a symlink)."""
    base = os.path.join(T3_HOME, *T3_ANTIGRAVITY)
    homes = {hashlib.sha256(b"antigravity").hexdigest()}
    try:
        with os.scandir(base) as entries:
            homes.update(e.name for e in entries if e.is_dir(follow_symlinks=False))
    except OSError:
        pass
    return sorted(homes)


def open_dir(base, parts, create=False):
    """A descriptor of base/parts..., each part opened without following a symlink (one fails the walk);
    with create, missing parts are made private. base is a path or a directory's descriptor."""
    fd = os.open(base, os.O_RDONLY | os.O_DIRECTORY) if isinstance(base, str) else os.dup(base)
    try:
        for part in parts:
            if create:
                try:
                    os.mkdir(part, 0o700, dir_fd=fd)
                except FileExistsError:
                    pass
            inner = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = inner
        return fd
    except BaseException:
        os.close(fd)
        raise


def share_antigravity_sign_in():
    """Copies the terminal's Antigravity token into T3 Code's homes, as T3 Code lays them out. Neither the
    token nor a directory on its way, on either side, may be a symlink."""
    try:
        src = open_dir(home(), [".gemini", "antigravity-cli"])
    except FileNotFoundError:
        return False
    try:
        fd = os.open("antigravity-oauth-token", os.O_RDONLY | os.O_NOFOLLOW, dir_fd=src)
    except FileNotFoundError:
        return False
    finally:
        os.close(src)
    with os.fdopen(fd, "rb") as f:
        if not stat.S_ISREG(os.fstat(f.fileno()).st_mode):
            return False
        token = f.read()
    if not token:
        return False
    for profile in t3_antigravity_homes():
        profile_fd = open_dir(T3_HOME, T3_ANTIGRAVITY + (profile,), create=True)
        try:
            os.fchmod(profile_fd, 0o700)
            target = open_dir(profile_fd, ["antigravity-acp"], create=True)
        finally:
            os.close(profile_fd)
        try:
            os.fchmod(target, 0o700)
            temporary = "antigravity-oauth-token.construct"
            try:
                os.unlink(temporary, dir_fd=target)
            except FileNotFoundError:
                pass
            fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=target)
            with os.fdopen(fd, "wb") as f:
                f.write(token)
            os.replace(temporary, "antigravity-oauth-token", src_dir_fd=target, dst_dir_fd=target)
        finally:
            os.close(target)
    return True


def run_status(argv, timeout=20):
    """The exit status of a status command, or None when it did not answer."""
    try:
        return subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              timeout=timeout, text=True)
    except (OSError, subprocess.SubprocessError):
        return None


def signed_in(tool):
    """Whether the agent is signed in; None when its check did not answer."""
    if tool == "claude":
        r = run_status(["claude", "auth", "status"])
        return None if r is None or r.returncode not in (0, 1) else r.returncode == 0
    if tool == "codex":
        r = run_status(["codex", "login", "status"])
        return None if r is None or r.returncode not in (0, 1) else r.returncode == 0
    if tool == "opencode":
        if opencode_v2():
            r = run_status(["opencode", "auth", "list", "--standalone", "--format", "json"], 60)
            if r is None or r.returncode != 0:
                return None
            try:
                listed = json.loads(r.stdout)
            except ValueError:
                return None
            return isinstance(listed, list) and len(listed) > 0
        data = os.environ.get("XDG_DATA_HOME") or os.path.join(home(), ".local", "share")
        try:
            with open(os.path.join(data, "opencode", "auth.json")) as f:
                stored = json.load(f)
        except FileNotFoundError:
            return False
        except (OSError, ValueError):
            return None
        return isinstance(stored, dict) and len(stored) > 0
    if tool == "antigravity":
        token = antigravity_token()
        return os.path.isfile(token) and os.path.getsize(token) > 0
    return None


def version_of(command):
    r = run_status([command, "--version"])
    match = VERSION.search(r.stdout) if r is not None and r.stdout else None
    return match.group(0)[:60] if match else None


def status():
    out = {}
    for command, tool in AGENTS.values():
        if shutil.which(command):
            state = signed_in(tool)
            if state is not None:
                out[tool] = state
    print(json.dumps(out))
    return 0


# ---- Sessions.

# Exit statuses of our own children that stop_group reaped while it waited.
reaped = {}


def reap(pid):
    """The child's exit code once it has exited (reaping it), else None. Not our child: None."""
    if pid in reaped:
        return reaped[pid]
    try:
        done, wait_status = os.waitpid(pid, os.WNOHANG)
    except ChildProcessError:
        return None
    if not done:
        return None
    reaped[pid] = os.waitstatus_to_exitcode(wait_status)
    return reaped[pid]


def stop_group(pgid, wait=5.0):
    """A hang-up to the group (the terminal went away), TERM to what stays, then KILL whatever is left."""
    if pgid <= 1:
        return
    start = time.monotonic()
    for sig, until in ((signal.SIGHUP, start + wait / 3), (signal.SIGTERM, start + wait), (signal.SIGKILL, None)):
        try:
            os.killpg(pgid, sig)
        except (ProcessLookupError, PermissionError):
            return
        while until is not None and time.monotonic() < until:
            # Our own child stays in its group as a zombie until it is reaped.
            reap(pgid)
            try:
                os.killpg(pgid, 0)
            except ProcessLookupError:
                return
            time.sleep(0.1)


def pid_file(session):
    return os.path.join(RUN_DIR, session + ".pid")


def stop_session(session):
    path = pid_file(session)
    try:
        with open(path) as f:
            pgid = int(f.read().strip() or "0")
    except (OSError, ValueError):
        return
    try:
        os.unlink(path)
    except OSError:
        pass
    stop_group(pgid)


def stop_others(session):
    """A sign-in cut off by an engine restart may still run: only one runs per VM."""
    try:
        names = os.listdir(RUN_DIR)
    except OSError:
        return
    for name in names:
        if name.endswith(".pid") and name != session + ".pid":
            stop_session(name[:-4])


class Input:
    """The engine's lines on stdin, read without blocking."""

    def __init__(self):
        self.buf = b""
        self.closed = False

    def lines(self):
        try:
            chunk = os.read(0, 65536)
        except OSError:
            chunk = b""
        if not chunk:
            self.closed = True
            return []
        self.buf += chunk
        *done, self.buf = self.buf.split(b"\n")
        if len(self.buf) > 65536:
            self.buf = b""
        return done


def keystrokes(raw):
    """What one input line types: its text and Enter, or one key. None for a line that is neither."""
    try:
        line = json.loads(raw.decode("utf-8", "replace"))
    except ValueError:
        return None
    if not isinstance(line, dict):
        return None
    if isinstance(line.get("key"), str):
        return KEYS.get(line["key"])
    text = line.get("text")
    if not isinstance(text, str) or len(text) > 16384:
        return None
    # Typed text only: no control characters that would steer the terminal.
    text = "".join(ch for ch in text if ch >= " " and ch != "\x7f")
    return text.encode("utf-8") + b"\r"


def run(agent, session):
    command, tool = AGENTS[agent]
    if not shutil.which(command):
        emit({"type": "end", "outcome": "failed", "text": f"{command} is not installed on this VM."})
        return 127
    argv, extra = sign_in_command(agent)

    os.makedirs(RUN_DIR, mode=0o700, exist_ok=True)
    stop_others(session)
    emit({"type": "start", "agent": agent})

    pid, fd = pty.fork()
    if pid == 0:
        env = dict(os.environ, TERM="xterm-256color", COLUMNS=str(COLS), LINES=str(ROWS), **extra)
        try:
            os.execvpe(argv[0], argv, env)
        except OSError as error:
            os.write(2, f"{argv[0]}: {error.strerror}\r\n".encode())
        os._exit(127)

    # The child leads its own session, so its process group is its pid.
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    # Input goes to the terminal as the CLI reads it, through the loop: a CLI that reads nothing never
    # blocks the loop, so its output, the end of stdin and the deadline are still served.
    os.set_blocking(fd, False)
    with open(pid_file(session), "w") as f:
        f.write(str(pid))

    term = Term(ROWS)
    decoder = codecs.getincrementaldecoder("utf-8")("replace")
    stdin = Input()
    pending = bytearray()
    deadline = time.monotonic() + TIMEOUT
    last_out = last_emit = 0.0
    shown = ""
    outcome = None
    note = ""

    def show(force=False):
        nonlocal shown, last_emit
        text = term.text()
        if text != shown or force:
            shown = text
            emit({"type": "screen", "text": text})
        last_emit = time.monotonic()

    try:
        while True:
            now = time.monotonic()
            if now >= deadline:
                outcome, note = "stopped", "The sign-in took too long and was stopped."
                deadline = float("inf")
                stop_group(pid)
            watch = [fd] + ([] if stdin.closed else [0])
            try:
                ready, writable, _ = select.select(watch, [fd] if pending else [], [], 0.1)
            except InterruptedError:
                ready = writable = []
            if fd in ready:
                try:
                    data = os.read(fd, 65536)
                except BlockingIOError:
                    data = None
                except OSError as error:
                    if error.errno != errno.EIO:
                        raise
                    data = b""
                if data is not None:
                    if not data:
                        break
                    term.feed(decoder.decode(data))
                    last_out = time.monotonic()
            if fd in writable:
                try:
                    del pending[:os.write(fd, pending)]
                except (BlockingIOError, InterruptedError):
                    pass
                except OSError:
                    pending.clear()
            if 0 in ready:
                for raw in stdin.lines():
                    keys = keystrokes(raw)
                    if keys and len(pending) + len(keys) <= MAX_PENDING:
                        pending += keys
                if stdin.closed:
                    outcome = outcome or "stopped"
                    stop_group(pid)
            now = time.monotonic()
            # Shown when the output pauses, and at least twice a second while it streams.
            if term.text() != shown and (now - last_out >= 0.1 or now - last_emit >= 0.5):
                show()
            # Over when the sign-in exited, even if something it started still holds the terminal.
            if not ready and reap(pid) is not None:
                break
    finally:
        # However this ends (stdin closed, a signal, an error), the sign-in and what it started go.
        stop_group(pid, wait=2.0)
        try:
            os.unlink(pid_file(session))
        except OSError:
            pass

    term.feed(decoder.decode(b"", final=True))
    code = reap(pid)
    if code is None:
        try:
            code = os.waitstatus_to_exitcode(os.waitpid(pid, 0)[1])
        except ChildProcessError:
            code = -1
    os.close(fd)
    show()

    end = {"type": "end"}
    if outcome is None:
        # Ended by a signal (a --stop): stopped, not failed.
        outcome = "done" if code == 0 else "stopped" if code < 0 else "failed"
    if outcome == "done" and agent == "antigravity":
        try:
            share_antigravity_sign_in()
        except OSError as error:
            note = f"T3 Code's Antigravity sign-in could not be written: {error.strerror}"
    state = signed_in(tool)
    if state is not None:
        end["signedIn"] = state
    version = version_of(command)
    if version:
        end["version"] = version
    end.update(outcome=outcome, text=note)
    emit(end)
    return 0 if outcome == "done" else 3


def usage():
    print("Usage: construct-agent-sign-in.py --session ID AGENT | --stop --session ID | --status", file=sys.stderr)
    return 2


def main(args):
    session = agent = None
    stop = False
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--status" and len(args) == 1:
            return status()
        if a == "--session" and i + 1 < len(args):
            session = args[i + 1]
            i += 2
            continue
        if a == "--stop":
            stop = True
        elif a in AGENTS and agent is None:
            agent = a
        else:
            return usage()
        i += 1
    if not session or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", session):
        return usage()
    if stop:
        stop_session(session)
        return 0
    if agent is None:
        return usage()
    # Ended by a signal: the sign-in goes too.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    signal.signal(signal.SIGHUP, lambda *_: sys.exit(129))
    return run(agent, session)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
