"use strict";
// Plain-node unit tests for the client port forwarder (src/forwarder.js, the extension
// half of `construct expose`) and its adapter (src/forwarder-ui.js).
//
// Covered thoroughly — which is the whole point of the §4.8 module shape: with the
// transport injected there is nothing here that needs a VM, a socket or a subprocess.
//   • the pure helpers — id/port/text guards, port policy, backoff, stream splitting;
//   • the spool documents — request/ack parsing, the ack document's exact shape, the
//     reconcile dump parser;
//   • the spool SCRIPTS as data — base64-as-data (no interpolation), the atomic ack
//     rename, the ownership claim's read-back, inotify monitor mode + the orphan trap,
//     and injection proofs for the spool path and the window id;
//   • the PLANNER, every action case, plus its idempotence (the property that makes it
//     safe to run on every event, every poll and every activation);
//   • the LOCAL flow against a fake transport — request → tunnel argv → atomic ack →
//     close → kill, port fallback, re-open on activation, and read-only non-ownership;
//   • the REMOTE flow against a fake fetch — poll → tunnel → POST ack, entry removed →
//     kill, and an acked entry left alone;
//   • tunnel supervision — the settle window, restart with backoff, the error ack after
//     persistent failure, dispose;
//   • the UI projections — the panel message contract, the idle-policy clamp, the saved
//     state's power intent;
//   • ssh.js's new `-L` argv builder.
//
// NOT covered here (runtime-only): a real `ssh -L`, a real inotifywait, and the VS Code
// settings read (a one-line getConfiguration call behind hostLabelOf/forwardsEnabled).
// Run:  node forwarder.test.js
const assert = require("assert");
const { EventEmitter } = require("events");
const cp = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const f = require("../src/forwarder");
const ui = require("../src/forwarder-ui");
const ssh = require("../src/ssh");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);
const deep = (name, got, want) => {
  let same = true;
  try { assert.deepStrictEqual(got, want); } catch (_) { same = false; }
  ok(name, same, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);
};

// ── Harness: a fake child, fake timers, a fake transport ────────────────────────

/** A stand-in for a spawned ssh: what forwarder.js documents it needs and nothing more. */
function makeChild() {
  const child = new EventEmitter();
  child.stdout = new EventEmitter();
  child.stdout.setEncoding = () => {};
  child.stderr = new EventEmitter();
  child.killed = 0;
  child.kill = () => { child.killed += 1; };
  /** Pretend the process died. */
  child.die = (code) => child.emit("exit", code == null ? 1 : code);
  return child;
}

/** A manual clock for the injected timers, so nothing here waits on real time. */
function makeTimers() {
  let seq = 1, now = 0;
  const queue = new Map();
  const api = {
    setTimeout(fn, ms) {
      const handle = { id: seq++, unref() { return handle; } };
      queue.set(handle.id, { at: now + (Number(ms) || 0), fn });
      return handle;
    },
    clearTimeout(handle) { if (handle && handle.id != null) queue.delete(handle.id); },
  };
  const flush = async () => { for (let i = 0; i < 8; i++) await new Promise((r) => setImmediate(r)); };
  return {
    api,
    now: () => now,
    pending: () => queue.size,
    /**
     * Advance the clock, firing due callbacks in time order.
     *
     * The flush comes FIRST on every iteration, and that is the whole trick: the code
     * under test registers most of its timers from inside a promise continuation (the
     * settle timer only exists once the port probe has resolved), so a scan before the
     * microtask queue drains would find an empty timer queue and jump straight past it.
     */
    async advance(ms) {
      const target = now + (Number(ms) || 0);
      for (;;) {
        await flush();
        let pick = null;
        for (const [id, entry] of queue) {
          if (entry.at <= target && (!pick || entry.at < pick.entry.at)) pick = { id, entry };
        }
        if (!pick) break;
        queue.delete(pick.id);
        now = Math.max(now, pick.entry.at);
        try { pick.entry.fn(); } catch (_) {}
      }
      now = target;
      await flush();
    },
    flush,
  };
}

/** Build the reconcile script's stdout. */
function dump(owner, docs) {
  const lines = ["OWNER=" + owner];
  for (const [kind, id, obj] of docs || []) {
    const payload = typeof obj === "string" ? obj : JSON.stringify(obj);
    lines.push(`${kind} ${id} ${Buffer.from(payload, "utf8").toString("base64")}`);
  }
  return lines.join("\n") + "\n";
}

const isReconcileScript = (s) => s.indexOf("dump R requests") >= 0;
const isAckScript = (s) => s.indexOf("base64 -d") >= 0;
const isRemoveScript = (s) => /^set -u\nrm -f /.test(s);
const isReleaseScript = (s) => s.indexOf('rm -f "$own"') >= 0;

function makeTransport(opts = {}) {
  const t = {
    scripts: [], acks: [], removes: [], releases: [],
    watches: [], tunnels: [], fetches: [],
    dump: dump("self", []),
    /** null = every port is free; otherwise only these are. */
    freePorts: null,
    /** Successive answers for GET .../forwards. The last one repeats. */
    lists: [[]],
    listIndex: 0,
    fetchFail: null,

    runRemoteScript(script) {
      t.scripts.push(script);
      if (isReconcileScript(script)) return Promise.resolve({ code: opts.readCode || 0, stdout: t.dump, stderr: "" });
      if (isAckScript(script)) { t.acks.push(script); return Promise.resolve({ code: opts.ackCode || 0, stdout: "", stderr: "" }); }
      if (isReleaseScript(script)) { t.releases.push(script); return Promise.resolve({ code: 0, stdout: "", stderr: "" }); }
      if (isRemoveScript(script)) { t.removes.push(script); return Promise.resolve({ code: 0, stdout: "", stderr: "" }); }
      return Promise.resolve({ code: 0, stdout: "", stderr: "" });
    },

    spawnWatch(script) {
      const child = makeChild();
      t.watches.push({ script, child });
      return child;
    },

    spawnTunnel(spec) {
      const child = makeChild();
      t.tunnels.push({ localPort: spec.localPort, vmPort: spec.vmPort, bindHost: spec.bindHost, child });
      return child;
    },

    probePort(port) {
      return Promise.resolve(t.freePorts ? t.freePorts.indexOf(port) >= 0 : true);
    },

    fetchJson(method, path, body) {
      t.fetches.push({ method, path, body });
      if (t.fetchFail) return Promise.reject(new Error(t.fetchFail));
      if (method === "GET" && /\/forwards$/.test(path)) {
        const list = t.lists[Math.min(t.listIndex, t.lists.length - 1)];
        t.listIndex += 1;
        return Promise.resolve(list);
      }
      return Promise.resolve({});
    },
  };
  return t;
}

/** A Forwarder wired to fakes. Returns { fwd, transport, timers, pushes }. */
function makeForwarder(opts = {}) {
  const timers = makeTimers();
  const transport = opts.transport || makeTransport(opts);
  const pushes = [];
  const fwd = f.createForwarder({
    instance: opts.instance || { name: "agent-vm", backend: opts.backend || "hyperv-local" },
    mode: opts.mode,
    transport,
    dir: opts.dir || "/etc/construct/forwards",
    hostLabel: opts.hostLabel || "",
    windowId: opts.windowId || "win1",
    timers: timers.api,
    now: timers.now,
    onChange: (snap) => pushes.push(snap),
    log: opts.log || (() => {}),
    portBase: opts.portBase,
    portCount: opts.portCount,
  });
  return { fwd, transport, timers, pushes };
}

/**
 * Drive one reconcile to completion past the tunnel settle window. Starts the forwarder
 * on first use, because that is the only state in which it acts — a disposed/never-started
 * one deliberately does nothing, which is what makes dispose() final.
 */
async function settle(fwd, timers, ms = 5000) {
  if (fwd._stopped) fwd.start();
  const p = fwd.reconcile();
  await timers.advance(ms);
  await p;
  await timers.flush();
}

