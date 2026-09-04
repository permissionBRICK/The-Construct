"use strict";
// CLIENT PORT FORWARDER — the extension half of `construct expose` (plan §4.6, wire
// format in docs/expose.md, module design in extension/ARCHITECTURE.md §Forwards).
//
// An agent on the VM runs `construct expose 5173` and needs ONE link to hand the user.
// The port it names lives inside the VM; the link points at the user's PC, because this
// module opened a local port here and tunnelled it over the SSH connection the extension
// already knows how to make. Then it writes the ack the CLI is waiting for.
//
// Built to the §4.8 client-tool-module rules from day one — this is the worked example
// the older modules (audio.js, notify.js) are meant to converge on:
//
//   1. NO VS CODE API, and no child_process/net either. Every effect goes through the
//      injected transport, so the whole local and remote flow unit-tests in plain node.
//      The ONE thing pulled out of `net` is `isIP` — a pure parser, no socket — because
//      the host-label rule has to know a real IPv6 literal from a plausible-looking string
//      (sanitizeHostLabel); instances.js reads the same function for the same reason.
//   2. TRANSPORT INJECTED, NEVER OWNED:
//        runRemoteScript(script, opts) -> Promise<{code, stdout, stderr}>
//        spawnWatch(script)            -> child          (long-lived SSH stream)
//        spawnTunnel({localPort,vmPort,bindHost}) -> child  (long-lived `ssh -L`)
//        probePort(port, bindHost)     -> Promise<bool>  (free on THIS PC, there?)
//        fetchJson(method, path, body) -> Promise<any>   (remote mode only)
//      A `child` is anything with `stdout`, `on("exit"|"error")` and `kill()`.
//   3. OWN STATE, KEYED BY INSTANCE. One Forwarder per instance name; its spool lives on
//      that VM, its tunnel table and its ownership claim are its own. It reads no other
//      module's files or globals.
//   4. A DOCUMENTED CONTRACT. The spool documents and the service's forward routes are the
//      wire protocol (docs/expose.md, service/README.md) — changing one is a versioned
//      decision. This file is their client and never their author.
//
// LAZY, AND GATED ON THE GUEST. Nothing here is started by activation: a Forwarder is
// created only for a VM the window has already established is up (forwarder.planLifecycle,
// wired in extension.js), and its own first act is one cheap capability check
// (buildCapabilityScript) — a guest provisioned before `construct expose` existed costs
// exactly that one exec and never sees a watcher.
//
// TWO MODES, ONE PLANNER. Local (hyperv-local, no service) watches the guest spool over
// SSH; remote (hyperv-remote) polls the host service. Both funnel into the same pure
// `planActions`, so "what should happen" is decided in one tested place and the class only
// executes it.

// `net.isIP` only — a real IPv6 parser for the host-label rule, no socket (see rule 1).
const net = require("net");

// ── The contract's constants ─────────────────────────────────────────────────

/** The guest spool (docs/expose.md). Under /etc, not /run: a forward request must
 *  survive a VM reboot, because the dev server is usually restarted with it. */
const SPOOL_DIR = "/etc/construct/forwards";

/** Every spool document carries this. Bump = a versioned decision, not a tweak. */
const WIRE_VERSION = 1;

/** Ownership record inside the spool — see buildReconcileScript for the protocol. */
const OWNER_FILE = ".owner";
/** The mutex guarding a claim transaction. A DIRECTORY, because `mkdir` is the atomic
 *  create-or-fail primitive every POSIX sh has. */
const OWNER_LOCK_DIR = ".owner.lock";
/** A claim older than this is a dead window's; three missed reconciles. */
const OWNER_TTL_SEC = 90;
/** A lock older than this belonged to a window that died mid-transaction. The transaction
 *  is three filesystem operations, so a live lock is never remotely this old. */
const CLAIM_LOCK_TTL_SEC = 60;

/** The reconcile poll. Also the ownership heartbeat, and the whole fallback for a VM
 *  with no inotify-tools. */
const RECONCILE_MS = 30000;
/** Remote mode's list poll, while a window has the instance active. */
const REMOTE_POLL_MS = 10000;
/** Coalesce a burst of spool events into one reconcile. */
const EVENT_DEBOUNCE_MS = 250;

/** Local ports to fall back on when the VM's own port number is taken on this PC. The
 *  mirror of the mic tunnel's 8767–8774, for the opposite direction. */
const PORT_BASE = 18800;
const PORT_COUNT = 16;
/**
 * THE FALLBACK RANGE IS SLICED PER INSTANCE (B14, plan §4.12 "Smaller items folded in").
 *
 * 18800–18815 used to be shared by every VM: two instances forwarding the same VM port
 * from two windows raced for the same 16 numbers, and the loser got a port a user had
 * already been handed for the OTHER machine. Each instance now owns its own 16-port
 * slice inside 18800–19311 (32 slices):
 *
 *   slice 0 (18800–18815) — RESERVED for the default instance, so a single-VM install
 *                            probes exactly the ports it always probed.
 *   slice 1–31            — chosen by an FNV-1a hash of the instance name, so the same
 *                            VM lands on the same slice in every window and after every
 *                            restart without anything being written down.
 *
 * Two different names CAN hash to one slice (32 slices, no coordination). That costs
 * nothing: the candidate list is probed, ports this instance already holds are skipped,
 * and a busy port simply moves to the next candidate — the same thing that happens today
 * when the VM's own port number is taken.
 */
const PORT_SLICE_COUNT = 32;

/** An `ssh -L` that dies within this window never opened the port: that is a failure to
 *  report, not a restart to schedule (same rule, and same number, as audio.js). */
const TUNNEL_SETTLE_MS = 1200;
/** Restart backoff for a tunnel that dies later: quick first retry, doubling to a minute. */
const RECONNECT_BASE_MS = 2000;
const RECONNECT_MAX_MS = 60000;
/** A tunnel that lived this long was healthy: the next drop starts backing off afresh. */
const CONNECTION_HEALTHY_MS = 60000;
/** Consecutive failed (re)starts before the guest is told the forward is broken. */
const MAX_TUNNEL_ATTEMPTS = 5;

/** The watcher's own pacing. `#` is its heartbeat: a write into a dead pipe is what
 *  reaps an orphaned watcher on the VM (SIGPIPE), exactly as in notify.js. */
const WATCH_FALLBACK_SECONDS = 30;
const WATCH_HEARTBEAT_SECONDS = 60;
const HEARTBEAT_LINE = "#";
const CHANGED_LINE = "CHANGED";

/** The capability check's two answers (buildCapabilityScript): does this guest have the
 *  spool contract at all? */
const SPOOL_YES = "SPOOL=1";
const SPOOL_NO = "SPOOL=0";

/**
 * WHAT `start()` RESOLVES TO — the guest's answer, handed back to the window that armed
 * this session, because the two "no" answers mean opposite things for the armed edge:
 *
 *   `supported`   — serving (a `SPOOL=1` guest, or remote mode, which has no spool).
 *   `unsupported` — a guest provisioned before `construct expose` existed. It answered,
 *                   and the answer will not change until it is reprovisioned: stay armed,
 *                   so it is asked once per connection and not every 30 s.
 *   `unanswered`  — the check could not be made at all (the exec failed, or exited
 *                   non-zero). NOTHING was established, so the window must let this
 *                   session go and let the next reachable reading try again — the bug this
 *                   value exists to close is a transient failure arming the instance
 *                   forever, which silently swallows every later `construct expose`.
 *   `stood-down`  — the window withdrew the session while it was in flight (eligible()
 *                   went false, or dispose() ran). The stop path owns the cleanup.
 *   `running`     — start() was called on a session that is already started.
 */
const START_SUPPORTED = "supported";
const START_UNSUPPORTED = "unsupported";
const START_UNANSWERED = "unanswered";
const START_STOOD_DOWN = "stood-down";
const START_RUNNING = "running";

/**
 * Where a forward listens on the user's PC.
 *
 * Loopback is the default and is stated EXPLICITLY in the ssh argv, so the privacy of a
 * default forward does not depend on ssh's configuration. `hostLabel` is the opt-in: it
 * advertises this PC by name, which per docs/expose.md is "a name other machines use" — so
 * the forward has to actually bind all interfaces, or the link it advertises is dead. One
 * setting, one consistent meaning: no label ⇒ private to this PC; a label ⇒ reachable at
 * that name.
 */
const BIND_LOOPBACK = "127.0.0.1";
const BIND_ALL = "0.0.0.0";

/** Caps. A host label lands in a URL the CLI prints; a message lands on its stderr. */
const MAX_HOST_LABEL = 200;
const MAX_MESSAGE = 300;
/** A label is written by an agent on the VM and rendered in the panel. */
const MAX_LABEL = 100;

// ── Pure helpers ─────────────────────────────────────────────────────────────

/**
 * Is this a forward id we are willing to touch? It ends up BOTH in a spool file name and
 * in a URL path, so it is held to one harmless component — the same guard
 * `bin/construct-expose.sh` (`is_safe_id`) and `construct project` apply. Pure.
 */
function isSafeId(id) {
  const s = String(id == null ? "" : id);
  if (!s || s.length > 128) return false;
  if (s.indexOf("..") >= 0) return false;
  return /^[A-Za-z0-9._-]+$/.test(s);
}

/**
 * Strip control characters, collapse whitespace, trim and cap. Everything crossing a
 * trust boundary goes through this: a label authored by an agent on the VM before it is
 * rendered, and the two strings we write into a document the CLI prints. A newline in any
 * of them would let one field forge another line of output. Pure.
 */