// ── Pure helpers ───────────────────────────────────────────────────────────────
(() => {
  ok("isSafeId: the CLI's own id shape", f.isSafeId("1756742400-a3f1"));
  ok("isSafeId: a service guid", f.isSafeId("9f1c2f0f4b7e4a1e8c2d3e4f5a6b7c8d"));
  ok("isSafeId: rejects a traversal", !f.isSafeId("../../etc/passwd"));
  ok("isSafeId: rejects a bare ..", !f.isSafeId(".."));
  ok("isSafeId: rejects a slash", !f.isSafeId("a/b"));
  ok("isSafeId: rejects a shell metacharacter", !f.isSafeId("a;rm -rf /"));
  ok("isSafeId: rejects empty", !f.isSafeId(""));
  ok("isSafeId: rejects a null id", !f.isSafeId(null));
  ok("isSafeId: rejects an absurdly long id", !f.isSafeId("a".repeat(200)));

  eq("sanitizeText: collapses whitespace", f.sanitizeText("  a\t b  ", 50), "a b");
  eq("sanitizeText: newlines cannot forge a line", f.sanitizeText("a\nb", 50), "a b");
  eq("sanitizeText: CR is stripped too", f.sanitizeText("a\r\nb", 50), "a b");
  eq("sanitizeText: NUL is stripped", f.sanitizeText("a\u0000b", 50), "a b");
  eq("sanitizeText: U+2028 is stripped", f.sanitizeText("a\u2028b", 50), "a b");
  eq("sanitizeText: caps the length", f.sanitizeText("x".repeat(500), 10), "x".repeat(10));
  eq("sanitizeText: null reads as empty", f.sanitizeText(null, 10), "");

  eq("hostLabel: a plain machine name survives", f.sanitizeHostLabel("christoph-pc"), "christoph-pc");
  eq("hostLabel: an FQDN survives", f.sanitizeHostLabel("pc.home.example"), "pc.home.example");
  eq("hostLabel: an IPv6 literal survives bracketed", f.sanitizeHostLabel("[fe80::1]"), "[fe80::1]");
  eq("hostLabel: whitespace is removed, not a separator", f.sanitizeHostLabel("  pc  "), "pc");
  eq("hostLabel: a slash is refused outright", f.sanitizeHostLabel("pc/evil"), "");
  eq("hostLabel: an @ (credential smuggling) is refused", f.sanitizeHostLabel("evil.test@pc"), "");
  eq("hostLabel: a query/fragment is refused", f.sanitizeHostLabel("pc?a=b"), "");
  eq("hostLabel: empty is the default", f.sanitizeHostLabel(""), "");
  eq("hostLabel: undefined is the default", f.sanitizeHostLabel(undefined), "");

  eq("toPort: an integer", f.toPort(5173), 5173);
  eq("toPort: a numeric string (came over JSON)", f.toPort("5173"), 5173);
  eq("toPort: 0 is not a port", f.toPort(0), null);
  eq("toPort: 65536 is not a port", f.toPort(65536), null);
  eq("toPort: a float is not a port", f.toPort(80.5), null);
  eq("toPort: null is not a port", f.toPort(null), null);
  eq("toPort: '' is not a port", f.toPort(""), null);
  eq("toPort: a hostile string is not a port", f.toPort("80; rm -rf /"), null);

  deep("ports: the VM's own port comes first", f.portCandidates(5173, { count: 2 }), [5173, 18800, 18801]);
  deep("ports: a busy vmPort falls through to the range", f.portCandidates(5173, { count: 2, taken: [5173] }), [18800, 18801]);
  deep("ports: an already-promised port wins", f.portCandidates(5173, { count: 2, prefer: 18805 }), [18805, 5173, 18800, 18801]);
  deep("ports: a taken preferred port is skipped", f.portCandidates(5173, { count: 2, prefer: 18805, taken: [18805] }), [5173, 18800, 18801]);
  deep("ports: a vmPort inside the range is not offered twice", f.portCandidates(18801, { count: 3 }), [18801, 18800, 18802]);
  deep("ports: no duplicates when prefer === vmPort", f.portCandidates(5173, { count: 1, prefer: 5173 }), [5173, 18800]);
  ok("ports: never runs past 65535", f.portCandidates(80, { base: 65534, count: 8 }).every((p) => p <= 65535));

  eq("backoff: first retry is quick", f.reconnectDelayMs(1), 2000);
  eq("backoff: doubles", f.reconnectDelayMs(3), 8000);
  eq("backoff: caps at a minute", f.reconnectDelayMs(50), 60000);
  eq("backoff: garbage reads as the first attempt", f.reconnectDelayMs("x"), 2000);

  const split = f.splitLines("", "CHANGED\n#\n\nCHAN");
  deep("splitLines: complete lines only, heartbeat dropped", split.lines, ["CHANGED"]);
  eq("splitLines: keeps the partial line", split.rest, "CHAN");
  deep("splitLines: joins across chunks", f.splitLines("CHAN", "GED\n").lines, ["CHANGED"]);
  eq("splitLines: caps the carried remainder", f.splitLines("", "x".repeat(20000)).rest, "");

  eq("shQuote: quotes plainly", f.shQuote("abc"), "'abc'");
  eq("shQuote: escapes an embedded quote", f.shQuote("a'b"), "'a'\\''b'");
})();

// ── Spool documents ────────────────────────────────────────────────────────────
(() => {
  const req = f.parseRequest("1-a", { v: 1, id: "1-a", vmPort: 5173, label: "vite dev", target: "client" });
  eq("request: vmPort", req.vmPort, 5173);
  eq("request: label", req.label, "vite dev");
  eq("request: a missing target defaults to client",
    f.parseRequest("1-a", { v: 1, id: "1-a", vmPort: 80 }).target, "client");
  eq("request: a host target is not ours",
    f.parseRequest("1-a", { v: 1, id: "1-a", vmPort: 80, target: "host" }), null);
  eq("request: a bad port is dropped, not guessed", f.parseRequest("1-a", { v: 1, id: "1-a", vmPort: 0 }), null);
  eq("request: an unsafe id is dropped", f.parseRequest("../x", { v: 1, id: "../x", vmPort: 80 }), null);
  eq("request: a hostile label cannot forge a line",
    f.parseRequest("1-a", { v: 1, id: "1-a", vmPort: 80, label: "a\nFAKE" }).label, "a FAKE");

  // The wire version and the document's own id are MANDATORY on the local spool: acting on
  // a v2 document as if it were v1 is exactly what the version field exists to prevent.
  eq("request: a MISSING v is not a v1 document", f.parseRequest("1-a", { id: "1-a", vmPort: 80 }), null);
  eq("request: a FUTURE v is refused, not read as v1",
    f.parseRequest("1-a", { v: 2, id: "1-a", vmPort: 80 }), null);
  eq("request: v as a string is not v1", f.parseRequest("1-a", { v: "1", id: "1-a", vmPort: 80 }), null);
  eq("request: a body id that disagrees with the file name is ambiguous, so refused",
    f.parseRequest("1-a", { v: 1, id: "2-b", vmPort: 80 }), null);
  // docs/expose.md defines `id` in all three document shapes, and the spool is published
  // atomically — so a document without one was never validly written, and reading it would
  // mean acting on a partial file.
  eq("request: a MISSING body id is refused (it is part of the v1 shape)",
    f.parseRequest("1-a", { v: 1, vmPort: 80 }), null);
  eq("request: a non-string body id is refused", f.parseRequest("1-a", { v: 1, id: 1, vmPort: 80 }), null);

  const ack = f.parseAck("1-a", { v: 1, id: "1-a", status: "open", localPort: 18800, hostLabel: "pc", message: "" });
  eq("ack: status", ack.status, "open");
  eq("ack: localPort", ack.localPort, 18800);
  eq("ack: hostLabel", ack.hostLabel, "pc");
  eq("ack: an unknown status is not an ack", f.parseAck("1-a", { v: 1, id: "1-a", status: "maybe" }), null);
  eq("ack: an error ack needs no port",
    f.parseAck("1-a", { v: 1, id: "1-a", status: "error", message: "no" }).localPort, null);
  eq("ack: a missing v is refused", f.parseAck("1-a", { id: "1-a", status: "open", localPort: 1 }), null);
  eq("ack: a future v is refused", f.parseAck("1-a", { v: 2, id: "1-a", status: "open", localPort: 1 }), null);
  eq("ack: a mismatched body id is refused",
    f.parseAck("1-a", { v: 1, id: "9-z", status: "open", localPort: 1 }), null);
  eq("ack: a MISSING body id is refused", f.parseAck("1-a", { v: 1, status: "open", localPort: 1 }), null);

  eq("close: a v1 document closes the forward it names", f.parseClose("1-a", { v: 1, id: "1-a" }), "1-a");
  // An INCOMPLETE close document must not tear down the tunnel its file name selects.
  eq("close: a MISSING body id is refused", f.parseClose("1-a", { v: 1 }), null);
  eq("close: a missing v is refused", f.parseClose("1-a", { id: "1-a" }), null);
  eq("close: a future v is refused", f.parseClose("1-a", { v: 2, id: "1-a" }), null);
  eq("close: a mismatched body id is refused", f.parseClose("1-a", { v: 1, id: "2-b" }), null);
  eq("close: an unsafe id is refused", f.parseClose("../x", { v: 1, id: "../x" }), null);

  eq("bind: no host label means loopback, explicitly", f.bindHostFor(""), f.BIND_LOOPBACK);
  eq("bind: ...and undefined too", f.bindHostFor(undefined), f.BIND_LOOPBACK);
  eq("bind: a host label opts in to all interfaces, or the link it advertises is dead",
    f.bindHostFor("christoph-pc"), f.BIND_ALL);
  eq("bind: a REJECTED host label stays loopback (it is not advertised either)",
    f.bindHostFor("pc/evil"), f.BIND_LOOPBACK);

  const doc = f.ackDocument("1-a", { status: "open", localPort: 18800, hostLabel: "pc", message: "" });
  deep("ack document: exactly the documented shape",
    doc, { v: 1, id: "1-a", status: "open", localPort: 18800, hostLabel: "pc", message: "" });
  ok("ack document: hostLabel is OMITTED when empty (the loopback default)",
    !("hostLabel" in f.ackDocument("1-a", { status: "open", localPort: 18800, hostLabel: "" })));
  const errDoc = f.ackDocument("1-a", { status: "error", message: "no free port" });
  eq("ack document: an error carries no port", errDoc.localPort, undefined);
  eq("ack document: an error carries the reason", errDoc.message, "no free port");
  eq("ack document: an unknown status becomes open, never something new on the wire",
    f.ackDocument("1-a", { status: "weird", localPort: 1 }).status, "open");
  eq("ack document: the version is the contract's", doc.v, f.WIRE_VERSION);

  const parsed = f.parseDump(dump("self", [
    ["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, label: "vite" }],
    ["A", "1-a", { v: 1, id: "1-a", status: "open", localPort: 18800 }],
    ["C", "2-b", { v: 1, id: "2-b", closedAt: "now" }],
    ["R", "../evil", { v: 1, id: "../evil", vmPort: 80 }],
    ["R", "3-c", "{not json"],
    ["X", "4-d", { v: 1, id: "4-d", vmPort: 80 }],
    ["R", "5-e", { v: 2, id: "5-e", vmPort: 80 }],
    ["C", "6-f", { v: 2, id: "6-f" }],
    ["R", "7-g", { v: 1, vmPort: 80 }],
    ["C", "8-h", { v: 1 }],
  ]));
  eq("dump: ownership is read", parsed.owner, "self");
  eq("dump: one request", parsed.requests.length, 1);
  eq("dump: one ack", parsed.acks.length, 1);
  deep("dump: one close, by NAME", parsed.closes, ["2-b"]);
  ok("dump: an unsafe id never reaches the planner", !parsed.requests.some((r) => r.id.indexOf("evil") >= 0));
  ok("dump: a FUTURE-version request is not acted on", !parsed.requests.some((r) => r.id === "5-e"));
  ok("dump: a future-version close document is not acted on", parsed.closes.indexOf("6-f") < 0);
  ok("dump: a request with no body id is not acted on", !parsed.requests.some((r) => r.id === "7-g"));
  ok("dump: an incomplete close document does not tear a tunnel down", parsed.closes.indexOf("8-h") < 0);
  eq("dump: 'other' is read", f.parseDump("OWNER=other\n").owner, "other");
  eq("dump: 'absent' (a VM predating the feature) is read", f.parseDump("OWNER=absent\n").owner, "absent");
  eq("dump: a missing OWNER line is 'unknown', never 'self'", f.parseDump("").owner, "unknown");
  eq("dump: a garbage OWNER value is 'unknown'", f.parseDump("OWNER=yes\n").owner, "unknown");
})();

// ── The spool scripts, as data ─────────────────────────────────────────────────
(() => {
  const script = f.buildReconcileScript({ dir: "/etc/construct/forwards", windowId: "win1" });
  ok("reconcile: mutual exclusion is a real mkdir mutex, not a read-back",
    script.indexOf(`mkdir "$lock"`) >= 0);
  ok("reconcile: ...released after the transaction", script.indexOf(`rmdir "$lock"`) >= 0);
  ok("reconcile: ...and a window that cannot take it does NOT claim",
    /if ! mkdir "\$lock" 2>\/dev\/null; then[\s\S]*?return 1\n {4}fi/.test(script));
  ok("reconcile: the record is replaced with an atomic rename", script.indexOf('mv -f "$tmp" "$own"') >= 0);
  ok("reconcile: the record is read AFTER the transaction, outside the lock",
    script.lastIndexOf("read_owner") > script.lastIndexOf(`rmdir "$lock"`));
  ok("reconcile: a stale lock is breakable so the spool cannot wedge forever",
    script.indexOf(`rmdir "$lock" 2>/dev/null || true\n      mkdir "$lock"`) >= 0);
  ok("reconcile: honours a TTL", script.indexOf(`-ge "$ttl"`) >= 0 && script.indexOf(`ttl=${f.OWNER_TTL_SEC}`) >= 0);
  ok("reconcile: the lock has its own, shorter TTL", script.indexOf(`lockttl=${f.CLAIM_LOCK_TTL_SEC}`) >= 0);
  ok("reconcile: takes over only when free, ours, or stale",
    script.indexOf('if [ -z "$who" ] || [ "$who" = "$me" ]') >= 0);
  ok("reconcile: a non-numeric timestamp reads as 0", script.indexOf("case \"$ts\" in ''|*[!0-9]*) ts=0 ;; esac") >= 0);
  ok("reconcile: a missing spool answers 'absent', not an error", script.indexOf("OWNER=absent") >= 0);
  ok("reconcile: dumps all three directories",
    script.indexOf("dump R requests") >= 0 && script.indexOf("dump A acks") >= 0 && script.indexOf("dump C close") >= 0);
  ok("reconcile: documents ride as base64, never as parsed text", script.indexOf("| base64 2>/dev/null") >= 0);
  ok("reconcile: caps how much of a file it will read", script.indexOf("head -c 4096") >= 0);
  ok("reconcile: skips dotfiles (its own temp/claim files)", script.indexOf('case "$id" in .*) continue ;; esac') >= 0);

  // Injection proofs: both interpolated values are attacker-shaped.
  const hostile = f.buildReconcileScript({ dir: "/tmp/x'; rm -rf /; echo '", windowId: "a b; rm -rf /" });
  ok("reconcile: a hostile dir stays one quoted literal", hostile.indexOf("rm -rf /; echo") >= 0 && hostile.indexOf("\nrm -rf") < 0);
  ok("reconcile: a hostile dir's quote is escaped", hostile.indexOf("'\\''") >= 0);
  ok("reconcile: a hostile window id is reduced to safe characters", hostile.indexOf("me='abrm-rf'") >= 0);

  const ackScript = f.buildAckScript("1-a", { v: 1, id: "1-a", status: "open", localPort: 18800, message: "" }, { dir: "/s" });
  ok("ack script: the document rides as base64", /printf %s '[A-Za-z0-9+/=]+' \| base64 -d/.test(ackScript));
  ok("ack script: published with a rename, so no half-written read", ackScript.indexOf('mv -f "$tmp" "$1-a.json"') < 0);
  ok("ack script: renames onto the id's file", ackScript.indexOf('mv -f "$tmp" "$d/1-a.json"') >= 0);
  ok("ack script: creates the directory if the VM predates the feature", ackScript.indexOf('mkdir -p "$d"') >= 0);
  ok("ack script: cleans up its temp file on failure", ackScript.indexOf('rm -f "$tmp"') >= 0);
  ok("ack script: the payload decodes back byte for byte", (() => {
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(ackScript)[1];
    return Buffer.from(b64, "base64").toString("utf8").trim() ===
      JSON.stringify({ v: 1, id: "1-a", status: "open", localPort: 18800, message: "" });
  })());
  ok("ack script: a hostile message never appears as script source", (() => {
    const doc = f.ackDocument("1-a", { status: "error", message: "'; rm -rf / #" });
    return f.buildAckScript("1-a", doc, { dir: "/s" }).indexOf("rm -rf") < 0;
  })());
  ok("ack script: an unsafe id is REFUSED, not escaped", (() => {
    try { f.buildAckScript("../evil", { v: 1 }, { dir: "/s" }); return false; } catch (_) { return true; }
  })());

  const remove = f.buildRemoveScript([
    { sub: "close", id: "1-a" }, { sub: "acks", id: "2-b" },
    { sub: "evil", id: "3-c" }, { sub: "close", id: "../x" },
  ], { dir: "/s" });
  ok("remove: names the close document", remove.indexOf("'/s/close/1-a.json'") >= 0);
  ok("remove: names the ack", remove.indexOf("'/s/acks/2-b.json'") >= 0);
  ok("remove: an unknown subdirectory is dropped", remove.indexOf("evil") < 0);
  ok("remove: an unsafe id is dropped", remove.indexOf("..") < 0);
  ok("remove: tolerates a file that already vanished", remove.indexOf("rm -f") >= 0 && remove.indexOf("|| true") >= 0);
  eq("remove: nothing to do is an empty script", f.buildRemoveScript([]), "");

  const watch = f.buildWatchScript({ dir: "/s" });
  ok("watch: inotify in MONITOR mode, so a change during a drain is not missed", watch.indexOf("inotifywait -m -q") >= 0);
  ok("watch: watches requests AND close", watch.indexOf('"$d/requests" "$d/close"') >= 0);
  ok("watch: reaps its inotifywait on hangup", watch.indexOf("trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE") >= 0);
  ok("watch: a process substitution, so the pid stays visible to cleanup", watch.indexOf("exec 3< <(inotifywait") >= 0);
  ok("watch: heartbeats, so a dead pipe reaps the watcher", watch.indexOf(`printf '${f.HEARTBEAT_LINE}\\n'`) >= 0);
  ok("watch: reconciles on connect (re-opens what is still queued)",
    watch.indexOf(`printf '${f.CHANGED_LINE}\\n'\nwhile :;`) >= 0);
  ok("watch: carries no spool data, only a signal", watch.indexOf("base64") < 0);
  ok("watch: degrades to a quiet retry without inotify-tools",
    watch.indexOf("command -v inotifywait >/dev/null 2>&1 || return 1") >= 0);
  ok("watch: a hostile dir stays one quoted literal",
    f.buildWatchScript({ dir: "/tmp/x'; rm -rf /; echo '" }).indexOf("\nrm -rf") < 0);
})();

// ── The claim protocol, RUN FOR REAL against a temp spool ──────────────────────
// The mutual-exclusion property cannot be asserted by reading the script: it is a property
// of concurrent execution. So the generated script is actually executed here, many
// windows at a time, and the invariant checked is the one that matters — AT MOST ONE
// window may ever report OWNER=self for the same spool.
//
// The script runs on the VM in production, so this needs a POSIX shell; skipped on Windows,
// where the extension host could not run it anyway.
async function claimProtocol() {
  console.log("\n  -- claim protocol (real bash, concurrent) --");
  if (process.platform === "win32") {
    console.log("  SKIP  (needs a POSIX shell; the script runs on the VM in production)");
    return;
  }

  const root = fs.mkdtempSync(path.join(os.tmpdir(), "construct-fwd-claim-"));
  const spool = path.join(root, "forwards");
  for (const d of ["", "requests", "acks", "close"]) fs.mkdirSync(path.join(spool, d), { recursive: true });

  const scriptFor = (windowId, opts = {}) => {
    const file = path.join(root, `claim-${windowId}.sh`);
    fs.writeFileSync(file, f.buildReconcileScript({ dir: spool, windowId, ...opts }));
    return file;
  };

  const run = (file) => new Promise((resolve) => {
    cp.execFile("bash", [file], { timeout: 20000 }, (err, stdout) => resolve(String(stdout || "")));
  });
  const ownerOf = (out) => (/^OWNER=(\w+)$/m.exec(out) || [])[1] || "?";

  // Round 1: a single window takes an unowned spool.
  const w1 = scriptFor("win1"), w2 = scriptFor("win2"), w3 = scriptFor("win3");
  eq("claim: an unowned spool is taken", ownerOf(await run(w1)), "self");
  eq("claim: a second window is refused", ownerOf(await run(w2)), "other");
  eq("claim: the owner renews its own claim", ownerOf(await run(w1)), "self");
  eq("claim: ...and the second is still refused", ownerOf(await run(w2)), "other");

  // THE RACE. Many windows claim a fresh spool simultaneously, repeatedly. Exactly one
  // may win each time; "two owners" is the bug this replaced write-then-read-back for.
  let races = 0, doubleOwners = 0, noOwners = 0;
  for (let round = 0; round < 25; round++) {
    fs.rmSync(path.join(spool, ".owner"), { force: true });
    fs.rmSync(path.join(spool, ".owner.lock"), { recursive: true, force: true });
    const contenders = ["a", "b", "c", "d", "e"].map((n) => scriptFor(`race${round}${n}`));
    const outs = await Promise.all(contenders.map(run));
    const selves = outs.filter((o) => ownerOf(o) === "self").length;
    races++;
    if (selves > 1) doubleOwners++;
    if (selves === 0) noOwners++;
  }
  eq(`claim: ${races} concurrent races, never two owners`, doubleOwners, 0);
  // Not a hard invariant (a round where every window loses the lock is legal — they retry
  // on the next reconcile), but it should be rare enough that the protocol is useful.
  ok(`claim: ...and a winner is elected in most rounds (${races - noOwners}/${races})`,
    noOwners <= Math.floor(races / 2), `${noOwners} rounds elected nobody`);

  // A stale record is taken over; the previous owner then stands down.
  fs.writeFileSync(path.join(spool, ".owner"), `win1 ${Math.floor(Date.now() / 1000) - 500}\n`);
  eq("claim: a stale record is taken over", ownerOf(await run(w3)), "self");
  eq("claim: ...and the previous owner stands down", ownerOf(await run(w1)), "other");

  // A lock left behind by a window that died mid-transaction blocks exactly once, then is
  // reclaimed on age.
  fs.mkdirSync(path.join(spool, ".owner.lock"), { recursive: true });
  eq("claim: a live lock makes a window report the record rather than guess",
    ownerOf(await run(w3)), "self");   // w3 still owns the record from above
  eq("claim: ...and a contender does not become a second owner",
    ownerOf(await run(w1)), "other");
  // Backdate the lock past its TTL: it must be reclaimable, not a permanent wedge.
  const old = new Date(Date.now() - 10 * 60 * 1000);
  fs.utimesSync(path.join(spool, ".owner.lock"), old, old);
  fs.writeFileSync(path.join(spool, ".owner"), `win3 ${Math.floor(Date.now() / 1000) - 500}\n`);
  eq("claim: a stale lock is broken so the spool cannot wedge forever",
    ownerOf(await run(w1)), "self");
  ok("claim: ...and the lock is released again", !fs.existsSync(path.join(spool, ".owner.lock")));

  // A missing spool is 'absent', and an unwritable one must never read as ours.
  eq("claim: a missing spool answers absent",
    ownerOf(await run(scriptFor("win1", { dir: path.join(root, "nope") }))), "absent");
  if (process.getuid && process.getuid() !== 0) {
    const ro = path.join(root, "ro");
    fs.mkdirSync(ro, { recursive: true });
    fs.chmodSync(ro, 0o555);
    eq("claim: an unwritable spool never reports self",
      ownerOf(await run(scriptFor("winRO", { dir: ro }))), "other");
    fs.chmodSync(ro, 0o755);
  } else {
    console.log("  SKIP  claim: unwritable-spool case (running as root, which ignores mode bits)");
  }

  fs.rmSync(root, { recursive: true, force: true });
}

// ── The planner ────────────────────────────────────────────────────────────────
(() => {
  const request = { id: "1-a", vmPort: 5173, label: "vite" };

  deep("plan: a fresh request opens a tunnel",
    f.planActions({ requests: [request] }).map((a) => a.kind), ["open"]);
  eq("plan: the open action carries the VM port",
    f.planActions({ requests: [request] })[0].vmPort, 5173);
  deep("plan: nothing at all when another window owns the spool",
    f.planActions({ requests: [request], owner: false }), []);

  deep("plan: a tunnel that is up but unacked gets an ack",
    f.planActions({ requests: [request], tunnels: [{ id: "1-a", localPort: 18800, state: "up" }] })
      .map((a) => a.kind), ["ack"]);
  eq("plan: the ack names the port that actually opened",
    f.planActions({ requests: [request], tunnels: [{ id: "1-a", localPort: 18800, state: "up" }] })[0].localPort, 18800);
  eq("plan: the ack carries the configured host label",
    f.planActions({ requests: [request], hostLabel: "pc", tunnels: [{ id: "1-a", localPort: 18800, state: "up" }] })[0].hostLabel, "pc");

  deep("plan: an agreeing ack produces NOTHING (idempotence)",
    f.planActions({
      requests: [request],
      acks: [{ id: "1-a", status: "open", localPort: 18800, hostLabel: "" }],
      tunnels: [{ id: "1-a", localPort: 18800, state: "up", acked: true }],
    }), []);
  deep("plan: a stale ack port is re-acked (a reconnect on another port)",
    f.planActions({
      requests: [request],
      acks: [{ id: "1-a", status: "open", localPort: 1, hostLabel: "" }],
      tunnels: [{ id: "1-a", localPort: 18800, state: "up" }],
    }).map((a) => a.kind), ["ack"]);
  deep("plan: a changed host label is re-acked",
    f.planActions({
      requests: [request], hostLabel: "pc",
      acks: [{ id: "1-a", status: "open", localPort: 18800, hostLabel: "" }],
      tunnels: [{ id: "1-a", localPort: 18800, state: "up", acked: true }],
    }).map((a) => a.kind), ["ack"]);

  deep("plan: a still-settling tunnel is left alone",
    f.planActions({ requests: [request], tunnels: [{ id: "1-a", localPort: 18800, state: "starting" }] }), []);

  const failed = f.planActions({ requests: [request], tunnels: [{ id: "1-a", state: "failed", message: "port busy" }] });
  deep("plan: a failed tunnel reports an error", failed.map((a) => a.kind), ["error"]);
  eq("plan: the error carries the reason", failed[0].message, "port busy");
  eq("plan: a failure with no detail still says something useful",
    f.planActions({ requests: [request], tunnels: [{ id: "1-a", state: "failed" }] })[0].message,
    "the tunnel to the VM could not be opened");
  deep("plan: an error already reported is not repeated",
    f.planActions({
      requests: [request],
      acks: [{ id: "1-a", status: "error", message: "port busy" }],
      tunnels: [{ id: "1-a", state: "failed", message: "port busy" }],
    }), []);
  deep("plan: an error ack is a FINAL answer — no re-open churn",
    f.planActions({ requests: [request], acks: [{ id: "1-a", status: "error", message: "x" }] }), []);

  const closed = f.planActions({ closes: ["1-a"], tunnels: [{ id: "1-a", localPort: 18800, state: "up" }] });
  deep("plan: a close document kills the tunnel and removes itself",
    closed.map((a) => a.kind), ["close", "sweep"]);
  eq("plan: the swept document is the close one", closed[1].sub, "close");
  deep("plan: a close with no tunnel still removes the document",
    f.planActions({ closes: ["1-a"] }).map((a) => `${a.kind}:${a.sub || ""}`), ["sweep:close"]);
  deep("plan: a closed request is not re-opened in the same pass",
    f.planActions({ requests: [request], closes: ["1-a"] }).map((a) => a.kind), ["sweep"]);

  deep("plan: a tunnel whose request vanished is closed",
    f.planActions({ tunnels: [{ id: "1-a", localPort: 18800, state: "up" }] }).map((a) => a.kind), ["close"]);
  eq("plan: ...and says why", f.planActions({ tunnels: [{ id: "1-a", state: "up" }] })[0].reason, "gone");
  deep("plan: an orphan ack is swept",
    f.planActions({ acks: [{ id: "9-z", status: "open", localPort: 1 }] }).map((a) => `${a.kind}:${a.sub}`), ["sweep:acks"]);

  const adopted = f.planActions({
    requests: [request],
    acks: [{ id: "1-a", status: "open", localPort: 19000, hostLabel: "" }],
    tunnels: [{ id: "1-a", localPort: 18800, state: "up", acked: true }],
  });
  deep("plan: somebody else's ack wins — we drop our tunnel", adopted.map((a) => a.kind), ["adopt"]);
  eq("plan: adopt names the winning port", adopted[0].localPort, 19000);

  deep("plan: LOCAL re-opens a forward whose ack outlived its window",
    f.planActions({ requests: [request], acks: [{ id: "1-a", status: "open", localPort: 18800 }], reopenAcked: true })
      .map((a) => a.kind), ["open"]);
  eq("plan: ...on the port the guest was already promised",
    f.planActions({ requests: [request], acks: [{ id: "1-a", status: "open", localPort: 18805 }], reopenAcked: true })[0].preferPort,
    18805);
  const reclaim = f.planActions({
    requests: [request], acks: [{ id: "1-a", status: "open", localPort: 18800 }], reopenAcked: false,
  });
  deep("plan: REMOTE reclaims an acked entry only conditionally", reclaim.map((a) => a.kind), ["open"]);
  eq("plan: ...on exactly the port the guest was promised, or not at all", reclaim[0].requirePort, 18800);
  eq("plan: ...never with a fallback", reclaim[0].preferPort, undefined);
  eq("plan: LOCAL prefers that port but may fall back (it re-acks)",
    f.planActions({ requests: [request], acks: [{ id: "1-a", status: "open", localPort: 18800 }], reopenAcked: true })[0].requirePort,
    undefined);

  deep("plan: several requests each get their own tunnel",
    f.planActions({ requests: [request, { id: "2-b", vmPort: 3000 }] }).map((a) => a.kind), ["open", "open"]);
  deep("plan: the second open is told which ports are already claimed",
    f.planActions({
      requests: [request, { id: "2-b", vmPort: 3000 }],
      tunnels: [{ id: "1-a", localPort: 18800, state: "up", acked: true }],
    }).filter((a) => a.kind === "open")[0].taken, [18800]);
  ok("plan: an unsafe id in the inputs is never acted on",
    f.planActions({ requests: [{ id: "../x", vmPort: 80 }], tunnels: [{ id: "../x", state: "up" }] })
      .every((a) => a.id !== "../x"));
})();

// ── The local flow ─────────────────────────────────────────────────────────────
async function localFlow() {
  console.log("\n  -- local flow --");

  // request -> tunnel -> atomic ack, in ONE reconcile.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, label: "vite dev", target: "client" }]]);
    await settle(fwd, timers);

    eq("local: one tunnel was opened", transport.tunnels.length, 1);
    eq("local: on the VM's own port number", transport.tunnels[0].localPort, 5173);
    eq("local: to the VM's port", transport.tunnels[0].vmPort, 5173);
    eq("local: the ack was written in the SAME pass", transport.acks.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    const doc = JSON.parse(Buffer.from(b64, "base64").toString("utf8"));
    deep("local: the ack is exactly the documented document",
      doc, { v: 1, id: "1-a", status: "open", localPort: 5173, message: "" });
    ok("local: the ack is published atomically", transport.acks[0].indexOf('mv -f "$tmp" "$d/1-a.json"') >= 0);

    const snap = fwd.snapshot();
    eq("local: the snapshot reports it open", snap.items[0].status, "open");
    eq("local: ...with the link the CLI will print", snap.items[0].url, "http://localhost:5173/");
    eq("local: ...and the label the agent gave it", snap.items[0].label, "vite dev");
    fwd.dispose();
  }

  // The host label ends up in the ack and in the link.
  {
    const { fwd, transport, timers } = makeForwarder({ hostLabel: "christoph-pc" });
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    eq("local: the host label rides in the ack",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).hostLabel, "christoph-pc");
    eq("local: ...and names the PC in the link", fwd.snapshot().items[0].url, "http://christoph-pc:5173/");
    fwd.dispose();
  }

  // Port fallback: the VM's port is taken on this PC.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.freePorts = [18800, 18801];
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: a busy local port falls through to the range", transport.tunnels[0].localPort, 18800);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    eq("local: the ack names the port that ACTUALLY opened",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).localPort, 18800);
    fwd.dispose();
  }

  // Nothing free at all: an honest error ack rather than a silent hang.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.freePorts = [];
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: no port free means no tunnel", transport.tunnels.length, 0);
    eq("local: ...and an error ack so `expose` stops waiting", transport.acks.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    const doc = JSON.parse(Buffer.from(b64, "base64").toString("utf8"));
    eq("local: the error ack says error", doc.status, "error");
    ok("local: ...and names the ports it tried", doc.message.indexOf("5173") >= 0 && doc.message.indexOf("18800") >= 0);
    eq("local: the snapshot shows the failure", fwd.snapshot().items[0].status, "error");
    fwd.dispose();
  }

  // Two requests, two ports, no collision.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.freePorts = [18800, 18801];
    transport.dump = dump("self", [
      ["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }],
      ["R", "2-b", { v: 1, id: "2-b", vmPort: 3000, target: "client" }],
    ]);
    await settle(fwd, timers);
    eq("local: two tunnels", transport.tunnels.length, 2);
    ok("local: on two different local ports", transport.tunnels[0].localPort !== transport.tunnels[1].localPort);
    fwd.dispose();
  }

  // A close document tears the tunnel down and removes itself.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;

    // The CLI removes the request and the ack itself and leaves the close document.
    transport.dump = dump("self", [["C", "1-a", { v: 1, id: "1-a", closedAt: "now" }]]);
    await settle(fwd, timers);

    eq("local: close kills the tunnel", child.killed, 1);
    ok("local: ...and removes the close document",
      transport.removes.some((s) => s.indexOf("close/1-a.json") >= 0));
    eq("local: nothing is left to render", fwd.snapshot().items.length, 0);
    fwd.dispose();
  }

  // A request that vanished (the CLI's --close removes it) closes the tunnel too.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    transport.dump = dump("self", []);
    await settle(fwd, timers);
    eq("local: a vanished request closes its tunnel", child.killed, 1);
    fwd.dispose();
  }

  // Re-open on activation: a queued request survived a reboot, its ack did too.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [
      ["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }],
      ["A", "1-a", { v: 1, id: "1-a", status: "open", localPort: 18805 }],
    ]);
    await settle(fwd, timers);
    eq("local: activation re-opens a request whose window is gone", transport.tunnels.length, 1);
    eq("local: ...on the port the guest was already promised", transport.tunnels[0].localPort, 18805);
    // Nothing to re-ack: the surviving ack already names that port, so the link the agent
    // printed is live again without another write. Idempotence, end to end.
    eq("local: ...and writes no redundant ack", transport.acks.length, 0);
    eq("local: the snapshot reports the restored link", fwd.snapshot().items[0].url, "http://localhost:18805/");
    fwd.dispose();
  }

  // ...but when the promised port is gone, the ack is REWRITTEN (docs/expose.md allows it).
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.freePorts = [18800];
    transport.dump = dump("self", [
      ["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }],
      ["A", "1-a", { v: 1, id: "1-a", status: "open", localPort: 18805 }],
    ]);
    await settle(fwd, timers);
    eq("local: a promised port that is now busy falls back", transport.tunnels[0].localPort, 18800);
    eq("local: ...and the ack is overwritten", transport.acks.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    eq("local: ...with the port that actually opened",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).localPort, 18800);
    fwd.dispose();
  }

  // An orphan ack with no request behind it is swept.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["A", "9-z", { v: 1, id: "9-z", status: "open", localPort: 18800 }]]);
    await settle(fwd, timers);
    ok("local: an orphan ack is removed", transport.removes.some((s) => s.indexOf("acks/9-z.json") >= 0));
    fwd.dispose();
  }

  // Ownership: another window holds the claim.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("other", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: a non-owner opens nothing", transport.tunnels.length, 0);
    eq("local: ...and writes nothing", transport.acks.length, 0);
    eq("local: ...but still renders the list read-only", fwd.snapshot().items.length, 1);
    eq("local: ...and says it is not the owner", fwd.snapshot().owner, false);

    // ...and it must not be able to CLOSE either: deleting the owner's request/ack would
    // tear down a forward the owner still believes it is serving.
    const closed = await fwd.closeForward("1-a");
    eq("local: a non-owner's Close is refused by the core", closed, false);
    eq("local: ...and touches no spool document", transport.removes.length, 0);
    eq("local: ...and the panel is told not to offer it",
      ui.toPanelForwards(fwd.snapshot()).items[0].closable, false);
    fwd.dispose();
  }

  // Losing ownership mid-flight hands the tunnels back.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    transport.dump = dump("other", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: losing the claim releases the tunnel", child.killed, 1);
    fwd.dispose();
  }

  // An unreachable VM keeps the tunnels and claims nothing.
  {
    const { fwd, transport, timers } = makeForwarder({ readCode: 255 });
    transport.dump = "";
    await settle(fwd, timers);
    eq("local: an unreachable VM writes nothing", transport.acks.length, 0);
    eq("local: ...and reports no forwards rather than inventing some", fwd.snapshot().items.length, 0);
    fwd.dispose();
  }

  // A VM that predates the feature: exactly one probe, then stand down. This is the
  // zero-change bar — an install that never got the guest side must not be polled.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = "OWNER=absent\n";
    fwd.start();
    await timers.advance(1000);
    const probes = transport.scripts.filter(isReconcileScript).length;
    eq("local: a VM with no spool is probed once", probes, 1);
    eq("local: ...and the watcher is dropped", transport.watches[0].child.killed, 1);
    await timers.advance(f.RECONCILE_MS * 4);
    eq("local: ...and never polled again",
      transport.scripts.filter(isReconcileScript).length, probes);
    eq("local: ...and nothing is rendered", fwd.snapshot().items.length, 0);
    // ...but a later activation/switch asks again rather than giving up forever.
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: a reprovisioned VM is picked up on the next start", transport.tunnels.length, 1);
    fwd.dispose();
  }

  // The watcher: a CHANGED line drives a reconcile; the claim is given back on dispose.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    fwd.start();
    await timers.advance(5000);
    eq("local: start opens exactly one watcher", transport.watches.length, 1);
    ok("local: the watcher runs the WATCH script, not the reconcile one",
      transport.watches[0].script.indexOf("inotifywait") >= 0 && !isReconcileScript(transport.watches[0].script));
    eq("local: start reconciles immediately", transport.tunnels.length, 1);

    const before = transport.scripts.length;
    transport.watches[0].child.stdout.emit("data", f.CHANGED_LINE + "\n");
    await timers.advance(1000);
    ok("local: a CHANGED line drives a reconcile", transport.scripts.length > before);

    const tunnelChild = transport.tunnels[0].child;
    const watchChild = transport.watches[0].child;
    fwd.dispose();
    await timers.flush();
    eq("local: dispose kills the tunnel", tunnelChild.killed, 1);
    eq("local: dispose kills the watcher", watchChild.killed, 1);
    ok("local: dispose gives the claim back", transport.releases.length >= 1);
    ok("local: ...only when it is still ours", transport.releases[0].indexOf('if [ "$who" = "$me" ]') >= 0);
  }

  // A dead watcher reconnects with backoff.
  {
    const { fwd, transport, timers } = makeForwarder();
    fwd.start();
    await timers.advance(5000);
    eq("local: one watcher", transport.watches.length, 1);
    transport.watches[0].child.die(255);
    await timers.advance(1999);
    eq("local: does not reconnect instantly", transport.watches.length, 1);
    await timers.advance(2);
    eq("local: reconnects after the first backoff", transport.watches.length, 2);
    fwd.dispose();
  }

  // The periodic reconcile is the whole fallback for a VM without inotify.
  {
    const { fwd, transport, timers } = makeForwarder();
    fwd.start();
    await timers.advance(1000);
    const before = transport.scripts.filter(isReconcileScript).length;
    await timers.advance(f.RECONCILE_MS + 10);
    ok("local: the 30s poll reconciles by itself",
      transport.scripts.filter(isReconcileScript).length > before);
    fwd.dispose();
  }

  // The panel's Close button.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    const p = fwd.closeForward("1-a");
    await timers.advance(5000);
    await p;
    eq("local: Close kills the tunnel", child.killed, 1);
    const removed = transport.removes.join("\n");
    ok("local: Close removes the request", removed.indexOf("requests/1-a.json") >= 0);
    ok("local: ...the ack", removed.indexOf("acks/1-a.json") >= 0);
    eq("local: an unsafe id is refused outright", await fwd.closeForward("../evil"), false);
    fwd.dispose();
  }

  // Turning a host label ON changes what the port is REACHABLE FROM, so the running ssh —
  // which captured its bind address when it spawned — has to be replaced, not just re-acked.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("local: one ack so far", transport.acks.length, 1);
    eq("local: the first tunnel binds loopback", transport.tunnels[0].bindHost, f.BIND_LOOPBACK);
    const first = transport.tunnels[0].child;

    fwd.setHostLabel("christoph-pc");
    await timers.advance(5000);

    eq("local: '' -> label KILLS the loopback-only listener", first.killed, 1);
    eq("local: ...and respawns it", transport.tunnels.length, 2);
    eq("local: ...bound to all interfaces, so the advertised name works",
      transport.tunnels[1].bindHost, f.BIND_ALL);
    eq("local: ...on the SAME local port the guest was promised",
      transport.tunnels[1].localPort, transport.tunnels[0].localPort);
    eq("local: ...and only then is the new ack written", transport.acks.length, 2);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[1])[1];
    eq("local: ...with the new label",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).hostLabel, "christoph-pc");

    // ...and the reverse, which is the security-sensitive direction: clearing the label
    // must actually STOP listening on 0.0.0.0, not merely re-advertise localhost.
    const second = transport.tunnels[1].child;
    fwd.setHostLabel("");
    await timers.advance(5000);
    eq("local: label -> '' kills the wildcard listener", second.killed, 1);
    eq("local: ...and respawns it", transport.tunnels.length, 3);
    eq("local: ...back on loopback only — the user opted OUT",
      transport.tunnels[2].bindHost, f.BIND_LOOPBACK);
    eq("local: ...still on the same local port", transport.tunnels[2].localPort, transport.tunnels[0].localPort);
    const b64b = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[transport.acks.length - 1])[1];
    ok("local: ...and the ack drops the host label",
      !("hostLabel" in JSON.parse(Buffer.from(b64b, "base64").toString("utf8"))));
    fwd.dispose();
  }

  // Label -> different label: the bind address does not change, so this is a re-ack only.
  // Killing a working tunnel to change the text of a link would be pure churn.
  {
    const { fwd, transport, timers } = makeForwarder({ hostLabel: "pc-one" });
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    eq("local: a labelled forward binds all interfaces from the start",
      transport.tunnels[0].bindHost, f.BIND_ALL);

    fwd.setHostLabel("pc-two");
    await timers.advance(5000);
    eq("local: label -> label does NOT restart the tunnel", child.killed, 0);
    eq("local: ...and spawns nothing new", transport.tunnels.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[transport.acks.length - 1])[1];
    eq("local: ...it only re-acks the new name",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).hostLabel, "pc-two");
    fwd.dispose();
  }
}