function sanitizeText(value, max) {
  const limit = Math.max(1, Number(max) || MAX_MESSAGE);
  const out = String(value == null ? "" : value)
    .replace(/[\u0000-\u001F\u007F\u2028\u2029]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  return out.length > limit ? out.slice(0, limit).trim() : out;
}

/**
 * A host label for the ack: one host-name-shaped token, or "" for the default (a
 * loopback-only tunnel, which is what an untouched install produces). Anything with a
 * character that has no business in a URL authority is refused rather than escaped —
 * the setting is a machine name, and a half-encoded one would print a link nobody can
 * open. Pure.
 *
 * THE CANONICAL WIRE FORM OF AN IPv6 LABEL IS THE BARE LITERAL — `fe80::1`, never
 * `[fe80::1]` (docs/expose.md). One representation is the whole point: this value crosses
 * to a guest CLI that prints a link and to a service that builds one, so anything that
 * accepted both spellings without normalizing gave `[[fe80::1]]` on one path and
 * `fe80::1:5173` on the other. A bracketed value is therefore ACCEPTED and unwrapped here
 * (a user typing what they see in a browser is not an error), and the brackets are put
 * back exactly once, by urlHostFor, at the moment a URL is built.
 *
 * The literal is PARSED (`net.isIP`), not character-classed: `::::`, `1::2::3` and
 * `1:2:3:4:5:6:7:8:9` all pass a character class and none of them is an address. A ZONE ID
 * (`fe80::1%eth0`) is refused — `%` never was in this rule's alphabet, a URL would need it
 * as `%25`, and a zone is meaningful only on the machine that owns that interface, which is
 * the opposite of what a label advertising this PC to others is for.
 */
function sanitizeHostLabel(value) {
  const raw = sanitizeText(value, MAX_HOST_LABEL).replace(/\s+/g, "");
  if (!raw) return "";
  const bare = raw.length > 2 && raw.charAt(0) === "[" && raw.charAt(raw.length - 1) === "]"
    ? raw.slice(1, -1)
    : raw;
  if (!bare) return "";
  // The zone id goes before the parser, not after it: node's `isIP` accepts `fe80::1%eth0`
  // and .NET's `IPAddress.TryParse` does not, so agreeing on a wire form means deciding it
  // here rather than inheriting whichever parser happens to be reading.
  if (bare.indexOf("%") >= 0) return "";
  if (bare.indexOf(":") >= 0) return net.isIP(bare) === 6 ? bare : "";
  return /^[A-Za-z0-9._-]+$/.test(bare) ? bare : "";
}

/**
 * THE HOST AS IT GOES INTO A URL — the one rule shared by this module, the guest CLI's
 * `url_host` (bin/construct-expose.sh) and the service's response builder: normalize to
 * the canonical bare form, then add exactly one bracket pair for an IPv6 literal and
 * nothing for anything else. "" when the label is unusable, so the caller falls back to
 * `localhost` the way an absent label does. Pure.
 */
function urlHostFor(value) {
  const bare = sanitizeHostLabel(value);
  if (!bare) return "";
  return net.isIP(bare) === 6 ? `[${bare}]` : bare;
}

/**
 * Which address a forward must listen on, given the configured host label. Pure, and the
 * ONE place the rule lives — the ssh argv, the port probe and the rendered link all read
 * it, so they cannot disagree about what "open" means.
 */
function bindHostFor(hostLabel) {
  return sanitizeHostLabel(hostLabel) ? BIND_ALL : BIND_LOOPBACK;
}

/** A port as a safe integer in [1,65535], or null. Accepts a numeric string (a value that
 *  came back over JSON). Pure. */
function toPort(value) {
  let v = value;
  if (typeof v === "string" && /^[0-9]{1,5}$/.test(v)) v = Number(v);
  if (typeof v !== "number" || !Number.isInteger(v) || v < 1 || v > 65535) return null;
  return v;
}

/**
 * The local ports to try for a forward, in preference order:
 *
 *   1. `opts.prefer` — a port an ack has ALREADY promised the guest. Re-establishing a
 *      tunnel on the same number keeps the link the agent printed working, which is worth
 *      more than tidiness.
 *   2. the VM's own port number, so the common case prints exactly what was asked for.
 *   3. the fallback range.
 *
 * Anything this instance's other tunnels already hold is skipped.
 *
 * Pure: it returns candidates, and the caller probes them. That split is what makes the
 * policy testable without a socket.
 */
function portCandidates(vmPort, opts = {}) {
  const base = toPort(opts.base) || PORT_BASE;
  const count = Number.isInteger(opts.count) && opts.count > 0 && opts.count <= 64 ? opts.count : PORT_COUNT;
  const taken = new Set((opts.taken || []).map((p) => toPort(p)).filter((p) => p !== null));
  const out = [];
  const add = (p) => {
    if (p === null || taken.has(p) || out.indexOf(p) >= 0) return;
    out.push(p);
  };
  add(toPort(opts.prefer));
  add(toPort(vmPort));
  for (let i = 0; i < count; i++) {
    const p = base + i;
    if (p > 65535) break;
    add(p);
  }
  return out;
}

/**
 * The 16-port fallback slice this instance owns. Pure and deterministic: the default
 * instance always gets the historical [18800, 18816) and every other name gets one of
 * the 31 slices above it. See PORT_SLICE_COUNT for why collisions are harmless.
 */
function instancePortSlice(name) {
  const n = String(name == null ? "" : name).trim();
  // "agent-vm" spelled out, as everywhere else in this module (see the constructor):
  // forwarder.js deliberately depends on nothing but node:net.
  if (!n || n === "agent-vm") return { base: PORT_BASE, count: PORT_COUNT };
  // FNV-1a over the name's code units: a few lines, no dependency, and stable across
  // Node versions (a JS hash of an object's iteration order would not be).
  let h = 0x811c9dc5;
  for (let i = 0; i < n.length; i++) {
    h ^= n.charCodeAt(i) & 0xff;
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  const slot = 1 + (h % (PORT_SLICE_COUNT - 1));
  return { base: PORT_BASE + slot * PORT_COUNT, count: PORT_COUNT };
}

/** Reconnect backoff, 2s doubling to 60s. Pure. */
function reconnectDelayMs(attempt) {
  const n = Number(attempt);
  const step = Number.isFinite(n) && n > 0 ? Math.min(Math.floor(n), 10) : 1;
  return Math.min(RECONNECT_MAX_MS, RECONNECT_BASE_MS * Math.pow(2, step - 1));
}

/**
 * Split a stream chunk into complete lines, keeping the trailing partial for the next
 * chunk. Heartbeats and blanks are dropped here so callers only see real events. `rest`
 * is capped: a peer that streams megabytes without a newline must not grow host memory.
 * Pure.
 */
function splitLines(buffered, chunk) {
  const MAX_REST = 8 * 1024;
  const all = String(buffered == null ? "" : buffered) + String(chunk == null ? "" : chunk);
  const parts = all.split("\n");
  let rest = parts.pop();
  if (rest.length > MAX_REST) rest = "";
  const lines = parts.map((l) => l.trim()).filter((l) => l && l !== HEARTBEAT_LINE);
  return { lines, rest };
}

/** A single-quoted POSIX shell literal. Pure. */
function shQuote(value) {
  return "'" + String(value == null ? "" : value).replace(/'/g, "'\\''") + "'";
}

// ── Spool documents ──────────────────────────────────────────────────────────

/**
 * Is this a v1 spool document that agrees with the file it was found in?
 *
 * `"v": 1` is MANDATORY on every spool document (docs/expose.md: "Every document carries
 * `v: 1`", and "changing a shape here is a versioned decision"). Treating a `v: 2`
 * document as v1 is precisely the mistake the version field exists to prevent — a future
 * writer could give `localPort` a different meaning, and we would open a port for it.
 * A missing `v` is equally not-v1: the CLI has always written it, so its absence means the
 * document was not written by something that speaks this protocol.
 *
 * The id is MANDATORY and must equal the file name. `docs/expose.md` defines `id` in all
 * three document shapes, so a document without one is not a v1 document — it is a
 * partially written or hand-made file, and the spool's whole atomicity story (write to a
 * temp name, publish with `mv`) exists so that a reader never sees one. A body that claims
 * a DIFFERENT id is worse than odd: the file name is the identity used everywhere (the ack
 * is written to `acks/<id>.json`, `--close` names it, the panel closes it), so acting on
 * either reading could ack — or tear down — the wrong forward.
 *
 * Deliberately LOCAL-ONLY. The service's forward list is a different contract with no `v`
 * at all, and `readForwardList` stays lenient by design. Pure.
 */
function isV1Document(id, doc) {
  if (!doc || typeof doc !== "object") return false;
  if (doc.v !== WIRE_VERSION) return false;
  if (typeof doc.id !== "string" || doc.id !== String(id)) return false;
  return true;
}

/**
 * Parse a request document from the LOCAL spool. Anything shaped wrong — a foreign wire
 * version, a document that disagrees with its own file name, a bad port — is dropped
 * rather than guessed at: a corrupt or future file must not be acted on, and must not stop
 * the rest of the spool from being served. Pure.
 */
function parseRequest(id, doc) {
  if (!isSafeId(id)) return null;
  if (!isV1Document(id, doc)) return null;
  const vmPort = toPort(doc.vmPort);
  if (vmPort === null) return null;
  // `target` is always "client" in the spool (host targets are stateless there), but an
  // explicit other value is honoured as "not ours" rather than assumed away.
  const target = String(doc.target == null || doc.target === "" ? "client" : doc.target).toLowerCase();
  if (target !== "client") return null;
  return { id, vmPort, label: sanitizeText(doc.label, MAX_LABEL), target };
}

/** Parse an ack document from the LOCAL spool (ours, from an earlier run or another
 *  window). Same version/id discipline as parseRequest. Pure. */
function parseAck(id, doc) {
  if (!isSafeId(id)) return null;
  if (!isV1Document(id, doc)) return null;
  const status = String(doc.status == null ? "" : doc.status).toLowerCase();
  if (status !== "open" && status !== "error") return null;
  return {
    id,
    status,
    localPort: toPort(doc.localPort),
    hostLabel: sanitizeHostLabel(doc.hostLabel),
    message: sanitizeText(doc.message, MAX_MESSAGE),
  };
}

/**
 * Parse a close document from the LOCAL spool. Only its identity is load-bearing, but the
 * version and the id still have to agree — a v2 close document may not mean "close this".
 *
 * Being strict here is safe rather than risky, in both directions: `construct expose
 * --close` removes the request and the ack itself, so a close document we decline to read
 * still leaves a tunnel with no request behind it, which the planner closes anyway — while
 * an INCOMPLETE close document that we did read would tear down the tunnel its file name
 * happens to select. Pure.
 */
function parseClose(id, doc) {
  if (!isSafeId(id)) return null;
  if (!isV1Document(id, doc)) return null;
  return id;
}

/** The ack document to write, in the exact shape docs/expose.md specifies. `hostLabel` is
 *  OMITTED when empty — the CLI then prints a loopback link, which is the default. Pure. */
function ackDocument(id, ack) {
  const doc = { v: WIRE_VERSION, id: String(id) };
  doc.status = ack.status === "error" ? "error" : "open";
  if (ack.localPort != null) doc.localPort = ack.localPort;
  const label = sanitizeHostLabel(ack.hostLabel);
  if (label) doc.hostLabel = label;
  doc.message = sanitizeText(ack.message, MAX_MESSAGE);
  return doc;
}

/**
 * Parse the reconcile script's output: an `OWNER=` line plus one base64 line per document.
 * Unknown line kinds, unsafe ids and undecodable payloads are skipped. Pure — the base64
 * decoder is injected so this stays testable in any runtime.
 */
function parseDump(stdout, decodeBase64) {
  const decode = decodeBase64 || ((b) => Buffer.from(String(b), "base64").toString("utf8"));
  const out = { owner: "unknown", requests: [], acks: [], closes: [] };
  for (const raw of String(stdout == null ? "" : stdout).split("\n")) {
    const line = raw.trim();
    if (!line) continue;
    if (line.startsWith("OWNER=")) {
      const who = line.slice("OWNER=".length).trim();
      out.owner = who === "self" || who === "other" || who === "absent" ? who : "unknown";
      continue;
    }
    const parts = line.split(" ");
    if (parts.length !== 3) continue;
    const [kind, id, payload] = parts;
    if (!isSafeId(id)) continue;
    let doc;
    try { doc = JSON.parse(decode(payload)); } catch (_) { continue; }
    if (kind === "R") {
      const req = parseRequest(id, doc);
      if (req) out.requests.push(req);
    } else if (kind === "A") {
      const ack = parseAck(id, doc);
      if (ack) out.acks.push(ack);
    } else if (kind === "C") {
      const close = parseClose(id, doc);
      if (close) out.closes.push(close);
    }
  }
  return out;
}

// ── Spool scripts ────────────────────────────────────────────────────────────

/**
 * Claim ownership and dump the spool, in ONE round trip.
 *
 * THE CLAIM. Several VS Code windows can watch one VM and each would see the same
 * request; they must not each open a tunnel and each overwrite the ack. So exactly one
 * window owns an instance's spool.
 *
 * Mutual exclusion comes from `mkdir "$d/.owner.lock"`, which either creates the
 * directory or fails, atomically. Write-then-read-back is NOT enough on its own and was
 * the bug here: with A-write, A-read, B-write, B-read both windows read their own id and
 * both believe they own the spool. So the lease transaction (read the record, decide,
 * replace it) happens inside the lock, and the record is then read once more OUTSIDE it —
 * so a window that could not take the lock, or could not write the file, reports whatever
 * is actually there rather than what it hoped for.
 *
 * A window claims only when the record is missing, already its own, or older than the TTL
 * (i.e. a window that is gone). A lock left by a window that died mid-transaction is
 * removed once it is older than `CLAIM_LOCK_TTL_SEC`; the transaction it guards is three
 * filesystem operations, so a live lock is never that old.
 *
 * A missing spool directory answers `OWNER=absent`, which is not an error but a full stop:
 * that VM was provisioned before this feature existed, so it has no `construct expose` to
 * serve either, and the forwarder stands down instead of polling it forever. `provision.sh`
 * creates the three directories on every provision, so reprovisioning is what enables it.
 * Pure.
 */
function buildReconcileScript(opts = {}) {
  const dir = shQuote(opts.dir || SPOOL_DIR);
  const me = shQuote(String(opts.windowId || "").replace(/[^A-Za-z0-9._-]/g, ""));
  const ttl = Number.isInteger(opts.ttlSeconds) && opts.ttlSeconds > 0 ? opts.ttlSeconds : OWNER_TTL_SEC;
  const lockTtl = Number.isInteger(opts.lockTtlSeconds) && opts.lockTtlSeconds > 0
    ? opts.lockTtlSeconds : CLAIM_LOCK_TTL_SEC;
  return `set -u
d=${dir}
me=${me}
ttl=${ttl}
lockttl=${lockTtl}
now=$(date +%s 2>/dev/null || echo 0)
if [ ! -d "$d" ]; then printf 'OWNER=absent\\n'; exit 0; fi
own="$d/${OWNER_FILE}"
lock="$d/${OWNER_LOCK_DIR}"

read_owner() {
  cur=""
  if [ -f "$own" ]; then cur=$(head -c 200 "$own" 2>/dev/null | tr -d '\\r\\n' || true); fi
  who=\${cur%% *}
  ts=\${cur#* }
  case "$ts" in ''|*[!0-9]*) ts=0 ;; esac
}

# The lease transaction runs inside a real mutex. \`mkdir\` is the primitive: it either
# creates the directory or fails, atomically, with no window in between — unlike a
# write-then-read-back, where two windows can each read their own value and both believe
# they won (A writes, A reads, B writes, B reads).
#
# A lock left behind by a window that died mid-transaction is removed once it is older
# than lockttl; that is safe because the transaction it guards is three filesystem
# operations long, so a live lock is never old.
claim() {
  if ! mkdir "$lock" 2>/dev/null; then
    if [ -n "$(find "$lock" -maxdepth 0 -mmin +$((lockttl / 60 + 1)) 2>/dev/null)" ]; then
      rmdir "$lock" 2>/dev/null || true
      mkdir "$lock" 2>/dev/null || return 1
    else
      # Somebody else is claiming right now. Report what the record says and try again on
      # the next reconcile — never guess, and never two owners.
      return 1
    fi
  fi
  read_owner
  if [ -z "$who" ] || [ "$who" = "$me" ] || [ $((now - ts)) -ge "$ttl" ]; then
    tmp="$d/${OWNER_FILE}.tmp.$$"
    if printf '%s %s\\n' "$me" "$now" >"$tmp" 2>/dev/null; then
      chmod 0644 "$tmp" 2>/dev/null || true
      mv -f "$tmp" "$own" 2>/dev/null || rm -f "$tmp"
    fi
  fi
  rmdir "$lock" 2>/dev/null || true
  return 0
}

claim || true
# The record is the single source of truth, read AFTER the transaction: a window that
# could not take the lock, or could not write the file, reports what is actually there.
read_owner
if [ "$who" = "$me" ]; then printf 'OWNER=self\\n'; else printf 'OWNER=other\\n'; fi
dump() {
  kind="$1"; sub="$2"
  [ -d "$d/$sub" ] || return 0
  for f in "$d/$sub"/*.json; do
    [ -e "$f" ] || continue
    id=$(basename "$f" .json)
    case "$id" in .*) continue ;; esac
    b=$(head -c 4096 "$f" 2>/dev/null | base64 2>/dev/null | tr -d '\\n') || continue
    [ -n "$b" ] || continue
    printf '%s %s %s\\n' "$kind" "$id" "$b"
  done
}
dump R requests
dump A acks
dump C close
`;
}

/**
 * Publish an ack. The document rides as base64 so no quoting layer can touch it, and it
 * is published with a rename — the CLI polls this file and must never read a half-written
 * one (the same guarantee its own writes give us). An existing ack is overwritten, which
 * docs/expose.md explicitly asks for: the extension re-acks after re-establishing a
 * tunnel. Pure.
 */
function buildAckScript(id, doc, opts = {}) {
  if (!isSafeId(id)) throw new Error(`refusing to write an ack for an unusable id: ${id}`);
  const dir = shQuote((opts.dir || SPOOL_DIR) + "/acks");
  const b64 = Buffer.from(JSON.stringify(doc) + "\n", "utf8").toString("base64");
  return `set -u
d=${dir}
mkdir -p "$d" 2>/dev/null || true
tmp="$d/.tmp.$$.ack"
printf %s '${b64}' | base64 -d >"$tmp" 2>/dev/null || { rm -f "$tmp"; exit 1; }
chmod 0644 "$tmp" 2>/dev/null || true
mv -f "$tmp" "$d/${id}.json" || { rm -f "$tmp"; exit 1; }
`;
}

/**
 * Remove spool documents: a close document we have acted on, and the leftovers of a
 * request that is gone. `rm -f` on a list, so a file that vanished under us (the CLI's own
 * `--close` removes the request and the ack itself) is not an error. Pure.
 */
function buildRemoveScript(entries, opts = {}) {
  const dir = String(opts.dir || SPOOL_DIR);
  const paths = [];
  for (const entry of entries || []) {
    const sub = entry && entry.sub;
    if (sub !== "requests" && sub !== "acks" && sub !== "close") continue;
    if (!isSafeId(entry.id)) continue;
    paths.push(shQuote(`${dir}/${sub}/${entry.id}.json`));
  }
  if (!paths.length) return "";
  return `set -u\nrm -f ${paths.join(" ")} 2>/dev/null || true\nexit 0\n`;
}

/**
 * THE CAPABILITY CHECK, and the first thing this module ever runs on a VM.
 *
 * `construct.forwards.enabled` defaults to true — a feature agents are told to use cannot
 * need configuring first — so the guest side has to be established rather than assumed. A
 * VM provisioned BEFORE `construct expose` existed has no spool, no `expose` verb and
 * nothing this module could serve; opening a long-lived `inotifywait` stream to it (a
 * socket, a wakeup, a battery) would be a cost charged to an install that gained nothing,
 * which is exactly what the zero-change rule is about.
 *
 * So the answer is one line over one one-shot exec, and it is the EXACT marker
 * `bin/provision.sh` writes: `install -d` of `<dir>` plus `requests/`, `acks/` and
 * `close/`, all four together, on every provision. Testing all four rather than only the
 * root is deliberate — a half-made spool would let the watcher's `inotifywait` fail into
 * the polling fallback forever, and it is the same directory set docs/expose.md specifies.
 *
 * Answers `SPOOL=1` or `SPOOL=0` and exits 0 either way: "this VM does not have it" is an
 * answer, not a failure. Pure.
 */
function buildCapabilityScript(opts = {}) {
  const dir = shQuote(opts.dir || SPOOL_DIR);
  return `set -u
d=${dir}
if [ -d "$d" ] && [ -d "$d/requests" ] && [ -d "$d/acks" ] && [ -d "$d/close" ]; then
  printf '${SPOOL_YES}\\n'
else
  printf '${SPOOL_NO}\\n'
fi
exit 0
`;
}

/**
 * Read the capability check's answer. Only an explicit `SPOOL=1` is a yes: an empty or
 * garbled stdout (a VM that dropped the connection mid-answer, a transport that swallowed
 * it) means we did not establish the contract, and the watcher stays unstarted. Pure.
 */
function parseCapability(stdout) {
  for (const raw of String(stdout == null ? "" : stdout).split("\n")) {
    if (raw.trim() === SPOOL_YES) return true;
  }
  return false;
}

/**
 * The long-lived watcher: ONE SSH connection that blocks on the VM until the spool
 * changes. It carries NO DATA — it prints `CHANGED` and the host answers with a
 * reconcile. Forward changes are rare (a human or an agent typing `expose`), so one SSH
 * exec per change buys a much smaller protocol than streaming the spool would, and the
 * reconcile is the same code path the periodic poll uses.
 *
 * `inotifywait` runs in MONITOR mode (-m), not one-shot, so a change that lands while the
 * host is mid-reconcile waits in the pipe instead of being missed. Without inotify-tools
 * the loop just retries (in case the package appears later) and the host's 30 s reconcile
 * is the whole fallback — which is exactly the older-VM path.
 *
 * The trap matters: the host closing the connection sends a SIGHUP, and without it the
 * `inotifywait` we started would linger on the VM writing into a dead pipe. Pure.
 */
function buildWatchScript(opts = {}) {
  const dir = shQuote(opts.dir || SPOOL_DIR);
  const fallback = Number(opts.fallbackSeconds) > 0 ? Math.round(Number(opts.fallbackSeconds)) : WATCH_FALLBACK_SECONDS;
  const heartbeat = Number(opts.heartbeatSeconds) > 0 ? Math.round(Number(opts.heartbeatSeconds)) : WATCH_HEARTBEAT_SECONDS;
  return `set -u
d=${dir}
iw=""
cleanup() { if [ -n "$iw" ]; then kill "$iw" 2>/dev/null || true; fi; }
trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE
last=$SECONDS
beat() {
  if [ $((SECONDS - last)) -ge ${heartbeat} ]; then last=$SECONDS; printf '${HEARTBEAT_LINE}\\n'; fi
}
watch_events() {
  command -v inotifywait >/dev/null 2>&1 || return 1
  [ -d "$d/requests" ] || return 1
  [ -d "$d/close" ] || return 1
  # A process substitution, not a pipeline: the loop must run in THIS shell, or the
  # watcher's pid would be invisible to cleanup and survive us as an orphan.
  exec 3< <(inotifywait -m -q -e close_write,moved_to,delete,moved_from --format '' "$d/requests" "$d/close" 2>/dev/null)
  iw=$!
  while :; do
    IFS= read -r -t ${heartbeat} -u 3 _
    rc=$?
    # >128 = the read timed out (no events): beat and keep waiting.
    # non-zero and <=128 = EOF: inotifywait died; hand back to the caller.
    if [ "$rc" -ne 0 ] && [ "$rc" -le 128 ]; then break; fi
    if [ "$rc" -eq 0 ]; then printf '${CHANGED_LINE}\\n'; fi
    beat
  done
  cleanup
  iw=""
  exec 3<&-
  return 0
}
# On connect: the host reconciles once, which is what re-opens everything still queued
# after a reboot, a reconnect or a window switch.
printf '${CHANGED_LINE}\\n'
while :; do
  watch_events || true
  sleep ${fallback}
  beat
done
`;
}

// ── The remote list ──────────────────────────────────────────────────────────

/** A service entry that says, in either of the two field names the API might use, that
 *  the forward is finished. Explicitly closed is NOT a request to re-open. Pure. */
function isClosedEntry(raw) {
  for (const field of ["status", "state"]) {
    if (String(raw[field] == null ? "" : raw[field]).toLowerCase() === "closed") return true;
  }
  return false;
}

/**
 * Read the host service's forward list into the planner's inputs. The ack fields are
 * inline and flat there (service/README.md), which is the same lenient shape the guest
 * CLI reads — so this parser and `construct expose`'s agree by construction.
 *
 * Three outputs, because a remote list carries three different kinds of thing:
 *   requests   CLIENT entries this window may serve — the planner's input
 *   acks       their inline acks
 *   host       HOST entries, kept for PRESENTATION only. The service materializes those
 *              itself, and it answers 409 to an ack for one — but they are still this VM's
 *              forwards, and dropping them meant the panel could not show, open or close a
 *              `construct expose --to host` forward at all.
 *   closes     entries explicitly reported as closed, so a stale record cannot be read as
 *              a live request and re-opened.
 * Pure.
 */
function readForwardList(list) {
  const requests = [];
  const acks = [];
  const host = [];
  const closes = [];
  const entries = Array.isArray(list) ? list : [];
  for (const raw of entries) {
    if (!raw || typeof raw !== "object") continue;
    const id = String(raw.id == null ? "" : raw.id);
    if (!isSafeId(id)) continue;
    const vmPort = toPort(raw.vmPort);
    if (vmPort === null) continue;
    const target = String(raw.target == null ? "client" : raw.target).toLowerCase();
    const label = sanitizeText(raw.label, MAX_LABEL);

    if (target !== "client") {
      // Presentation only: never a request, so no tunnel is ever planned for it.
      host.push({
        id, vmPort, label, target,
        publicPort: toPort(raw.publicPort),
        url: typeof raw.url === "string" && raw.url ? raw.url : null,
      });
      continue;
    }

    if (isClosedEntry(raw)) {
      // The service says this one is done. Anything we hold for it has to go, and it must
      // NOT be treated as a request — that would re-open a forward the guest closed.
      closes.push(id);
      continue;
    }

    requests.push({ id, vmPort, label, target });
    const status = String(raw.status == null ? "" : raw.status).toLowerCase();
    if (status === "open" || status === "error") {
      acks.push({
        id,
        status,
        localPort: toPort(raw.localPort),
        hostLabel: sanitizeHostLabel(raw.hostLabel),
        message: sanitizeText(raw.message, MAX_MESSAGE),
      });
    }
  }
  return { requests, acks, host, closes };
}

// ── The planner ──────────────────────────────────────────────────────────────

/**
 * Given the spool (or the service's list) and the tunnels we hold, decide what to do.
 * PURE, and the only place these decisions are made — the Forwarder just executes the
 * result. Which is what makes the interesting properties testable instead of hoped for:
 * idempotence above all, since this runs on every inotify event, every 30 s poll and
 * every window activation.
 *
 * Inputs:
 *   requests  [{id, vmPort, label}]           what the guest asked for
 *   acks      [{id, status, localPort, hostLabel, message}]   what has been reported
 *   closes    [id]                            close documents (local mode)
 *   tunnels   [{id, vmPort, localPort, state:'starting'|'up'|'failed', acked, message}]
 *   owner     bool    false ⇒ read-only: another window owns this spool
 *   hostLabel string  the label our acks should carry
 *   reopenAcked bool  is this spool ours alone? TRUE for the local spool, FALSE for the
 *                     service, where another window (or another PC) may be the one serving.
 *
 * RE-OPENING SOMETHING THAT IS ALREADY ACKED. An `open` ack is a promise already
 * delivered: the agent printed that link and moved on. So the forward has to be
 * re-established on THE SAME PORT or not at all — a different port would silently change
 * the answer under a guest that is no longer looking.
 *
 *   • Locally there is exactly one owner per spool, so an ack found at startup is our own
 *     from a previous run and its tunnel died with the window that made it. Re-open,
 *     PREFERRING that port (`preferPort`); falling back is acceptable because we then
 *     re-ack, which docs/expose.md explicitly allows.
 *   • Remotely we cannot tell our own stale ack from another live window's. So the open is
 *     CONDITIONAL (`requirePort`): take it only if that exact port is still free on this
 *     PC. Free means nobody here is serving it — the window that did has gone — and we
 *     restore the very link the guest holds. Busy means somebody is, and we leave it be.
 *
 * Actions: open · ack · error · close · sweep · adopt.
 */
function planActions(input = {}) {
  // Every input is filtered by `isSafeId` HERE, once, rather than at each use: an id
  // reaches a spool file name and a URL path, so nothing downstream should have to
  // remember to re-check it. The parsers drop unsafe ids too — this is the belt to their
  // braces, and the reason a hostile id can never become an action.
  const safe = (list) => (Array.isArray(list) ? list : []).filter((e) => e && isSafeId(e.id));
  const requests = safe(input.requests);
  const acks = safe(input.acks);
  const tunnels = safe(input.tunnels);
  const closes = (Array.isArray(input.closes) ? input.closes : []).filter(isSafeId);
  const hostLabel = sanitizeHostLabel(input.hostLabel);
  const actions = [];

  // Not the owner: render, write nothing. The links another window opened work from here
  // anyway — it is the same PC and the same ports.
  if (input.owner === false) return actions;

  const byId = (list) => new Map(list.map((e) => [e.id, e]));
  const requestMap = byId(requests);
  const ackMap = byId(acks);
  const tunnelMap = byId(tunnels);
  const closeSet = new Set(closes);

  // 1. Close documents: tear the tunnel down, then remove the document — the CLI has
  //    already removed the request and the ack, so nothing else is left to do.
  for (const id of closeSet) {
    if (tunnelMap.has(id)) actions.push({ kind: "close", id, reason: "closed" });
    actions.push({ kind: "sweep", sub: "close", id });
  }

  // 2. A tunnel whose request is gone: the forward was closed (or the service dropped it).
  for (const tunnel of tunnels) {
    if (closeSet.has(tunnel.id)) continue;
    if (!requestMap.has(tunnel.id)) actions.push({ kind: "close", id: tunnel.id, reason: "gone" });
  }

  // 3. An ack with no request behind it: leftovers from a forward that is gone.
  for (const ack of acks) {
    if (!requestMap.has(ack.id)) actions.push({ kind: "sweep", sub: "acks", id: ack.id });
  }

  // 4. The requests themselves.
  const busy = tunnels
    .filter((t) => t && !closeSet.has(t.id) && requestMap.has(t.id))
    .map((t) => toPort(t.localPort))
    .filter((p) => p !== null);

  for (const request of requests) {
    if (closeSet.has(request.id)) continue;
    const tunnel = tunnelMap.get(request.id);
    const ack = ackMap.get(request.id);

    if (!tunnel) {
      // An error ack already reported is a FINAL answer to the guest: it has stopped
      // waiting, so re-opening on every 30 s tick would churn tunnels nobody is watching.
      // A retry comes from a fresh request (or a window switch, which clears the acks
      // this instance knows about along with the tunnels).
      if (ack && ack.status === "error") continue;
      const promised = ack && ack.status === "open" ? toPort(ack.localPort) : null;
      // An acked forward on a spool that is not ours alone: reclaim it only if the exact
      // port it promised is still free here — see the note above.
      if (promised !== null && input.reopenAcked !== true) {
        actions.push({
          kind: "open",
          id: request.id,
          vmPort: request.vmPort,
          label: request.label,
          requirePort: promised,
          taken: busy.slice(),
        });
        continue;
      }
      actions.push({
        kind: "open",
        id: request.id,
        vmPort: request.vmPort,
        label: request.label,
        // Keep the promise the existing ack already made to the guest, if we can.
        preferPort: promised,
        taken: busy.slice(),
      });
      continue;
    }

    if (tunnel.state === "failed") {
      const message = sanitizeText(tunnel.message, MAX_MESSAGE) || "the tunnel to the VM could not be opened";
      if (!ack || ack.status !== "error" || ack.message !== message) {
        actions.push({ kind: "error", id: request.id, message });
      }
      continue;
    }

    if (tunnel.state !== "up") continue; // still settling

    const localPort = toPort(tunnel.localPort);
    if (localPort === null) continue;

    // Somebody else overwrote our ack with a port that is not ours — in remote mode two
    // windows can race, and the ack IS the claim there. They won; drop our tunnel rather
    // than fight over the ack and leave the loser's port orphaned.
    if (tunnel.acked && ack && ack.status === "open" && toPort(ack.localPort) !== localPort) {
      actions.push({ kind: "adopt", id: request.id, localPort: toPort(ack.localPort) });
      continue;
    }

    // Ack when there is none, when it disagrees, or when it is stale in any field. A
    // re-ack after a reconnect is explicitly allowed by the contract.
    const disagrees = !ack
      || ack.status !== "open"
      || toPort(ack.localPort) !== localPort
      || sanitizeHostLabel(ack.hostLabel) !== hostLabel;
    if (disagrees) {
      actions.push({ kind: "ack", id: request.id, localPort, hostLabel });
    }
  }

  return actions;
}

/**
 * WHEN A WINDOW MAY HOLD A FORWARDER AT ALL — the lazy, guest-gated rule, as a pure
 * decision so it is driven by tests rather than by a live window.
 *
 * The forwarder is NOT started by activation. It is started by what the window's EXISTING
 * status flow (`probeOnce` + `withVmState`, the same reading the panel renders) has just
 * learned about the instance that refresh was captured for, and it never probes on its own:
 * a window that has not established the VM is up must not open an SSH stream to it, which
 * is what an activation-driven start did on every default install — including one whose VM
 * was off, and one whose guest predates `construct expose` entirely.
 *
 * Inputs:
 *   enabled  the `construct.forwards.enabled` setting (default true)
 *   name     the CAPTURED instance the reading describes
 *   armed    the instance a forwarder has already been asked to serve since that VM was
 *            last seen up, or null. This is what makes the trigger an EDGE: an older guest
 *            that answered "no spool" is not asked again on every 30 s refresh, only after
 *            a reconnect (which clears it), a switch, or the setting being toggled.
 *   online   did the probe reach the VM?
 *   vmState  the folded Hyper-V/host state ("running" | "off" | "saved" | "absent" | …)
 *
 * Returns { action: "start" | "stop" | "none", reason }.
 *
 * REACHABILITY IS THE WHOLE RULE, in both directions. A reading that reached the VM starts
 * the forwarder; a reading that did NOT reach it stops the one we hold and clears the
 * armed edge, whatever the host-side power state says — a watcher and a set of `ssh -L`
 * children pointed at a VM this window can no longer reach are doing nothing but holding
 * sockets, and the honest recovery is the reconnect edge: the next reading that DOES reach
 * it starts a fresh session, which re-opens everything still queued in the spool (requests
 * live under /etc/construct, so nothing is lost by letting go). `off`/`saved`/`absent` are
 * reported as the reason rather than being the condition. Pure.
 */
function planLifecycle(input = {}) {
  const armed = input.armed == null || input.armed === "" ? null : String(input.armed);
  const name = input.name == null ? null : String(input.name);
  if (input.enabled === false) return { action: armed ? "stop" : "none", reason: "disabled" };
  if (input.online === true) {
    if (armed !== null && name !== null && armed === name) return { action: "none", reason: "armed" };
    return { action: "start", reason: "reachable" };
  }
  const state = String(input.vmState == null ? "" : input.vmState).trim().toLowerCase();
  const reason = state === "off" || state === "saved" || state === "absent" ? state : "unreachable";
  return { action: armed ? "stop" : "none", reason };
}

/**
 * WHAT THE GUEST'S ANSWER MEANS FOR THE ARMED EDGE — planLifecycle's other half, and the
 * reason `start()` resolves to a value at all. Pure.
 *
 * planLifecycle arms an instance the moment a reachable reading asks for a start, which is
 * correct for the two answers the check can actually give: `SPOOL=1` is serving, `SPOOL=0`
 * is a guest that will not change until it is reprovisioned. But the check is one SSH exec
 * and it can simply FAIL — the connection drops, the VM is going down, sshd is not up yet —
 * and that answer establishes nothing. Left armed, the session (which has already stood
 * itself down) makes every later reachable reading a `none`, so no watcher and no reconcile
 * is ever retried and a queued `construct expose` waits without an ack until something
 * unrelated (an unreachable reading, a switch, a setting toggle, a reload) clears the edge.
 *
 * So an `unanswered` start is a RETRY: the window drops the session it holds and clears the
 * armed edge, and the next reachable reading starts a fresh one. It may only do that while
 * its own claim and generation are still current — a start whose window has moved on must
 * touch neither, because what it would be clearing belongs to another session by then.
 *
 * Inputs:
 *   outcome  what `Forwarder.start()` resolved to (START_* above)
 *   current  is this start still the window's current session? (slot claim + generation)
 *
 * Returns { action: "retry" | "keep" | "none", reason }:
 *   "retry" — clear the session reference and the armed edge; the next reading re-arms.
 *   "keep"  — leave both alone (serving, or a genuinely older guest asked once per connect).
 *   "none"  — not ours to touch.
 */
function planStartOutcome(input = {}) {
  if (input.current === false) return { action: "none", reason: "superseded" };
  const outcome = String(input.outcome == null ? "" : input.outcome);
  if (outcome === START_UNANSWERED) return { action: "retry", reason: "unanswered" };
  if (outcome === START_UNSUPPORTED) return { action: "keep", reason: "unsupported" };
  if (outcome === START_STOOD_DOWN) return { action: "none", reason: "stood-down" };
  return { action: "keep", reason: outcome || "started" };
}

// ── Presentation ─────────────────────────────────────────────────────────────

/**
 * The panel's view of one instance's forwards. Derived from the same inputs the planner
 * sees, so what the user reads and what the module does cannot drift. Pure.
 *
 * `status` is the guest's word for it: `open` (a link exists), `error` (we told the guest
 * so) or `queued` (nobody has picked it up yet — which is a normal state, not a fault:
 * the request waits in the spool until a window connects).
 */
function toSnapshot(input = {}) {
  const mode = input.mode === "remote" ? "remote" : "local";
  const safe = (list) => (Array.isArray(list) ? list : []).filter((e) => e && isSafeId(e.id));
  const requests = safe(input.requests);
  const closes = new Set((Array.isArray(input.closes) ? input.closes : []).filter(isSafeId));
  const ackMap = new Map(safe(input.acks).map((a) => [a.id, a]));
  const tunnelMap = new Map(safe(input.tunnels).map((t) => [t.id, t]));

  const items = [];
  for (const request of requests) {
    if (closes.has(request.id)) continue;
    const ack = ackMap.get(request.id);
    const tunnel = tunnelMap.get(request.id);
    const item = {
      id: request.id,
      vmPort: request.vmPort,
      label: sanitizeText(request.label, MAX_LABEL),
      target: "client",
      status: "queued",
      localPort: null,
      url: null,
      message: "",
      owned: true,
    };
    if (ack && ack.status === "error") {
      item.status = "error";
      item.message = ack.message;
    } else if (ack && ack.status === "open" && toPort(ack.localPort) !== null) {
      item.status = "open";
      item.localPort = toPort(ack.localPort);
      item.url = `http://${urlHostFor(ack.hostLabel) || "localhost"}:${item.localPort}/`;
    } else if (tunnel && tunnel.state === "up" && toPort(tunnel.localPort) !== null) {
      // The tunnel is up but the ack has not landed yet (or is somebody else's). Report
      // the port that is actually listening; it is the honest answer for this PC.
      item.status = "open";
      item.localPort = toPort(tunnel.localPort);
      item.url = `http://${urlHostFor(input.hostLabel) || "localhost"}:${item.localPort}/`;
    }
    items.push(item);
  }

  // HOST-target forwards (remote mode only). The service materializes and owns them, so
  // there is no tunnel and no ack here — but they are this VM's forwards, and the panel
  // has to be able to show, open and close them. `owned: false` says "not ours to serve",
  // which is what stops the UI offering anything that implies we are.
  for (const record of (Array.isArray(input.host) ? input.host : [])) {
    if (!record || !isSafeId(record.id)) continue;
    items.push({
      id: record.id,
      vmPort: record.vmPort,
      label: sanitizeText(record.label, MAX_LABEL),
      target: "host",
      status: record.url ? "open" : "queued",
      localPort: toPort(record.publicPort),
      url: record.url || null,
      message: "",
      owned: false,
    });
  }

  items.sort((a, b) => (a.vmPort - b.vmPort) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return { mode, owner: input.owner !== false, items };
}

// ── The Forwarder ────────────────────────────────────────────────────────────

/**
 * One instance's forwarder. Holds the tunnels, the watcher and the poll timer; every
 * effect goes through the injected transport.
 *
 * opts:
 *   instance    the normalized instance object ({name, backend, …}) — identity only
 *   mode        "local" | "remote"  (default: derived from instance.backend)
 *   transport   see the header
 *   dir         spool directory (default /etc/construct/forwards)
 *   hostLabel   the `construct.forwards.hostLabel` setting (default "")
 *   windowId    this window's claim id (default: a random one)
 *   eligible    () => bool — may this session still run? Asked around the guest capability
 *               check, because the window can disable/switch/close while it is in flight
 *               and the teardown for that is queued behind this start (see _eligible)
 *   onChange    (snapshot) => void — the panel push
 *   log         (line) => void
 *   timers      { setTimeout, clearTimeout } — injected so tests need no clock
 *   now         () => ms
 */
class Forwarder {
  constructor(opts = {}) {
    const instance = opts.instance || {};
    this.instance = instance;
    this.name = String(instance.name || "agent-vm");
    this.vmName = String(instance.vmName || instance.name || "");
    this.mode = opts.mode
      || (String(instance.backend || "").trim().toLowerCase() === "hyperv-remote" ? "remote" : "local");
    this.transport = opts.transport || {};
    this.dir = opts.dir || SPOOL_DIR;
    this.hostLabel = sanitizeHostLabel(opts.hostLabel);
    this.windowId = String(opts.windowId || randomWindowId()).replace(/[^A-Za-z0-9._-]/g, "") || "w";
    this.log = opts.log || (() => {});
    this.onChange = opts.onChange || (() => {});
    this._timers = opts.timers || { setTimeout, clearTimeout };
    this._now = opts.now || (() => Date.now());
    this._settleMs = opts._settleMs != null ? opts._settleMs : TUNNEL_SETTLE_MS;
    this._reconcileMs = opts.reconcileMs || (this.mode === "remote" ? REMOTE_POLL_MS : RECONCILE_MS);
    this._debounceMs = opts.debounceMs != null ? opts.debounceMs : EVENT_DEBOUNCE_MS;
    // An explicit base/count (tests, a future setting) wins; otherwise this instance's
    // own slice, which is the historical range for the default VM.
    const slice = instancePortSlice(this.name);
    this._portOpts = {
      base: opts.portBase != null ? opts.portBase : slice.base,
      count: opts.portCount != null ? opts.portCount : slice.count,
    };

    /** id -> tunnel record. THE module's state, and it is per-instance by construction. */
    this._tunnels = new Map();
    /** Last reconcile's view, for the snapshot. */
    this._view = { owner: this.mode === "remote", requests: [], acks: [], closes: [], host: [] };

    /** Does this VM have the spool contract? null until the capability check has answered
     *  (and back to null when it could not be established at all). */
    this.supported = null;
    /** The window's "may this session still run?" (see _eligible). Default: yes. */
    this._isEligible = typeof opts.eligible === "function" ? opts.eligible : null;

    this._stopped = true;
    this._starting = null;
    this._watchChild = null;
    this._watchBuffer = "";
    this._watchAttempt = 0;
    this._watchRestart = null;
    this._pollTimer = null;
    this._debounceTimer = null;
    this._reconciling = null;
  }

  /** The address this instance's forwards listen on right now — loopback unless a host
   *  label is configured, in which case the advertised name has to actually resolve here. */
  bindHost() {
    return bindHostFor(this.hostLabel);
  }

  /**
   * Start serving this instance. Idempotent, and NOTHING persistent is spawned before the
   * guest has been established: the first action is the one-shot capability check
   * (buildCapabilityScript), and only a VM that answers `SPOOL=1` gets the long-lived
   * watcher and the reconcile poll. A guest without the spool contract costs exactly that
   * one exec — no watcher, no reconcile, no timer — and is not asked again until the
   * window is told to start again (a reconnect, a switch, or the setting being toggled).
   *
   * Returns the promise for that first round trip so a test can await it; callers fire and
   * forget, because start() is reached from a status reading and may not block on SSH.
   *
   * It RESOLVES TO the guest's answer (START_* / planStartOutcome), because the window that
   * armed this instance cannot tell "an older guest, asked once per connection" from "the
   * check never happened" by any other means — and treating the second as the first arms
   * the instance forever. An unexpected throw is that same "nothing was established", so it
   * resolves to `unanswered` rather than rejecting: a start is never a caller's error path.
   */
  start() {
    if (!this._stopped) return this._starting || Promise.resolve(START_RUNNING);
    this._stopped = false;
    this._starting = this._begin().catch((e) => {
      this.log(`forwarder[${this.name}]: could not start — ${errText(e)}`);
      return START_UNANSWERED;
    });
    return this._starting;
  }

  /**
   * start()'s body: check the guest (local mode), then bring the transport up.
   *
   * `eligible()` is asked around the ONE await in here, for the same reason the caller asks
   * its own generation around the transport build: the window can disable forwarding,
   * switch instance, lose reachability or close WHILE the guest is answering, and the
   * teardown for any of those is queued BEHIND the start that is still running. Without
   * this check a positive answer would spawn the watcher and reconcile the spool first and
   * be disposed a moment later — SSH traffic after the user said stop.
   */
  async _begin() {
    if (!this._eligible("before the guest check")) return START_STOOD_DOWN;
    if (this.mode === "local") {
      const supported = await this._checkSpool();
      // dispose()/_standDown() while the check was in flight: the window switched, the
      // setting went off, or it is closing. Nothing may be spawned behind that.
      if (this._stopped) return START_STOOD_DOWN;
      if (!supported) {
        this._standDown();
        // `supported === false` is the guest ANSWERING "no spool"; null is a check that
        // never happened. Only the second one is worth another connection's worth of work.
        return this.supported === false ? START_UNSUPPORTED : START_UNANSWERED;
      }
    }
    if (!this._eligible("while the guest was answering")) return START_STOOD_DOWN;
    if (this.mode === "local") this._startWatch();
    this._schedulePoll();
    // Not awaited: the reconcile is a second round trip and the caller is a status
    // reading, not a step that may block on SSH.
    this._safeReconcile();
    return START_SUPPORTED;
  }

  /**
   * May this session still run? The window's own answer (generation + the enabled setting +
   * the captured instance + the shutdown flag), injected so this module stays free of every
   * one of those concepts. A session that is no longer eligible stands down HERE — it
   * spawns nothing — and the teardown queued behind it disposes it in the ordinary way.
   */
  _eligible(when) {
    if (this._stopped) return false;
    if (typeof this._isEligible !== "function") return true;
    let ok = false;
    try { ok = !!this._isEligible(); } catch (_) { ok = false; }
    if (ok) return true;
    this.log(`forwarder[${this.name}]: standing down ${when} — this window let the instance go`);
    this._standDown();
    return false;
  }

  /**
   * Does this VM have the spool contract? One cheap exec, and the only place `supported`
   * is decided. An unreachable VM (or a script that failed) is NOT recorded as an old
   * guest — it is simply not established, and says so in the log; `supported` stays null,
   * which is what start() reports back as `unanswered` so the window re-arms on the next
   * reachable reading instead of holding a stood-down session forever. Never throws.
   */
  async _checkSpool() {
    let res;
    try {
      res = await this._runScript(buildCapabilityScript({ dir: this.dir }));
    } catch (e) {
      this.supported = null;
      this.log(`forwarder[${this.name}]: could not check this VM's forward spool — ${errText(e)}`);
      return false;
    }
    if (!res || res.code !== 0) {
      this.supported = null;
      this.log(`forwarder[${this.name}]: could not check this VM's forward spool ` +
        `(exit ${res ? res.code : "?"}) — not watching it until the next reading reaches it`);
      return false;
    }
    this.supported = parseCapability(res.stdout);
    if (!this.supported) {
      // Provisioned before `construct expose` existed: there is nothing to serve and
      // nothing will appear until it is reprovisioned. One log line, no process, no timer.
      this.log(`forwarder[${this.name}]: no forward spool on this VM ` +
        `(${this.dir}) — standing down. Reprovision the VM to use \`construct expose\`.`);
    }
    return this.supported === true;
  }

  /**
   * Stop everything and let go: kill every tunnel, kill the watcher, release the claim.
   * A window switch and deactivation both come through here, so a tunnel can never
   * outlive the instance it belongs to.
   */
  dispose() {
    this._stopped = true;
    this._starting = null;
    this._clear("_watchRestart");
    this._clear("_pollTimer");
    this._clear("_debounceTimer");
    const child = this._watchChild;
    this._watchChild = null;
    this._watchBuffer = "";
    if (child) { try { child.kill(); } catch (_) {} }
    for (const id of [...this._tunnels.keys()]) this._killTunnel(id);
    this._releaseClaim();
    this._view = { owner: this.mode === "remote", requests: [], acks: [], closes: [], host: [] };
  }

  /**
   * Stop watching and polling, but stay re-startable: this VM has nothing to serve today,
   * and the next activation, instance switch or setting change asks again. Distinct from
   * dispose(), which is "let go for good" — here there is nothing to let go of.
   */
  _standDown() {
    this._stopped = true;
    this._starting = null;
    this._clear("_watchRestart");
    this._clear("_pollTimer");
    this._clear("_debounceTimer");
    const child = this._watchChild;
    this._watchChild = null;
    this._watchBuffer = "";
    if (child) { try { child.kill(); } catch (_) {} }
    this._view = { owner: false, requests: [], acks: [], closes: [], host: [] };
  }

  /** What the panel renders. Never throws. */
  snapshot() {
    return toSnapshot({
      mode: this.mode,
      owner: this._view.owner,
      hostLabel: this.hostLabel,
      requests: this._view.requests,
      acks: this._view.acks,
      closes: this._view.closes,
      host: this._view.host,
      tunnels: this._tunnelViews(),
    });
  }

  /**
   * The `construct.forwards.hostLabel` setting changed.
   *
   * Two different changes hide behind one setting, and only one of them is cheap:
   *
   *   • label → different label (both non-empty), or any change that leaves the BIND
   *     ADDRESS alone: the listener is already correct, so this is a re-ack and nothing
   *     more — the guest's link text changes, the socket does not.
   *
   *   • a transition between loopback and wildcard: the running `ssh -L` captured its bind
   *     address when it was spawned, so the setting and the socket now DISAGREE. Re-acking
   *     alone would be a lie in both directions, and one of them is a security bug:
   *       - "" → "pc" re-ack advertises a LAN URL for a listener still on 127.0.0.1
   *         (a dead link);
   *       - "pc" → "" re-ack says localhost while ssh keeps listening on 0.0.0.0, i.e.
   *         the port stays exposed after the user opted OUT.
   *     So the tunnels are actually restarted, on the SAME local port (the guest may
   *     already hold that number), and the new ack is written only once the replacement
   *     has survived its settle window.
   */
  setHostLabel(value) {
    const next = sanitizeHostLabel(value);
    if (next === this.hostLabel) return;
    const before = bindHostFor(this.hostLabel);
    const after = bindHostFor(next);
    this.hostLabel = next;
    if (before === after) {
      this._safeReconcile();
      return;
    }
    void this._rebindTunnels(after);
  }

  /**
   * Re-spawn every live tunnel on a new bind address, keeping its local port. The old
   * child is killed FIRST — otherwise the replacement's bind would fail against it — and
   * the reconcile that writes the new acks runs only after every replacement has settled.
   */
  async _rebindTunnels(bindHost) {
    const records = [...this._tunnels.values()];
    if (!records.length) {
      this._safeReconcile();
      return;
    }
    this.log(`forwarder[${this.name}]: host label changed — rebinding ${records.length} tunnel(s) to ${bindHost}`);
    for (const record of records) {
      if (this._stopped || this._tunnels.get(record.id) !== record) continue;
      if (record.restartTimer) { this._clearTimer(record.restartTimer); record.restartTimer = null; }
      const child = record.child;
      record.child = null;
      if (child) { try { child.kill(); } catch (_) {} }
      record.bindHost = bindHost;
      record.state = "starting";
      // The ack on record is now stale by construction: it names the old reachability.
      record.acked = false;
      record.attempt = 0;
      await this._spawnTunnel(record);
    }
    this._safeReconcile();
  }

  /**
   * The panel's Close button. Locally this is what `construct expose --close` does minus
   * the close document: the request and the ack are removed, so `--list` is immediately
   * right and the next reconcile has nothing to re-open. Remotely the service owns the
   * record, so it is a DELETE.
   */
  async closeForward(id) {
    if (!isSafeId(id)) return false;
    // READ-ONLY MEANS READ-ONLY. A window that does not own the local spool must not
    // delete the owner's request/ack documents: it would tear down a forward the owner
    // still believes it is serving, and the owner would then re-open it on the next
    // reconcile. Enforced here, in the core, and not only by hiding the button — the panel
    // is untrusted input. Remote mode has no spool and no claim: the service is the
    // authority there, so any window may ask it to delete a record.
    if (this.mode === "local" && this._view.owner === false) {
      this.log(`forwarder[${this.name}]: refusing to close ${id} — another window owns this VM's forward spool`);
      return false;
    }
    this._killTunnel(id);
    try {
      if (this.mode === "remote") {
        await this._fetch("DELETE", `/vms/${encodeURIComponent(this.vmName || this.name)}/forwards/${encodeURIComponent(id)}`);
      } else {
        await this._runScript(buildRemoveScript(
          [{ sub: "requests", id }, { sub: "acks", id }, { sub: "close", id }],
          { dir: this.dir }));
      }
    } catch (e) {
      this.log(`forwarder[${this.name}]: could not close ${id} — ${errText(e)}`);
      return false;
    }
    this._safeReconcile();
    return true;
  }

  /** One reconcile pass: read the world, plan, execute. Never rejects. */
  async reconcile() {
    if (this._stopped) return;
    // Coalesce: a burst of events and the periodic poll must not each drive a pass.
    if (this._reconciling) return this._reconciling;
    const run = this._reconcileOnce().catch((e) => {
      this.log(`forwarder[${this.name}]: reconcile failed — ${errText(e)}`);
    });
    this._reconciling = run;
    const clear = () => { if (this._reconciling === run) this._reconciling = null; };
    run.then(clear, clear);
    return run;
  }

  /**
   * Read the world once, then plan-and-apply until there is nothing left to do.
   *
   * The loop is what makes a forward open in ONE pass: round 1 opens the tunnel, round 2
   * sees it up (or failed) and writes the ack (or the error) the guest is blocking on.
   * Without it the ack would wait for the next tick — up to 30 s, which is the CLI's whole
   * default timeout, so `expose` would routinely report a working forward as queued.
   *
   * Bounded, because a planner bug must cost a log line and not a spin: every action
   * changes the state the next round plans against, so two rounds is the real depth and
   * the third only ever confirms it.
   */
  async _reconcileOnce() {
    const view = this.mode === "remote" ? await this._readRemote() : await this._readLocal();
    if (this._stopped || !view) return;
    this._view = view;

    for (let round = 0; round < 3; round++) {
      const actions = planActions({
        requests: this._view.requests,
        acks: this._view.acks,
        closes: this._view.closes,
        tunnels: this._tunnelViews(),
        owner: this._view.owner,
        hostLabel: this.hostLabel,
        reopenAcked: this.mode === "local",
      });
      if (!actions.length) break;

      for (const action of actions) {
        if (this._stopped) break;
        try {
          await this._apply(action);
        } catch (e) {
          this.log(`forwarder[${this.name}]: ${action.kind} ${action.id || ""} failed — ${errText(e)}`);
          // Do not retry inside this reconcile: a failing write would otherwise be
          // attempted three times in a row before the round limit stopped it.
          this._suppress(action);
        }
      }
      if (this._stopped) break;
    }
    this._push();
  }

  /**
   * After an action failed, record enough locally that the next round does not immediately
   * plan it again. Only the writes need this — an `open` that failed has already left the
   * tunnel in a state the planner reads correctly.
   */
  _suppress(action) {
    if (action.kind !== "ack" && action.kind !== "error") return;
    const record = this._tunnels.get(action.id);
    if (record) record.acked = true;
    const acks = this._view.acks.filter((a) => a.id !== action.id);
    acks.push({
      id: action.id,
      status: action.kind === "error" ? "error" : "open",
      localPort: action.localPort == null ? null : action.localPort,
      hostLabel: action.kind === "error" ? "" : sanitizeHostLabel(action.hostLabel),
      message: action.kind === "error" ? sanitizeText(action.message, MAX_MESSAGE) : "",
    });
    this._view = { ...this._view, acks };
  }

  async _apply(action) {
    if (action.kind === "open") return this._openTunnel(action);
    if (action.kind === "ack") return this._writeAck(action.id, { status: "open", localPort: action.localPort, hostLabel: action.hostLabel });
    if (action.kind === "error") return this._writeAck(action.id, { status: "error", message: action.message });
    if (action.kind === "close" || action.kind === "adopt") { this._killTunnel(action.id); return; }
    if (action.kind === "sweep") return this._sweep(action.sub, action.id);
  }

  // ── Reading the world ──────────────────────────────────────────────────────

  async _readLocal() {
    const res = await this._runScript(buildReconcileScript({ dir: this.dir, windowId: this.windowId }));
    if (!res || res.code !== 0) {
      // Unreachable VM, or a spool we may not read. Keep the tunnels (they are this PC's
      // and may still work) and say nothing about ownership we cannot verify.
      return null;
    }
    const dump = parseDump(res.stdout);
    if (dump.owner === "absent") {
      // No spool on this VM: it predates `construct expose`, so there is nothing to serve
      // and nothing will appear until it is reprovisioned. Stand down rather than hold a
      // connection and poll a VM every 30 s for a feature it does not have — an install
      // that never got the guest side must cost exactly one probe, not a heartbeat.
      this.log(`forwarder[${this.name}]: no forward spool on this VM ` +
        `(${this.dir}) — standing down. Reprovision the VM to use \`construct expose\`.`);
      this._standDown();
      return null;
    }
    const owner = dump.owner === "self";
    if (!owner && dump.owner === "other" && this._tunnels.size) {
      // Another window took the spool over while we held tunnels — hand them back rather
      // than serve ports whose acks somebody else now writes.
      this.log(`forwarder[${this.name}]: another window owns the forward spool; releasing ${this._tunnels.size} tunnel(s)`);
      for (const id of [...this._tunnels.keys()]) this._killTunnel(id);
    }
    return { owner, requests: dump.requests, acks: dump.acks, closes: dump.closes, host: [] };
  }

  async _readRemote() {
    const list = await this._fetch("GET", `/vms/${encodeURIComponent(this.vmName || this.name)}/forwards`);
    if (!Array.isArray(list)) return null;
    const read = readForwardList(list);
    return {
      owner: true,
      requests: read.requests,
      acks: read.acks,
      closes: read.closes,
      host: read.host,
    };
  }

  // ── Tunnels ────────────────────────────────────────────────────────────────

  _tunnelViews() {
    return [...this._tunnels.values()].map((t) => ({
      id: t.id, vmPort: t.vmPort, localPort: t.localPort,
      state: t.state, acked: t.acked, message: t.message,
    }));
  }

  async _openTunnel(action) {
    if (this._tunnels.has(action.id)) return;
    const taken = new Set([...(action.taken || []), ...this._tunnelViews().map((t) => t.localPort)]);

    // A CONDITIONAL reclaim (remote mode, an entry that already carries an ack): the only
    // acceptable port is the one the guest was already promised, and only if it is still
    // free here. Busy means another window — or another of the user's PCs — is serving it,
    // and taking it over on a different port would break a link somebody already has.
    if (action.requirePort != null) {
      const port = toPort(action.requirePort);
      if (port === null || taken.has(port) || !(await this._probe(port))) return;
      const record = this._newTunnel(action.id, action.vmPort, port);
      await this._spawnTunnel(record);
      return;
    }

    const candidates = portCandidates(action.vmPort, {
      ...this._portOpts,
      prefer: action.preferPort,
      taken: [...taken],
    });

    let localPort = null;
    for (const candidate of candidates) {
      if (await this._probe(candidate)) { localPort = candidate; break; }
    }
    if (localPort === null) {
      // Nothing free: an explicit error ack, so `construct expose` stops waiting and says
      // why instead of timing out into "no client attached", which would be a lie.
      this.log(`forwarder[${this.name}]: no free local port for VM port ${action.vmPort}`);
      await this._writeAck(action.id, {
        status: "error",
        message: `no free port on this PC for VM port ${action.vmPort} (tried ${action.vmPort} and ${PORT_BASE}-${PORT_BASE + PORT_COUNT - 1})`,
      });
      return;
    }

    const record = this._newTunnel(action.id, action.vmPort, localPort);
    await this._spawnTunnel(record);
  }

  /** A tunnel record, registered in the table before anything is spawned so a concurrent
   *  plan cannot hand the same local port to a second forward. */
  _newTunnel(id, vmPort, localPort) {
    const record = {
      id, vmPort, localPort, bindHost: this.bindHost(),
      state: "starting", acked: false, message: "",
      attempt: 0, child: null, startedAt: 0, restartTimer: null,
    };
    this._tunnels.set(id, record);
    return record;
  }

  /** Spawn the child and decide, after the settle window, whether it really opened. */
  _spawnTunnel(record) {
    return new Promise((resolve) => {
      let child;
      try {
        child = this.transport.spawnTunnel({
          localPort: record.localPort, vmPort: record.vmPort, bindHost: record.bindHost,
        });
      } catch (e) {
        record.state = "failed";
        record.message = errText(e);
        return resolve();
      }
      record.child = child;
      record.startedAt = this._now();
      record.state = "starting";

      let stderr = "";
      if (child.stderr && child.stderr.on) {
        child.stderr.on("data", (d) => { if (stderr.length < 2000) stderr += String(d); });
      }

      let settled = false;
      const settle = (ok, why) => {
        if (settled) return;
        settled = true;
        this._clearTimer(timer);
        if (ok) {
          record.state = "up";
          record.message = "";
          // `attempt` is deliberately NOT reset here. Surviving the 1.2 s settle window
          // only means the port opened; it says nothing about the link being healthy. A
          // tunnel that dies at 1.3 s every time would otherwise reset its own streak on
          // each retry, back off at 2 s forever and never reach the persistent-failure
          // ack — so the guest would wait out its whole timeout on a forward that is
          // provably broken. Only ended() clears it, and only after CONNECTION_HEALTHY_MS.
          this.log(`forwarder[${this.name}]: tunnel up — ${record.bindHost}:${record.localPort} -> vm:${record.vmPort}`);
        } else {
          record.state = "failed";
          record.message = why;
        }
        resolve();
      };

      const ended = (why) => {
        if (record.child !== child) return;   // superseded by a restart
        record.child = null;
        const detail = (stderr.trim().split("\n").pop() || why || "").slice(0, 200);
        if (!settled) {
          // Died inside the settle window: it never opened the port. `ExitOnForwardFailure`
          // is what makes this the reliable signal for "the local port was taken".
          return settle(false, detail || "the SSH tunnel exited immediately");
        }
        // Died later: the link dropped (VM saved, laptop slept). Restart it, on the SAME
        // local port — the ack already promised that number.
        if (this._now() - record.startedAt >= CONNECTION_HEALTHY_MS) record.attempt = 0;
        this._scheduleRestart(record, detail);
      };

      if (child.on) {
        child.on("error", (e) => ended(errText(e)));
        child.on("exit", (code) => ended(code == null ? "" : `ssh exited ${code}`));
      }

      const timer = this._setTimer(() => settle(true, ""), this._settleMs);
    });
  }

  _scheduleRestart(record, detail) {
    if (this._stopped || !this._tunnels.has(record.id)) return;
    record.attempt += 1;
    if (record.attempt > MAX_TUNNEL_ATTEMPTS) {
      // Persistently broken: tell the guest, and keep retrying at the slowest cadence so
      // it recovers by itself if the VM comes back.
      record.state = "failed";
      record.message = detail || "the SSH tunnel keeps dropping";
      this._safeReconcile();
    } else {
      record.state = "starting";
    }
    const delay = reconnectDelayMs(record.attempt);
    this.log(`forwarder[${this.name}]: tunnel ${record.bindHost}:${record.localPort} dropped` +
      (detail ? ` (${detail})` : "") + `; retrying in ${Math.round(delay / 1000)}s`);
    record.restartTimer = this._setTimer(() => {
      record.restartTimer = null;
      if (this._stopped || this._tunnels.get(record.id) !== record) return;
      this._spawnTunnel(record).then(() => this._safeReconcile(), () => this._safeReconcile());
    }, delay);
  }

  _killTunnel(id) {
    const record = this._tunnels.get(id);
    if (!record) return;
    this._tunnels.delete(id);
    if (record.restartTimer) { this._clearTimer(record.restartTimer); record.restartTimer = null; }
    const child = record.child;
    record.child = null;
    if (child) { try { child.kill(); } catch (_) {} }
  }

  // ── Writing ────────────────────────────────────────────────────────────────

  async _writeAck(id, ack) {
    const doc = ackDocument(id, ack);
    if (this.mode === "remote") {
      const body = { status: doc.status };
      if (doc.localPort != null) body.localPort = doc.localPort;
      if (doc.hostLabel) body.hostLabel = doc.hostLabel;
      if (doc.message) body.message = doc.message;
      await this._fetch("POST",
        `/vms/${encodeURIComponent(this.vmName || this.name)}/forwards/${encodeURIComponent(id)}/ack`, body);
    } else {
      const res = await this._runScript(buildAckScript(id, doc, { dir: this.dir }));
      if (!res || res.code !== 0) {
        throw new Error(`writing the ack failed${res && res.stderr ? `: ${res.stderr.trim().slice(0, 200)}` : ""}`);
      }
    }
    const record = this._tunnels.get(id);
    if (record) record.acked = true;
    // Reflect it locally so the next plan is computed against what we just wrote rather
    // than against a stale read.
    const acks = this._view.acks.filter((a) => a.id !== id);
    acks.push(parseAck(id, doc) || { id, status: doc.status, localPort: doc.localPort || null, hostLabel: doc.hostLabel || "", message: doc.message });
    this._view = { ...this._view, acks };
  }

  async _sweep(sub, id) {
    // Remote mode has no spool: the service's own record is the only state, and a forward
    // that left the list took its ack with it. The id is still dropped from our view, so
    // the next plan-and-apply round does not keep re-planning a sweep that does nothing.
    if (this.mode === "remote") {
      if (sub === "close") this._view = { ...this._view, closes: this._view.closes.filter((c) => c !== id) };
      return;
    }
    const script = buildRemoveScript([{ sub, id }], { dir: this.dir });
    if (script) await this._runScript(script);
    if (sub === "acks") this._view = { ...this._view, acks: this._view.acks.filter((a) => a.id !== id) };
    if (sub === "close") this._view = { ...this._view, closes: this._view.closes.filter((c) => c !== id) };
  }

  /** Give the claim back on the way out, so the next window takes over immediately rather
   *  than after the TTL. Best-effort by design: if it fails, the TTL still covers us. */
  _releaseClaim() {
    if (this.mode !== "local" || !this._view.owner) return;
    const script = `set -u
own=${shQuote(`${this.dir}/${OWNER_FILE}`)}
me=${shQuote(this.windowId)}
cur=$(head -c 200 "$own" 2>/dev/null | tr -d '\\r\\n' || true)
who=\${cur%% *}
if [ "$who" = "$me" ]; then rm -f "$own" 2>/dev/null || true; fi
exit 0
`;
    Promise.resolve()
      .then(() => this._runScript(script))
      .catch(() => {});
  }

  // ── The watcher ────────────────────────────────────────────────────────────

  _startWatch() {
    if (this._stopped || this._watchChild || this._watchRestart) return;
    let child;
    try {
      child = this.transport.spawnWatch(buildWatchScript({ dir: this.dir }));
    } catch (e) {
      this.log(`forwarder[${this.name}]: could not start the spool watcher — ${errText(e)}`);
      return this._scheduleWatchRestart();
    }
    if (!child) return this._scheduleWatchRestart();

    this._watchChild = child;
    this._watchBuffer = "";
    const startedAt = this._now();
    let stderr = "";

    if (child.stdout && child.stdout.on) {
      if (child.stdout.setEncoding) child.stdout.setEncoding("utf8");
      child.stdout.on("data", (chunk) => {
        const { lines, rest } = splitLines(this._watchBuffer, chunk);
        this._watchBuffer = rest;
        if (lines.some((l) => l === CHANGED_LINE)) this._onSpoolChanged();
      });
    }
    if (child.stderr && child.stderr.on) {
      child.stderr.on("data", (d) => { if (stderr.length < 2000) stderr += String(d); });
    }

    const ended = (why) => {
      if (this._watchChild !== child) return;
      this._watchChild = null;
      this._watchBuffer = "";
      if (this._now() - startedAt >= CONNECTION_HEALTHY_MS) this._watchAttempt = 0;
      const detail = (stderr.trim().split("\n").pop() || why || "").slice(0, 200);
      this.log(`forwarder[${this.name}]: spool watcher disconnected${detail ? ` (${detail})` : ""}`);
      this._scheduleWatchRestart();
    };
    if (child.on) {
      child.on("error", (e) => ended(errText(e)));
      child.on("exit", (code) => ended(code == null ? "" : `ssh exited ${code}`));
    }
  }

  _scheduleWatchRestart() {
    if (this._stopped || this._watchRestart) return;
    this._watchAttempt += 1;
    this._watchRestart = this._setTimer(() => {
      this._watchRestart = null;
      this._startWatch();
    }, reconnectDelayMs(this._watchAttempt));
  }

  /** A spool event: reconcile, debounced so a burst costs one round trip. */
  _onSpoolChanged() {
    if (this._stopped || this._debounceTimer) return;
    this._debounceTimer = this._setTimer(() => {
      this._debounceTimer = null;
      this._safeReconcile();
    }, this._debounceMs);
  }

  _schedulePoll() {
    if (this._stopped || this._pollTimer) return;
    this._pollTimer = this._setTimer(() => {
      this._pollTimer = null;
      this._safeReconcile();
      this._schedulePoll();
    }, this._reconcileMs);
  }

  // ── Plumbing ───────────────────────────────────────────────────────────────

  _safeReconcile() {
    try {
      const p = this.reconcile();
      if (p && p.catch) p.catch(() => {});
    } catch (_) { /* reconcile never throws, but a bad transport could */ }
  }

  _push() {
    try { this.onChange(this.snapshot()); } catch (_) {}
  }

  async _runScript(script) {
    if (!script) return { code: 0, stdout: "", stderr: "" };
    if (typeof this.transport.runRemoteScript !== "function") {
      throw new Error("the forwarder transport has no runRemoteScript");
    }
    return this.transport.runRemoteScript(script);
  }

  async _fetch(method, path, body) {
    if (typeof this.transport.fetchJson !== "function") {
      throw new Error("the forwarder transport has no fetchJson");
    }
    return this.transport.fetchJson(method, path, body);
  }

  /** Is `port` free on this PC, ON THE ADDRESS THE TUNNEL WILL BIND? Probing loopback and
   *  then binding 0.0.0.0 would call a port free that is not. */
  async _probe(port) {
    if (typeof this.transport.probePort !== "function") return true;
    try { return !!(await this.transport.probePort(port, this.bindHost())); } catch (_) { return false; }
  }

  _setTimer(fn, ms) {
    const t = this._timers.setTimeout(fn, ms);
    if (t && typeof t.unref === "function") t.unref();
    return t;
  }

  _clearTimer(t) {
    if (t != null) this._timers.clearTimeout(t);
  }

  _clear(field) {
    if (this[field] != null) { this._clearTimer(this[field]); this[field] = null; }
  }
}

/** A claim id for this window. Filename-safe, and only ever compared for equality. */
function randomWindowId() {
  return `w${Date.now().toString(36)}${Math.floor(Math.random() * 0xfffff).toString(36)}`;
}

/** One line out of anything that was thrown. */
function errText(e) {
  return String((e && e.message) || e || "").slice(0, 300);
}

function createForwarder(opts) {
  return new Forwarder(opts);
}

module.exports = {
  SPOOL_DIR, WIRE_VERSION, OWNER_FILE, OWNER_LOCK_DIR, OWNER_TTL_SEC, CLAIM_LOCK_TTL_SEC,
  RECONCILE_MS, REMOTE_POLL_MS, EVENT_DEBOUNCE_MS,
  PORT_BASE, PORT_COUNT, TUNNEL_SETTLE_MS,
  RECONNECT_BASE_MS, RECONNECT_MAX_MS, CONNECTION_HEALTHY_MS, MAX_TUNNEL_ATTEMPTS,
  WATCH_FALLBACK_SECONDS, WATCH_HEARTBEAT_SECONDS, HEARTBEAT_LINE, CHANGED_LINE,
  SPOOL_YES, SPOOL_NO,
  START_SUPPORTED, START_UNSUPPORTED, START_UNANSWERED, START_STOOD_DOWN, START_RUNNING,
  MAX_HOST_LABEL, MAX_MESSAGE, MAX_LABEL,
  BIND_LOOPBACK, BIND_ALL, bindHostFor,
  isSafeId, sanitizeText, sanitizeHostLabel, urlHostFor, toPort, portCandidates, instancePortSlice, reconnectDelayMs,
  splitLines, shQuote,
  isV1Document, parseRequest, parseAck, parseClose, ackDocument, parseDump,
  isClosedEntry, readForwardList,
  buildReconcileScript, buildAckScript, buildRemoveScript, buildWatchScript,
  buildCapabilityScript, parseCapability,
  planActions, planLifecycle, planStartOutcome, toSnapshot,
  Forwarder, createForwarder,
};