// ── Tunnel supervision ─────────────────────────────────────────────────────────
async function supervision() {
  console.log("\n  -- tunnel supervision --");

  // Dies inside the settle window: it never opened the port -> an error ack.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    fwd.start();
    await timers.flush();
    eq("supervision: the tunnel was spawned and is still settling", transport.tunnels.length, 1);
    transport.tunnels[0].child.stderr.emit("data", "bind: Address already in use\n");
    transport.tunnels[0].child.die(255);
    await timers.advance(5000);

    eq("supervision: an immediate death is a failure, not a restart", transport.tunnels.length, 1);
    eq("supervision: ...reported as an error ack", transport.acks.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    const doc = JSON.parse(Buffer.from(b64, "base64").toString("utf8"));
    eq("supervision: the ack says error", doc.status, "error");
    ok("supervision: ...and carries ssh's own reason", doc.message.indexOf("Address already in use") >= 0);
    fwd.dispose();
  }

  // Dies after the settle window: restart on the SAME port, with backoff.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("supervision: up and acked", transport.acks.length, 1);

    transport.tunnels[0].child.die(255);
    await timers.advance(1999);
    eq("supervision: a later death waits out the backoff", transport.tunnels.length, 1);
    await timers.advance(2);
    eq("supervision: then respawns", transport.tunnels.length, 2);
    eq("supervision: on the SAME local port the ack promised",
      transport.tunnels[1].localPort, transport.tunnels[0].localPort);
    fwd.dispose();
  }

  // Persistent failure eventually tells the guest.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    // Each death is immediately after the respawn, so the "it lived a minute, forget the
    // streak" rule never fires and the attempts really do accumulate.
    for (let i = 1; i <= f.MAX_TUNNEL_ATTEMPTS + 1; i++) {
      const last = transport.tunnels[transport.tunnels.length - 1];
      last.child.die(255);
      await timers.advance(f.reconnectDelayMs(i) + 20);
    }
    ok("supervision: a tunnel that keeps dropping is reported as an error",
      transport.acks.some((s) => {
        const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(s)[1];
        return JSON.parse(Buffer.from(b64, "base64").toString("utf8")).status === "error";
      }));
    fwd.dispose();
  }

  // THE STREAK MUST SURVIVE THE SETTLE WINDOW. A tunnel that dies just after settling —
  // repeatedly — is provably broken, and if merely settling reset `attempt` it would retry
  // at 2 s forever and never tell the guest, which would then wait out its whole timeout.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers, 1500);   // just past the settle window, well short of healthy
    eq("supervision: up after settling", transport.tunnels.length, 1);
    for (let i = 1; i <= f.MAX_TUNNEL_ATTEMPTS + 1; i++) {
      transport.tunnels[transport.tunnels.length - 1].child.die(255);
      // Each respawn survives the settle window and then dies: never healthy, always > settle.
      await timers.advance(f.reconnectDelayMs(i) + f.TUNNEL_SETTLE_MS + 50);
    }
    ok("supervision: repeated post-settle drops DO reach the persistent-failure ack",
      transport.acks.some((sc) => {
        const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(sc)[1];
        return JSON.parse(Buffer.from(b64, "base64").toString("utf8")).status === "error";
      }));
    fwd.dispose();
  }

  // ...while a tunnel that really was healthy for a minute does forget its streak.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    transport.tunnels[0].child.die(255);
    await timers.advance(2050);
    eq("supervision: respawned once", transport.tunnels.length, 2);
    await timers.advance(f.CONNECTION_HEALTHY_MS + 1000);   // it lived a minute
    transport.tunnels[1].child.die(255);
    // A healthy connection resets the streak, so the next retry is the FAST 2s one again
    // rather than the doubled delay the previous drop would otherwise have earned.
    await timers.advance(1900);
    eq("supervision: a healthy connection resets the backoff", transport.tunnels.length, 2);
    await timers.advance(200);
    eq("supervision: ...retrying quickly rather than at the old delay", transport.tunnels.length, 3);
    fwd.dispose();
  }

  // Dispose stops the restart timers too.
  {
    const { fwd, transport, timers } = makeForwarder();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    transport.tunnels[0].child.die(255);
    await timers.flush();
    fwd.dispose();
    await timers.advance(120000);
    eq("supervision: dispose cancels a pending restart", transport.tunnels.length, 1);
  }

  // A spawn that throws is a failure, not a crash.
  {
    const timers = makeTimers();
    const transport = makeTransport();
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    transport.spawnTunnel = () => { throw new Error("ENOENT ssh"); };
    const fwd = f.createForwarder({
      instance: { name: "agent-vm" }, transport, timers: timers.api, now: timers.now, windowId: "w",
    });
    await settle(fwd, timers);
    eq("supervision: a spawn failure becomes an error ack", transport.acks.length, 1);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    ok("supervision: ...naming the spawn error",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).message.indexOf("ENOENT") >= 0);
    fwd.dispose();
  }
}

// ── The remote flow ────────────────────────────────────────────────────────────
async function remoteFlow() {
  console.log("\n  -- remote flow --");

  const entry = (over) => Object.assign({
    id: "fwd-1", vmName: "work-vm", vmPort: 5173, publicPort: null,
    target: "client", label: "vite", created: "2026-09-01T09:00:00Z", url: null,
  }, over || {});

  // poll -> tunnel -> POST ack.
  {
    const { fwd, transport, timers } = makeForwarder({
      backend: "hyperv-remote",
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry()]];
    await settle(fwd, timers);

    eq("remote: the list was polled", transport.fetches[0].method, "GET");
    eq("remote: ...on the VM's forward route", transport.fetches[0].path, "/vms/work-vm/forwards");
    eq("remote: a tunnel was opened", transport.tunnels.length, 1);
    eq("remote: on the VM's own port", transport.tunnels[0].localPort, 5173);

    const post = transport.fetches.find((r) => r.method === "POST");
    ok("remote: the ack was POSTed", !!post);
    eq("remote: ...to the ack route", post.path, "/vms/work-vm/forwards/fwd-1/ack");
    deep("remote: ...with the documented body", post.body, { status: "open", localPort: 5173 });
    eq("remote: nothing was written on the VM", transport.acks.length, 0);
    eq("remote: no spool watcher exists in remote mode", transport.watches.length, 0);
    fwd.dispose();
  }

  // The host label rides along.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
      hostLabel: "christoph-pc",
    });
    transport.lists = [[entry()]];
    await settle(fwd, timers);
    const post = transport.fetches.find((r) => r.method === "POST");
    deep("remote: the ack body carries the host label",
      post.body, { status: "open", localPort: 5173, hostLabel: "christoph-pc" });
    fwd.dispose();
  }

  // An entry that left the list is closed.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry()], []];
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    await settle(fwd, timers);
    eq("remote: a forward that left the list kills its tunnel", child.killed, 1);
    eq("remote: ...and nothing is rendered", fwd.snapshot().items.length, 0);
    fwd.dispose();
  }

  // An entry acked on a port that is BUSY here: somebody is serving it, leave it alone.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.freePorts = [];   // 18800 is taken on this PC
    transport.lists = [[entry({ status: "open", localPort: 18800, hostLabel: "other-pc", url: "http://other-pc:18800/" })]];
    await settle(fwd, timers);
    eq("remote: an acked entry on a busy port opens no competing tunnel", transport.tunnels.length, 0);
    ok("remote: ...and posts no competing ack", !transport.fetches.some((r) => r.method === "POST"));
    const item = fwd.snapshot().items[0];
    eq("remote: ...but is rendered with the link that exists", item.url, "http://other-pc:18800/");
    fwd.dispose();
  }

  // ...and one acked on a port that is FREE here: the window serving it has gone, so
  // reclaim it on exactly that port and the guest's printed link works again.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.freePorts = [18800];
    transport.lists = [[entry({ status: "open", localPort: 18800 })]];
    await settle(fwd, timers);
    eq("remote: an orphaned forward is reclaimed", transport.tunnels.length, 1);
    eq("remote: ...on exactly the promised port", transport.tunnels[0].localPort, 18800);
    // No POST: the surviving ack already says exactly this, so the guest's link is live
    // again without a single write. The same idempotence as the local re-open.
    ok("remote: ...and needs no re-ack", !transport.fetches.some((r) => r.method === "POST"));
    eq("remote: ...and the link is rendered as open", fwd.snapshot().items[0].url, "http://localhost:18800/");
    fwd.dispose();
  }

  // A reclaim never falls back: a different port would break the link the guest holds.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.freePorts = [18801, 5173];   // everything EXCEPT the promised port
    transport.lists = [[entry({ status: "open", localPort: 18800 })]];
    await settle(fwd, timers);
    eq("remote: a reclaim does not settle for another port", transport.tunnels.length, 0);
    ok("remote: ...and rewrites no ack", !transport.fetches.some((r) => r.method === "POST"));
    fwd.dispose();
  }

  // An entry the service reports as CLOSED is not a request. Reading it as one would
  // re-open a forward the guest deliberately closed.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry()], [entry({ status: "closed" })]];
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    eq("remote: the forward was served while it was live", transport.tunnels.length, 1);
    await settle(fwd, timers);
    eq("remote: a closed entry kills its tunnel", child.killed, 1);
    eq("remote: ...and is not re-opened", transport.tunnels.length, 1);
    eq("remote: ...and is not rendered as live", fwd.snapshot().items.length, 0);
    fwd.dispose();
  }

  // `state: "closed"` is honoured too — the field name is not something to bet on.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry({ state: "closed" })]];
    await settle(fwd, timers);
    eq("remote: a `state:closed` entry opens nothing", transport.tunnels.length, 0);
    fwd.dispose();
  }

  ok("remote list: a closed client entry becomes a close, not a request", (() => {
    const read = f.readForwardList([
      { id: "a", vmPort: 1, target: "client", status: "closed" },
      { id: "b", vmPort: 2, target: "client", state: "closed" },
    ]);
    return read.requests.length === 0 && read.closes.length === 2;
  })());
  ok("remote list: host entries are kept for presentation, never as requests", (() => {
    const read = f.readForwardList([
      { id: "h", vmPort: 8080, target: "host", publicPort: 31234, url: "http://x:31234/" },
    ]);
    return read.requests.length === 0 && read.host.length === 1 && read.host[0].publicPort === 31234;
  })());

  // An error-acked entry is a final answer: no reclaim, no churn.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry({ status: "error", message: "no free port" })]];
    await settle(fwd, timers);
    eq("remote: an error-acked entry is not retried", transport.tunnels.length, 0);
    eq("remote: ...and is rendered as the failure it is", fwd.snapshot().items[0].status, "error");
    fwd.dispose();
  }

  // Another window overwrote our ack: drop our tunnel rather than fight.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [
      [entry()],
      [entry({ status: "open", localPort: 19000, hostLabel: "other-pc" })],
    ];
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    await settle(fwd, timers);
    eq("remote: a foreign ack releases our tunnel", child.killed, 1);
    fwd.dispose();
  }

  // Host targets are the service's business, not ours.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry({ target: "host", publicPort: 31234, url: "http://buildbox:31234/" })]];
    await settle(fwd, timers);
    eq("remote: a host forward is not tunnelled here", transport.tunnels.length, 0);
    ok("remote: ...and never acked (the service answers 409)",
      !transport.fetches.some((r) => r.method === "POST"));
    // ...but it IS this VM's forward, so the panel must be able to show, open and close it.
    const host = fwd.snapshot().items;
    eq("remote: a host forward is still rendered", host.length, 1);
    eq("remote: ...marked as a host target", host[0].target, "host");
    eq("remote: ...with the service's own URL", host[0].url, "http://buildbox:31234/");
    eq("remote: ...and the public port it was given", host[0].localPort, 31234);
    fwd.dispose();
  }

  // An unreachable service reports nothing rather than "no forwards".
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry()]];
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    transport.fetchFail = "connect ECONNREFUSED";
    await settle(fwd, timers);
    eq("remote: a failed poll keeps the tunnel this PC is serving", child.killed, 0);
    eq("remote: ...and keeps rendering what it knows", fwd.snapshot().items.length, 1);
    fwd.dispose();
  }

  // The Close button is a DELETE, because the service owns the record.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[entry()]];
    await settle(fwd, timers);
    const child = transport.tunnels[0].child;
    const p = fwd.closeForward("fwd-1");
    await timers.advance(5000);
    await p;
    const del = transport.fetches.find((r) => r.method === "DELETE");
    ok("remote: Close deletes the forward on the service", !!del);
    eq("remote: ...on the right route", del.path, "/vms/work-vm/forwards/fwd-1");
    eq("remote: ...and kills the tunnel", child.killed, 1);
    fwd.dispose();
  }

  // The remote poll runs on its own, faster than the local one.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[]];
    fwd.start();
    await timers.advance(1000);
    const before = transport.fetches.length;
    await timers.advance(f.REMOTE_POLL_MS + 10);
    ok("remote: the 10s poll runs by itself", transport.fetches.length > before);
    fwd.dispose();
  }

  // The list parser's own guards.
  {
    const read = f.readForwardList([
      entry(),
      entry({ id: "../evil" }),
      entry({ id: "fwd-2", vmPort: 0 }),
      entry({ id: "fwd-3", target: "host" }),
      entry({ id: "fwd-4", status: "error", message: "no port" }),
      null,
      "nonsense",
    ]);
    deep("remote list: only usable client entries survive",
      read.requests.map((r) => r.id), ["fwd-1", "fwd-4"]);
    deep("remote list: the inline ack is read", read.acks.map((a) => a.status), ["error"]);
    eq("remote list: ...with its message", read.acks[0].message, "no port");
    deep("remote list: a non-array is empty, not a throw", f.readForwardList(null).requests, []);
  }
}

// ── Snapshot + UI projections ──────────────────────────────────────────────────
(() => {
  const snap = f.toSnapshot({
    mode: "local",
    requests: [{ id: "b", vmPort: 5173, label: "vite" }, { id: "a", vmPort: 3000, label: "api" }],
    acks: [{ id: "b", status: "open", localPort: 18800, hostLabel: "pc" }],
  });
  deep("snapshot: sorted by VM port, so the list does not jump", snap.items.map((i) => i.vmPort), [3000, 5173]);
  eq("snapshot: an unacked request is queued, which is not a fault", snap.items[0].status, "queued");
  eq("snapshot: ...and has no link to offer", snap.items[0].url, null);
  eq("snapshot: an acked one is open", snap.items[1].status, "open");
  eq("snapshot: ...with the host label in the link", snap.items[1].url, "http://pc:18800/");

  eq("snapshot: an error ack surfaces its message",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], acks: [{ id: "a", status: "error", message: "busy" }] })
      .items[0].message, "busy");
  eq("snapshot: a live tunnel with no ack yet still reports the real port",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], tunnels: [{ id: "a", localPort: 18800, state: "up" }] })
      .items[0].url, "http://localhost:18800/");
  eq("snapshot: a closing forward is not rendered",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], closes: ["a"] }).items.length, 0);

  const local = ui.toPanelForwards({ mode: "local", owner: true, items: [] });
  eq("panel: an untouched LOCAL install shows nothing new", local.visible, false);
  eq("panel: a local install with a forward shows the card",
    ui.toPanelForwards({ mode: "local", items: [{ id: "a", vmPort: 1 }] }).visible, true);
  eq("panel: a REMOTE instance always shows the card",
    ui.toPanelForwards({ mode: "remote", items: [] }).visible, true);
  eq("panel: the mode is carried", ui.toPanelForwards({ mode: "remote", items: [] }).mode, "remote");
  eq("panel: non-ownership is carried (read-only rendering)",
    ui.toPanelForwards({ mode: "local", owner: false, items: [{ id: "a", vmPort: 1 }] }).owner, false);
  deep("panel: an item carries exactly the fields the webview renders",
    Object.keys(ui.toPanelForwards({ mode: "local", items: [{ id: "a", vmPort: 1 }] }).items[0]).sort(),
    ["closable", "id", "label", "localPort", "message", "status", "target", "url", "vmPort"]);
  eq("panel: the owner may close",
    ui.toPanelForwards({ mode: "local", owner: true, items: [{ id: "a", vmPort: 1 }] }).items[0].closable, true);
  eq("panel: a local NON-OWNER may not close (the core refuses it too)",
    ui.toPanelForwards({ mode: "local", owner: false, items: [{ id: "a", vmPort: 1 }] }).items[0].closable, false);
  eq("panel: remotely the service is the authority, so any window may close",
    ui.toPanelForwards({ mode: "remote", owner: false, items: [{ id: "a", vmPort: 1 }] }).items[0].closable, true);
  eq("panel: a host-target forward is rendered as such",
    ui.toPanelForwards({ mode: "remote", items: [{ id: "a", vmPort: 1, target: "host" }] }).items[0].target, "host");
  eq("panel: a missing snapshot is not a crash", ui.toPanelForwards(null).visible, false);

  deep("idle: within the cap, nothing is clamped",
    ui.clampIdlePolicy({ timeoutMinutes: 60, action: "save" }, 120),
    { timeoutMinutes: 60, action: "save", clamped: false });
  deep("idle: over the cap is clamped, and says so",
    ui.clampIdlePolicy({ timeoutMinutes: 500, action: "shutdown" }, 120),
    { timeoutMinutes: 120, action: "shutdown", clamped: true });
  eq("idle: cap 0 means no cap (the service's own default)",
    ui.clampIdlePolicy({ timeoutMinutes: 5000, action: "save" }, 0).timeoutMinutes, 5000);
  eq("idle: an unknown action falls back to the service default", ui.normalizeIdleAction("explode"), "save");
  eq("idle: 'off' survives", ui.normalizeIdleAction("OFF"), "off");
  eq("idle: a negative timeout reads as off", ui.clampIdlePolicy({ timeoutMinutes: -5 }, 0).timeoutMinutes, 0);

  deep("idle: the service's response becomes the panel's state",
    ui.toPanelIdlePolicy({ timeoutMinutes: 45, action: "shutdown", maxTimeoutMinutes: 120, clamped: false }),
    { timeoutMinutes: 45, action: "shutdown", maxTimeoutMinutes: 120, clamped: false });
  eq("idle: a local instance has no policy to show", ui.toPanelIdlePolicy(null), null);
  eq("idle: a nonsense body is no policy", ui.toPanelIdlePolicy({ timeoutMinutes: "soon" }), null);

  deep("power: running offers shutdown", ui.powerIntentFor("running"), { cmd: "shutdown", kind: "shutdown" });
  deep("power: off offers a start", ui.powerIntentFor("off"), { cmd: "startConnect", kind: "start" });
  deep("power: SAVED offers a RESUME — the same call, an honest label",
    ui.powerIntentFor("saved"), { cmd: "startConnect", kind: "resume" });
  deep("power: unknown still offers a start (the probe is permission-gated)",
    ui.powerIntentFor("unknown"), { cmd: "startConnect", kind: "start" });
  deep("power: absent offers nothing", ui.powerIntentFor("absent"), { cmd: "", kind: "none" });
})();

// ── ssh.js's new argv builder ──────────────────────────────────────────────────
(() => {
  const alias = ssh.buildLocalForwardArgs({}, 18800, 5173, false);
  ok("ssh -L: forward only, no remote command", alias.indexOf("-N") >= 0);
  eq("ssh -L: binds this PC's loopback EXPLICITLY, never ssh's default",
    alias[alias.indexOf("-L") + 1], "127.0.0.1:18800:127.0.0.1:5173");
  eq("ssh -L: a host label opts in to all interfaces so the advertised link works",
    ssh.buildLocalForwardArgs({}, 18800, 5173, false, { bindHost: ssh.FORWARD_BIND_ALL })
      .find((a, i, all) => all[i - 1] === "-L"),
    "0.0.0.0:18800:127.0.0.1:5173");
  eq("ssh -L: an unrecognised bind address falls back to loopback, never to a guess",
    ssh.buildLocalForwardArgs({}, 18800, 5173, false, { bindHost: "10.0.0.5; rm -rf /" })
      .find((a, i, all) => all[i - 1] === "-L"),
    "127.0.0.1:18800:127.0.0.1:5173");
  eq("bind host: only the two allowed values exist", ssh.normalizeBindHost("evil"), ssh.FORWARD_BIND_LOOPBACK);
  eq("bind host: '*' is the all-interfaces spelling too", ssh.normalizeBindHost("*"), ssh.FORWARD_BIND_ALL);
  ok("ssh -L: fails fast when the local port is taken", alias.indexOf("ExitOnForwardFailure=yes") >= 0);
  ok("ssh -L: keepalives, so a dead link makes the child exit", alias.indexOf("ServerAliveInterval=15") >= 0);
  eq("ssh -L: the alias branch ends with the host alias", alias[alias.length - 1], "agent-vm");
  ok("ssh -L: no -p for the default port", alias.indexOf("-p") < 0);

  const keyed = ssh.buildLocalForwardArgs({ vmHost: "vm2.local", sshPort: 2201, keyName: "k" }, 1, 2, true);
  eq("ssh -L: the key branch leads with -i", keyed[0], "-i");
  ok("ssh -L: ...and pins the identity", keyed.indexOf("IdentitiesOnly=yes") >= 0);
  ok("ssh -L: a forwarded ssh port is emitted", keyed.indexOf("-p") >= 0 && keyed[keyed.indexOf("-p") + 1] === "2201");
  eq("ssh -L: ...and the target is user@host", keyed[keyed.length - 1], "root@vm2.local");

  eq("ssh -L: a bad local port is refused, never redirected", (() => {
    try { ssh.buildLocalForwardArgs({}, 0, 5173, false); return "no throw"; } catch (_) { return "threw"; }
  })(), "threw");
  eq("ssh -L: a bad VM port is refused too", (() => {
    try { ssh.buildLocalForwardArgs({}, 18800, 70000, false); return "no throw"; } catch (_) { return "threw"; }
  })(), "threw");
  eq("forward port: a numeric string is accepted", ssh.normalizeForwardPort("5173"), 5173);
  eq("forward port: garbage is null, not 22", ssh.normalizeForwardPort("x"), null);
  eq("forward port: 0 is null", ssh.normalizeForwardPort(0), null);

  const stream = ui.buildStreamArgs(ssh, {}, false, "echo hi");
  ok("stream argv: no tty — it is a data stream", stream.indexOf("-T") >= 0);
  ok("stream argv: keepalives so a dead link is noticed", stream.indexOf("ServerAliveInterval=20") >= 0);
  ok("stream argv: the script rides base64-wrapped", stream[stream.length - 1].indexOf("base64 -d") >= 0);
  ok("stream argv: no -p for the default port", stream.indexOf("-p") < 0);
  ok("stream argv: a forwarded port is emitted",
    ui.buildStreamArgs(ssh, { sshPort: 2201 }, false, "x").indexOf("-p") >= 0);
})();

// ── Summary ────────────────────────────────────────────────────────────────────
(async () => {
  await claimProtocol();
  await localFlow();
  await supervision();
  await remoteFlow();
  console.log(`\n  forwarder unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
