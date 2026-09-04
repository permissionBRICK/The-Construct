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
//     close → kill, port fallback, re-open on connect, and read-only non-ownership;
//   • the REMOTE flow against a fake fetch — poll → tunnel → POST ack, entry removed →
//     kill, and an acked entry left alone;
//   • tunnel supervision — the settle window, restart with backoff, the error ack after
//     persistent failure, dispose;
//   • the LAZY START — the capability check is the first thing to touch the VM, an older
//     guest gets no watcher and no reconcile at all, and planLifecycle's start/stop rule;
//   • a MODEL of extension.js's forwarder wiring (the slot claim, the serialized chain and
//     the status trigger) around REAL Forwarder sessions with BOTH awaits deferred — the
//     transport build and the guest capability check: disable, deactivation, an
//     unreachable reading and a switch during either of them, overlapping starts, A→B→A
//     and the reconnect edge all end with one session (or none), zero orphans and zero
//     watcher/reconcile spawn after a stop — each with a PRE-FIX control that reproduces
//     the leak;
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
const inst = require("../src/instances");

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

const isCapabilityScript = (s) => s.indexOf('printf \'SPOOL=1') >= 0;
const isReconcileScript = (s) => s.indexOf("dump R requests") >= 0;
const isAckScript = (s) => s.indexOf("base64 -d") >= 0;
const isRemoveScript = (s) => /^set -u\nrm -f /.test(s);
const isReleaseScript = (s) => s.indexOf('rm -f "$own"') >= 0;

function makeTransport(opts = {}) {
  const t = {
    scripts: [], acks: [], removes: [], releases: [], capabilities: [],
    watches: [], tunnels: [], fetches: [],
    dump: dump("self", []),
    /** The guest's answer to the capability check: does it have the spool contract?
     *  `opts.spool: false` is the older-VM path, `opts.spoolCode` a check that failed. */
    spool: opts.spool !== false,
    /** null = every port is free; otherwise only these are. */
    freePorts: null,
    /** Successive answers for GET .../forwards. The last one repeats. */
    lists: [[]],
    listIndex: 0,
    fetchFail: null,

    runRemoteScript(script) {
      t.scripts.push(script);
      if (isCapabilityScript(script)) {
        t.capabilities.push(script);
        return Promise.resolve({
          code: opts.spoolCode || 0,
          stdout: t.spool ? f.SPOOL_YES + "\n" : f.SPOOL_NO + "\n",
          stderr: "",
        });
      }
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
  // start() is LAZY: its first act is the one-shot capability check, and the watcher/poll
  // only follow once the guest has answered. Awaited here so the tests below see the
  // started state rather than a half-started one.
  if (fwd._stopped) await fwd.start();
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

  eq("hostLabel: a plain machine name survives", f.sanitizeHostLabel("alice-pc"), "alice-pc");
  eq("hostLabel: an FQDN survives", f.sanitizeHostLabel("pc.home.example"), "pc.home.example");
  eq("hostLabel: whitespace is removed, not a separator", f.sanitizeHostLabel("  pc  "), "pc");
  eq("hostLabel: a slash is refused outright", f.sanitizeHostLabel("pc/evil"), "");
  eq("hostLabel: an @ (credential smuggling) is refused", f.sanitizeHostLabel("evil.test@pc"), "");
  eq("hostLabel: a query/fragment is refused", f.sanitizeHostLabel("pc?a=b"), "");
  eq("hostLabel: empty is the default", f.sanitizeHostLabel(""), "");
  eq("hostLabel: undefined is the default", f.sanitizeHostLabel(undefined), "");

  // ONE WIRE REPRESENTATION, ONE BRACKETING PLACE (docs/expose.md). The label travels bare
  // and is bracketed only when a URL is built — the two used to disagree, so an accepted
  // "[fe80::1]" rendered http://[[fe80::1]]:5173/ here while the service rendered the bare
  // one as http://fe80::1:5173/, and neither link opens.
  eq("hostLabel: an IPv6 literal is canonically BARE", f.sanitizeHostLabel("fe80::1"), "fe80::1");
  eq("hostLabel: ...and a bracketed one is accepted and unwrapped to it",
    f.sanitizeHostLabel("[fe80::1]"), "fe80::1");
  eq("hostLabel: ...compressed or full, same rule",
    f.sanitizeHostLabel("[2001:db8::8a2e:370:7334]"), "2001:db8::8a2e:370:7334");
  eq("hostLabel: an IPv4-mapped literal is a literal too",
    f.sanitizeHostLabel("::ffff:10.0.0.1"), "::ffff:10.0.0.1");
  // THE SHARED FIXTURE MATRIX. One rule, three implementations — this module's `net.isIP`,
  // `is_ipv6_literal` in bin/construct-expose.sh and `ForwardHost` in the service — and they
  // are only one rule while they agree address for address, so the SAME list runs in
  // test/construct-expose.test.sh and in Constructd.Tests' ForwardHostTests.
  //
  // Every entry is PARSED, never character-classed. The four marked (*) are the ones a
  // shape filter waves through — they are what a "plausible IPv6" test accepts and what a
  // real parser refuses, and each of them would otherwise reach a URL authority.
  const IPV6_LABELS_VALID = [
    "::", "::1", "fe80::1", "2001:db8::8a2e:370:7334", "1:2:3:4:5:6:7:8",
    "0:0:0:0:0:0:0:0", "1::", "::2", "0::0", "::ffff:10.0.0.1",
    "1:2:3:4:5:6:1.2.3.4", "::1.2.3.4", "1::1.2.3.4", "1:2:3:4:5:6:7::",
    "fe80::0204:61ff:fe9d:f156", "ABCD::1",
  ];
  const IPV6_LABELS_INVALID = [
    "::::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1.2.3:4", "....:", ":::",
    ":1", "1:", "12345::1" /* (*) */, "::ffff:999.1.1.1", "::ffff:1.2.3.004",
    "1:2" /* (*) */, "1:2:3:4:5:6:7" /* (*) */, "1:::" /* (*) */,
    "1::2:3:4:5:6:7:8", "::1.2.3.4.5", "1:2:3:4:5:6:7:1.2.3.4",
    // An embedded IPv4 address is the literal's FINAL 32 bits, so it can never appear
    // before the "::" — the grammar boundary a per-run "quad must be last" check misses.
    "192.0.2.1::", "192.0.2.1::1", "1:192.0.2.1::", "1.2.3.4::1:2",
  ];
  for (const good of IPV6_LABELS_VALID) {
    eq(`hostLabel[matrix]: ${JSON.stringify(good)} is an address, kept bare`,
      f.sanitizeHostLabel(good), good);
    eq(`hostLabel[matrix]: ${JSON.stringify(good)} bracketed is the same address`,
      f.sanitizeHostLabel(`[${good}]`), good);
    eq(`urlHost[matrix]: ${JSON.stringify(good)} gets exactly one bracket pair`,
      f.urlHostFor(good), `[${good}]`);
  }
  for (const bad of IPV6_LABELS_INVALID) {
    eq(`hostLabel[matrix]: ${JSON.stringify(bad)} is not an address, so it is refused`,
      f.sanitizeHostLabel(bad), "");
    eq(`urlHost[matrix]: ...and never reaches a URL authority`, f.urlHostFor(bad), "");
  }
  for (const bad of ["[]", "[fe80::1", "fe80::1]"]) {
    eq(`hostLabel: ${JSON.stringify(bad)} is not an address, so it is refused`,
      f.sanitizeHostLabel(bad), "");
  }
  eq("hostLabel: a zone id is refused (a URL would need %25, and a zone is local to one PC)",
    f.sanitizeHostLabel("fe80::1%eth0"), "");
  eq("hostLabel: ...bracketed too", f.sanitizeHostLabel("[fe80::1%25eth0]"), "");
  eq("hostLabel: nested brackets are not a label", f.sanitizeHostLabel("[[fe80::1]]"), "");

  eq("urlHost: a name is used as it stands", f.urlHostFor("alice-pc"), "alice-pc");
  eq("urlHost: an IPv4 literal too", f.urlHostFor("10.0.0.7"), "10.0.0.7");
  eq("urlHost: an IPv6 literal gets exactly one bracket pair", f.urlHostFor("fe80::1"), "[fe80::1]");
  eq("urlHost: ...and a bracketed one gets exactly one pair as well, never two",
    f.urlHostFor("[fe80::1]"), "[fe80::1]");
  eq("urlHost: an unusable label is empty, so the caller says localhost", f.urlHostFor("pc/evil"), "");
  eq("urlHost: ...as is an absent one", f.urlHostFor(""), "");

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
    f.bindHostFor("alice-pc"), f.BIND_ALL);
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
  // The ONE wire representation: what the guest CLI and the service read is the bare
  // literal, whichever spelling the setting was typed in.
  eq("ack document: an IPv6 host label goes on the wire BARE",
    f.ackDocument("1-a", { status: "open", localPort: 18800, hostLabel: "[fe80::1]" }).hostLabel,
    "fe80::1");
  ok("ack document: an unusable host label is omitted, not half-encoded",
    !("hostLabel" in f.ackDocument("1-a", { status: "open", localPort: 18800, hostLabel: "fe80::1%eth0" })));

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

  // The capability check — the FIRST thing this module runs on a VM, and the whole reason
  // an older guest never sees a watcher. It tests the exact marker bin/provision.sh
  // writes: `install -d` of the spool root plus requests/, acks/ and close/, all four.
  const cap = f.buildCapabilityScript({ dir: "/etc/construct/forwards" });
  ok("capability: tests the spool root", cap.indexOf('[ -d "$d" ]') >= 0);
  ok("capability: ...and all three subdirectories provision.sh creates",
    cap.indexOf('[ -d "$d/requests" ]') >= 0 && cap.indexOf('[ -d "$d/acks" ]') >= 0 &&
    cap.indexOf('[ -d "$d/close" ]') >= 0);
  ok("capability: the spool path is one quoted literal", cap.indexOf("d='/etc/construct/forwards'") >= 0);
  ok("capability: 'this VM does not have it' is an ANSWER, not a failure",
    cap.indexOf(`printf '${f.SPOOL_NO}`) >= 0 && cap.trim().endsWith("exit 0"));
  ok("capability: it is cheap — no watcher, no dump, no writes",
    cap.indexOf("inotifywait") < 0 && cap.indexOf("base64") < 0 && cap.indexOf("mkdir") < 0);
  ok("capability: a hostile dir stays one quoted literal",
    f.buildCapabilityScript({ dir: "/tmp/x'; rm -rf /; echo '" }).indexOf("\nrm -rf") < 0);

  ok("capability: SPOOL=1 is a yes", f.parseCapability("SPOOL=1\n"));
  ok("capability: ...even with noise around it", f.parseCapability("motd line\nSPOOL=1\n"));
  ok("capability: SPOOL=0 is a no", !f.parseCapability("SPOOL=0\n"));
  ok("capability: an empty answer is a no, never an assumed yes", !f.parseCapability(""));
  ok("capability: a null answer is a no", !f.parseCapability(null));
  ok("capability: a truncated answer is a no", !f.parseCapability("SPOOL="));
})();

// ── The lazy, guest-gated lifecycle rule (planLifecycle) ───────────────────────
// The forwarder is not started by activation; it is started by what the window's existing
// status flow just learned about the captured instance. The decision is pure, so the whole
// rule — including the deliberate asymmetry between starting and stopping — is tested here
// rather than only observable in a live window.
(() => {
  const plan = (o) => f.planLifecycle(o);
  deep("lifecycle: a VM the status flow reached is served",
    plan({ enabled: true, name: "agent-vm", armed: null, online: true, vmState: "running" }),
    { action: "start", reason: "reachable" });
  deep("lifecycle: ...but only ONCE per connect (an older guest is not re-probed every tick)",
    plan({ enabled: true, name: "agent-vm", armed: "agent-vm", online: true, vmState: "running" }),
    { action: "none", reason: "armed" });
  deep("lifecycle: a reading for ANOTHER instance starts that one",
    plan({ enabled: true, name: "work-vm", armed: "agent-vm", online: true, vmState: "running" }),
    { action: "start", reason: "reachable" });
  deep("lifecycle: nothing is started before the VM is known reachable",
    plan({ enabled: true, name: "agent-vm", armed: null, online: false, vmState: "unknown" }),
    { action: "none", reason: "unreachable" });
  for (const state of ["off", "saved", "absent"]) {
    deep(`lifecycle: a VM that is ${state} lets the forwarder go`,
      plan({ enabled: true, name: "agent-vm", armed: "agent-vm", online: false, vmState: state }),
      { action: "stop", reason: state });
    deep(`lifecycle: ...and says nothing when there is nothing to let go of (${state})`,
      plan({ enabled: true, name: "agent-vm", armed: null, online: false, vmState: state }),
      { action: "none", reason: state });
  }
  // REACHABILITY IS THE RULE IN BOTH DIRECTIONS: a reading that did not reach the VM lets
  // the forwarder go whatever the host-side power state says, and clears the armed edge so
  // the next reading that DOES reach it is a reconnect that starts a fresh session.
  deep("lifecycle: a VM that did not answer is let go even if the host says running",
    plan({ enabled: true, name: "agent-vm", armed: "agent-vm", online: false, vmState: "running" }),
    { action: "stop", reason: "unreachable" });
  deep("lifecycle: ...and with an unknown state",
    plan({ enabled: true, name: "agent-vm", armed: "agent-vm", online: false, vmState: "unknown" }),
    { action: "stop", reason: "unreachable" });
  deep("lifecycle: ...but there is nothing to let go of when nothing is armed",
    plan({ enabled: true, name: "agent-vm", armed: null, online: false, vmState: "running" }),
    { action: "none", reason: "unreachable" });
  deep("lifecycle: the setting off releases what is held",
    plan({ enabled: false, name: "agent-vm", armed: "agent-vm", online: true, vmState: "running" }),
    { action: "stop", reason: "disabled" });
  deep("lifecycle: ...and starts nothing when the setting is off",
    plan({ enabled: false, name: "agent-vm", armed: null, online: true, vmState: "running" }),
    { action: "none", reason: "disabled" });
  deep("lifecycle: an empty armed name is 'nothing armed', not an instance",
    plan({ enabled: true, name: "agent-vm", armed: "", online: true, vmState: "running" }),
    { action: "start", reason: "reachable" });
  deep("lifecycle: a missing reading starts nothing", plan({}), { action: "none", reason: "unreachable" });

  // THE OTHER HALF OF THE EDGE: what the guest's answer means for it. planLifecycle arms
  // the instance the moment a reachable reading asks for a start, which is right for both
  // answers the check can give — but a check that never HAPPENED establishes nothing, and
  // leaving it armed made every later reachable reading a "none": no watcher, no reconcile,
  // no ack, until an unrelated event cleared the edge.
  const outcome = (o) => f.planStartOutcome(o);
  deep("outcome: a guest that could not be asked is a RETRY, not an answer",
    outcome({ outcome: f.START_UNANSWERED, current: true }), { action: "retry", reason: "unanswered" });
  deep("outcome: a guest that answered 'no spool' keeps the edge (asked once per connect)",
    outcome({ outcome: f.START_UNSUPPORTED, current: true }), { action: "keep", reason: "unsupported" });
  deep("outcome: a serving session keeps it too",
    outcome({ outcome: f.START_SUPPORTED, current: true }), { action: "keep", reason: "supported" });
  deep("outcome: a session the window withdrew is the stop path's business",
    outcome({ outcome: f.START_STOOD_DOWN, current: true }), { action: "none", reason: "stood-down" });
  deep("outcome: start() on an already-started session changes nothing",
    outcome({ outcome: f.START_RUNNING, current: true }), { action: "keep", reason: "running" });
  // The claim/generation guard: what it would clear belongs to a LATER session by then.
  deep("outcome: a start the window has moved on from touches neither reference",
    outcome({ outcome: f.START_UNANSWERED, current: false }), { action: "none", reason: "superseded" });
  deep("outcome: ...whatever it answered",
    outcome({ outcome: f.START_SUPPORTED, current: false }), { action: "none", reason: "superseded" });
  deep("outcome: an unknown answer is never a retry (it would be an unbounded loop)",
    outcome({ outcome: "something-new", current: true }), { action: "keep", reason: "something-new" });
  deep("outcome: a missing answer is not one either", outcome({ current: true }), { action: "keep", reason: "started" });
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
    const { fwd, transport, timers } = makeForwarder({ hostLabel: "alice-pc" });
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    const b64 = /printf %s '([A-Za-z0-9+/=]+)'/.exec(transport.acks[0])[1];
    eq("local: the host label rides in the ack",
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).hostLabel, "alice-pc");
    eq("local: ...and names the PC in the link", fwd.snapshot().items[0].url, "http://alice-pc:5173/");
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

    fwd.setHostLabel("alice-pc");
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
      JSON.parse(Buffer.from(b64, "base64").toString("utf8")).hostLabel, "alice-pc");

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

// ── Lazy start: the guest is established before anything persistent is spawned ─
async function lazyStart() {
  console.log("\n  -- lazy start (guest-gated) --");

  // The supported path: ONE cheap check first, and only then the watcher and the poll.
  {
    const { fwd, transport, timers } = makeForwarder();
    const started = fwd.start();
    eq("lazy: start() spawns nothing before the guest has answered", transport.watches.length, 0);
    // start() RESOLVES TO the guest's answer: the window that armed this instance cannot
    // tell "an older guest" from "the check never happened" by any other means.
    eq("lazy: ...and start() reports a served guest", await started, f.START_SUPPORTED);
    await timers.advance(5000);
    eq("lazy: the capability check ran exactly once", transport.capabilities.length, 1);
    ok("lazy: ...and it was the FIRST thing to touch the VM", isCapabilityScript(transport.scripts[0]));
    eq("lazy: a supported guest gets the watcher", transport.watches.length, 1);
    ok("lazy: ...and it is the documented watch script",
      transport.watches[0].script.indexOf("inotifywait -m -q") >= 0);
    ok("lazy: ...and the reconcile follows it", transport.scripts.filter(isReconcileScript).length >= 1);
    eq("lazy: the guest is recorded as supported", fwd.supported, true);
    fwd.dispose();
  }

  // The older-guest path — the zero-change bar. One exec, then nothing: no watcher, no
  // reconcile, no timer, and no retry until the window starts it again.
  {
    const { fwd, transport, timers } = makeForwarder({ spool: false });
    const lines = [];
    fwd.log = (l) => lines.push(l);
    eq("lazy: an answered 'no spool' is reported as UNSUPPORTED", await fwd.start(), f.START_UNSUPPORTED);
    await timers.advance(f.RECONCILE_MS * 4);
    eq("lazy: an older guest is checked once", transport.capabilities.length, 1);
    eq("lazy: ...and NO watcher is spawned", transport.watches.length, 0);
    eq("lazy: ...and no reconcile SSH is run at all",
      transport.scripts.filter(isReconcileScript).length, 0);
    eq("lazy: ...and nothing else is either", transport.scripts.length, 1);
    eq("lazy: ...and no tunnel is opened", transport.tunnels.length, 0);
    eq("lazy: the guest is recorded as unsupported", fwd.supported, false);
    ok("lazy: ...and the log says what to do about it",
      lines.some((l) => l.indexOf("no forward spool") >= 0 && l.indexOf("Reprovision") >= 0));
    eq("lazy: ...and nothing is rendered", fwd.snapshot().items.length, 0);
    // A reprovision, and the next start picks it up — standing down is never permanent.
    transport.spool = true;
    transport.dump = dump("self", [["R", "1-a", { v: 1, id: "1-a", vmPort: 5173, target: "client" }]]);
    await settle(fwd, timers);
    eq("lazy: a reprovisioned VM is served on the next start", transport.tunnels.length, 1);
    fwd.dispose();
  }

  // A check that could not be answered (the VM dropped the connection) is NOT an older
  // guest: nothing is spawned, and nothing is recorded either — the next connect asks again.
  {
    const { fwd, transport, timers } = makeForwarder({ spoolCode: 255 });
    const lines = [];
    fwd.log = (l) => lines.push(l);
    // ...and this one is reported as UNANSWERED, which is what makes the window retry it
    // instead of holding a stood-down session armed forever (see the wiring model below).
    eq("lazy: a check that never happened is reported as UNANSWERED", await fwd.start(), f.START_UNANSWERED);
    await timers.advance(f.RECONCILE_MS * 2);
    eq("lazy: an unanswerable check spawns no watcher", transport.watches.length, 0);
    eq("lazy: ...and no reconcile", transport.scripts.filter(isReconcileScript).length, 0);
    eq("lazy: ...and does not brand the guest as old", fwd.supported, null);
    ok("lazy: ...and says so", lines.some((l) => l.indexOf("could not check") >= 0));
    fwd.dispose();
  }

  // dispose() while the check is in flight: the continuation must spawn nothing.
  {
    const { fwd, transport, timers } = makeForwarder();
    let release;
    const gate = new Promise((r) => { release = r; });
    const inner = transport.runRemoteScript;
    transport.runRemoteScript = (script) => (isCapabilityScript(script)
      ? gate.then(() => inner(script))
      : inner(script));
    const started = fwd.start();
    fwd.dispose();
    release();
    eq("lazy: a start disposed mid-check reports that it stood down", await started, f.START_STOOD_DOWN);
    await timers.advance(5000);
    eq("lazy: a start disposed mid-check spawns no watcher", transport.watches.length, 0);
    eq("lazy: ...and runs no reconcile", transport.scripts.filter(isReconcileScript).length, 0);
  }

  // Remote mode has no spool at all: the service is the authority, so there is nothing to
  // check — and the poll still starts only when the window asks it to.
  {
    const { fwd, transport, timers } = makeForwarder({
      instance: { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm" },
    });
    transport.lists = [[]];
    eq("lazy: remote mode has no spool to check, so it starts served", await fwd.start(), f.START_SUPPORTED);
    await timers.advance(1000);
    eq("lazy: remote mode runs no capability check", transport.capabilities.length, 0);
    eq("lazy: ...and opens no watcher", transport.watches.length, 0);
    ok("lazy: ...and polls the service instead", transport.fetches.length >= 1);
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
      hostLabel: "alice-pc",
    });
    transport.lists = [[entry()]];
    await settle(fwd, timers);
    const post = transport.fetches.find((r) => r.method === "POST");
    deep("remote: the ack body carries the host label",
      post.body, { status: "open", localPort: 5173, hostLabel: "alice-pc" });
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

  // The rendered link goes through the same URL-host rule as the ack the guest reads, so
  // the panel and `construct expose` cannot print two different addresses for one forward.
  eq("snapshot: an IPv6 host label is bracketed exactly once",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], acks: [{ id: "a", status: "open", localPort: 18800, hostLabel: "fe80::1" }] })
      .items[0].url, "http://[fe80::1]:18800/");
  eq("snapshot: ...and a bracketed one (an older client's ack) renders identically",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], acks: [{ id: "a", status: "open", localPort: 18800, hostLabel: "[fe80::1]" }] })
      .items[0].url, "http://[fe80::1]:18800/");
  eq("snapshot: an unusable host label falls back to a link that at least opens",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], acks: [{ id: "a", status: "open", localPort: 18800, hostLabel: "1::2::3" }] })
      .items[0].url, "http://localhost:18800/");
  eq("snapshot: a tunnel with no ack yet brackets the configured label the same way",
    f.toSnapshot({ requests: [{ id: "a", vmPort: 1 }], tunnels: [{ id: "a", localPort: 18800, state: "up" }], hostLabel: "[fe80::1]" })
      .items[0].url, "http://[fe80::1]:18800/");

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

  // The ONE long-lived process the default path gained (B8x), pinned in full — this is
  // the argv extension/ARCHITECTURE.md documents next to the zero-change statement, and it
  // is only ever spawned after the capability check said the guest has the spool.
  const watchScript = f.buildWatchScript({});
  const watchArgv = ui.buildStreamArgs(ssh, {}, false, watchScript);
  // The remote command is rebuilt HERE from the script's own base64 rather than by calling
  // ssh.wrapScriptCommand, so a change to EITHER the wrapper or the watch script fails this
  // test instead of sliding past a substring check.
  const watchCommand = "f=$(mktemp) && printf %s '" +
    Buffer.from(watchScript, "utf8").toString("base64") +
    "' | base64 -d > \"$f\" && bash \"$f\"; rc=$?; rm -f \"$f\"; exit $rc";
  deep("watcher argv: the default instance's ENTIRE argv, byte for byte",
    watchArgv,
    ["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
      "-o", "ConnectTimeout=12", "-o", "ServerAliveInterval=20", "-o", "ServerAliveCountMax=3",
      "agent-vm", watchCommand]);
  eq("watcher argv: ...and that is the ssh options plus the alias and the ONE command",
    watchArgv.length, 13);

  const stream = ui.buildStreamArgs(ssh, {}, false, "echo hi");
  ok("stream argv: no tty — it is a data stream", stream.indexOf("-T") >= 0);
  ok("stream argv: keepalives so a dead link is noticed", stream.indexOf("ServerAliveInterval=20") >= 0);
  ok("stream argv: the script rides base64-wrapped", stream[stream.length - 1].indexOf("base64 -d") >= 0);
  ok("stream argv: no -p for the default port", stream.indexOf("-p") < 0);
  ok("stream argv: a forwarded port is emitted",
    ui.buildStreamArgs(ssh, { sshPort: 2201 }, false, "x").indexOf("-p") >= 0);
})();

// ── extension.js's forwarder wiring: one session, zero orphans ─────────────────
// A MODEL of extension.js's forwarder half — the module-level forwarderSession /
// forwarderInstance / forwarderArmed, the slot claim, the serialized chain and the status
// trigger — driven with deferred transports so the interleavings are exact instead of
// intermittent in a live window. The DECISIONS are the production ones (instances.
// planEnable, instances.createHandover, instances.createSessionOwner and
// forwarder.planLifecycle); the model supplies only the effects.
//
// `serialized: false` is the PRE-FIX control: the shape extension.js had, where the
// enabled check happened once before the await, a teardown ran beside the chain that was
// not there, and the continuation published whatever it had built.
async function lifecycleWiring() {
  console.log("\n  -- extension.js wiring: one session, zero orphans --");
  const tick = async (n) => { for (let i = 0; i < (n || 6); i++) await Promise.resolve(); };
  const deferred = () => {
    let resolve;
    const promise = new Promise((res) => { resolve = res; });
    return { promise, resolve };
  };

  function forwarderWindow(startName, opts) {
    const o = opts || {};
    const serialized = o.serialized !== false;
    // `gated: false` is the OTHER pre-fix control: a session built without the window's
    // `eligible` callback, so nothing re-checks the world across the guest capability
    // check — the second half of the escape this round fixes.
    const gated = o.gated !== false;
    // Hold the guest's answer so a test can park a session inside its own await, which is
    // the window between publishing it and spawning anything.
    const holdCapability = o.holdCapability === true;
    // `keepArmed: true` is the PRE-FIX control for the retry edge: start()'s answer is
    // thrown away, so an unanswered check leaves the instance armed forever.
    const retryUnanswered = o.keepArmed !== true;
    // What the GUEST answers, per start: `{}` = SPOOL=1, `{ spool: false }` = an older
    // guest, `{ spoolCode: 255 }` = a check that could not be made at all. A function is
    // asked once per session, so one window can fail a check and then succeed.
    const guest = typeof o.guest === "function" ? o.guest : () => (o.guest || {});
    const gate = inst.createGate(startName);
    const slot = inst.createSessionOwner();
    const sessions = [];                       // every forwarder ever constructed
    const held = { session: null, name: null };  // forwarderSession / forwarderInstance
    let armed = null;                          // forwarderArmed
    let enabled = true;                        // construct.forwards.enabled
    let closed = false;                        // has deactivate() run?
    const builds = new Map();                  // buildForwarderTransport, per instance
    const holds = new Map();                   // the guest capability check, per instance
    const asked = [];                          // every call of it, in order
    const log = [];
    const buildFor = (name, count) => {
      if (!builds.has(name)) builds.set(name, deferred());
      if (count) asked.push(name);
      return builds.get(name);
    };
    const holdFor = (name) => {
      if (!holds.has(name)) {
        const d = deferred();
        if (!holdCapability) d.resolve();
        holds.set(name, d);
      }
      return holds.get(name);
    };
    const target = (name) => ({ ...inst.captureTarget(gate, { name }), name });

    /** stopForwarder(): drop the reference, dispose, and take the slot away so a start
     *  still awaiting its transport can publish nothing. */
    const stop = () => {
      if (serialized) slot.claim("");
      const s = held.session;
      held.session = null; held.name = null;
      if (s) { s.open = false; try { s.forwarder.dispose(); } catch (_) {} log.push("disposed:" + s.name); }
    };

    /** startForwarder(): its ONE await is the transport build (SecretStorage, remotely). */
    async function start(t) {
      const plan = serialized
        ? inst.planEnable({
          live: !!held.session, name: held.name,
          enabled: !!held.session, pending: false, closed: chain.closed,
        }, t.name)
        // PRE-FIX: only an already-live session for THIS instance stopped a second one.
        : { action: held.session && held.name === t.name ? "report" : "create", reason: "pre-fix" };
      if (plan.action !== "create") { log.push("plan:" + plan.action + ":" + t.name); return; }
      if (!enabled) return;
      if (inst.targetSuperseded(gate, t)) { log.push("superseded:" + t.name); return; }
      // PRE-FIX: the live session was torn down HERE, beside anything else in flight.
      if (!serialized) stop();
      const claim = serialized ? slot.claim(t.name) : null;
      const transport = await buildFor(t.name, true).promise;
      if (serialized) {
        if (!slot.owns(claim)) { log.push("discarded-stopped:" + t.name); return; }
        if (chain.closed || !enabled) { log.push("discarded-off:" + t.name); return; }
        if (inst.targetSuperseded(gate, t)) { log.push("discarded-switched:" + t.name); return; }
      } else if (gate.name !== t.name) {
        // The pre-fix shape's ONLY post-await check: is this still the active instance?
        return;
      }
      if (!transport) return;
      // A REAL Forwarder, so the second await this round is about — its own guest
      // capability check — is exercised rather than modelled.
      const timers = makeTimers();
      const fake = makeTransport(guest(t.name));
      const hold = holdFor(t.name);
      const inner = fake.runRemoteScript;
      fake.runRemoteScript = (script) => (isCapabilityScript(script)
        ? hold.promise.then(() => inner(script)) : inner(script));
      const stillWanted = () => slot.owns(claim) && !chain.closed && enabled
        && !inst.targetSuperseded(gate, t);
      const fwd = f.createForwarder({
        instance: { name: t.name, backend: "hyperv-local" },
        transport: fake,
        timers: timers.api,
        now: timers.now,
        log: () => {},
        eligible: gated ? stillWanted : undefined,
        onChange: () => {},
      });
      const session = { name: t.name, open: true, afterClose: closed, forwarder: fwd, transport: fake };
      sessions.push(session);
      held.session = session; held.name = t.name;
      log.push("serving:" + t.name);
      // AWAITED, exactly as extension.js awaits forwarderSession.start(): the chain step
      // stays open until the guest has answered, and that answer decides the armed edge.
      const answer = await fwd.start();
      // noteForwarderStarted(): a check that never happened establishes nothing, so the
      // session AND the armed edge go — but only while this start is still the current one.
      const outcomePlan = f.planStartOutcome({
        outcome: answer,
        current: slot.owns(claim) && held.session === session && !inst.targetSuperseded(gate, t),
      });
      log.push("outcome:" + outcomePlan.action + ":" + t.name);
      if (serialized && retryUnanswered && outcomePlan.action === "retry") { armed = null; stop(); }
      return answer;
    }

    const chain = inst.createHandover({
      session: () => ({ live: !!held.session, name: held.name }),
      teardown: stop,
      arm: (t) => start(t),
      superseded: (t) => inst.targetSuperseded(gate, t),
    });

    /** requestForwarderStop(): invalidate the slot SYNCHRONOUSLY (a start parked in either
     *  of its awaits must not go on to spawn anything), then queue the disposal. */
    const requestStop = () => {
      if (!serialized) { stop(); return Promise.resolve(); }
      slot.claim("");
      armed = null;
      return chain.disable();
    };

    return {
      log, sessions,
      /** buildForwarderTransport resolving (null = a remote instance with no credential). */
      resolveBuild: (name, value) => buildFor(name).resolve(value === null ? null : { name }),
      /** refreshAll/refreshState's reading — the forwarder's only trigger. */
      note(name, reading) {
        const t = target(name);
        const plan = f.planLifecycle({
          enabled, name, armed, online: reading.online, vmState: reading.vmState,
        });
        log.push("lifecycle:" + plan.action + ":" + name);
        if (plan.action === "start") {
          armed = name;
          return serialized ? chain.enable(t, (x) => start(x)) : start(t);
        }
        if (plan.action === "stop") { armed = null; return requestStop(); }
        return Promise.resolve();
      },
      /** The construct.forwards.enabled setting changing. */
      setEnabled(value) {
        enabled = value;
        if (value) return Promise.resolve();
        return requestStop();
      },
      /** onInstanceChanged()'s forwarder half. */
      switchTo(name) {
        gate.set(name);
        if (serialized) {
          if (held.name !== name || armed !== name) return requestStop();
          return Promise.resolve();
        }
        // PRE-FIX: tear down and start the destination right here, beside what is in flight.
        if (held.name !== name) { stop(); return start(target(name)); }
        return Promise.resolve();
      },
      /** deactivate(): close the chain FIRST, then dispose the one live session. */
      close() {
        closed = true;
        if (serialized) chain.close();
        armed = null;
        stop();
      },
      askedFor: (name) => asked.filter((n) => n === name).length,
      /** Guest capability checks this window ever made — one exec per session, at most. */
      checks: () => sessions.reduce((n, s) => n + s.transport.capabilities.length, 0),
      /** The guest answering the capability check for a session that was parked in it. */
      answerCapability: (name) => { holdFor(name).resolve(); return tick(); },
      /** Watchers this window ever SPAWNED, and reconciles it ever ran — the traffic. */
      watchers: () => sessions.reduce((n, s) => n + s.transport.watches.length, 0),
      reconciles: () => sessions.reduce(
        (n, s) => n + s.transport.scripts.filter(isReconcileScript).length, 0),
      /** Watcher children still alive — a leak that outlived its session. */
      liveWatchers: () => sessions.reduce(
        (n, s) => n + s.transport.watches.filter((w) => !w.child.killed).length, 0),
      openSessions: () => sessions.filter((s) => s.open).map((s) => s.name),
      /** Sessions still OPEN while the window no longer references them — the leak. */
      orphans: () => sessions.filter((s) => s.open && s !== held.session).map((s) => s.name),
      builtFor: (name) => sessions.filter((s) => s.name === name).length,
      armedName: () => armed,
    };
  }

  const up = { online: true, vmState: "running" };

  // 0) THE DEFAULT PATH: an untouched window that has established nothing builds no
  //    transport at all — the activation-level property this whole change is about.
  {
    const w = forwarderWindow("agent-vm");
    await tick();
    deep("default: a window with no reading serves nothing", w.openSessions(), []);
    eq("default: ...and never even asked for a transport", w.askedFor("agent-vm"), 0);
    await w.note("agent-vm", { online: false, vmState: "off" });
    deep("default: a VM that is off starts nothing", w.openSessions(), []);
    await w.note("agent-vm", { online: false, vmState: "unknown" });
    deep("default: an unreachable VM starts nothing either", w.openSessions(), []);
    eq("default: ...and nothing is armed", w.armedName(), null);
  }

  // 1) DISABLE DURING A START. The setting goes off while the transport is being built.
  {
    const w = forwarderWindow("agent-vm");
    const started = w.note("agent-vm", up);
    await tick();
    const off = w.setEnabled(false);
    w.resolveBuild("agent-vm");
    await Promise.all([started, off]);
    await tick();
    deep("disable-during-start: nothing is serving", w.openSessions(), []);
    deep("disable-during-start: ...and nothing is orphaned", w.orphans(), []);
    ok("disable-during-start: the in-flight start says why it was dropped",
      w.log.some((l) => l.indexOf("discarded-") === 0));
  }
  {
    const w = forwarderWindow("agent-vm", { serialized: false });
    const started = w.note("agent-vm", up);
    await tick();
    w.setEnabled(false);
    w.resolveBuild("agent-vm");
    await started;
    await tick();
    deep("control: the pre-fix shape opens a forwarder AFTER the user disabled forwarding",
      w.openSessions(), ["agent-vm"]);
  }

  // 2) DEACTIVATE DURING A START. The window shuts down mid-build.
  {
    const w = forwarderWindow("agent-vm");
    const started = w.note("agent-vm", up);
    await tick();
    w.close();
    w.resolveBuild("agent-vm");
    await started;
    await tick();
    deep("deactivate-during-start: nothing survives the window", w.openSessions(), []);
    deep("deactivate-during-start: ...and nothing is orphaned", w.orphans(), []);
  }
  {
    const w = forwarderWindow("agent-vm", { serialized: false });
    const started = w.note("agent-vm", up);
    await tick();
    w.close();
    w.resolveBuild("agent-vm");
    await started;
    await tick();
    deep("control: the pre-fix shape leaves a forwarder behind after deactivation",
      w.openSessions(), ["agent-vm"]);
    ok("control: ...built after the window was already gone",
      w.sessions.some((s) => s.open && s.afterClose));
  }

  // 3) OVERLAPPING STARTS. The VM blinks (one tick reports it off) while the first start
  //    is still building its transport, and the next reading asks for it again.
  {
    const w = forwarderWindow("agent-vm");
    const first = w.note("agent-vm", up);
    await tick();
    const down = w.note("agent-vm", { online: false, vmState: "saved" });
    const again = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await Promise.all([first, down, again]);
    await tick();
    deep("overlapping: exactly one forwarder is serving", w.openSessions(), ["agent-vm"]);
    deep("overlapping: ...and nothing is orphaned", w.orphans(), []);
  }
  {
    const w = forwarderWindow("agent-vm", { serialized: false });
    const first = w.note("agent-vm", up);
    await tick();
    const down = w.note("agent-vm", { online: false, vmState: "saved" });
    const again = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await Promise.all([first, down, again]);
    await tick();
    eq("control: the pre-fix shape builds two forwarders for one VM", w.builtFor("agent-vm"), 2);
    deep("control: ...and orphans the one it stopped referencing", w.orphans(), ["agent-vm"]);
  }

  // 4) A→B→A. Every step is queued, so the destination's forwarder is built from the
  //    destination's own capture — and the instance we left keeps nothing.
  {
    const w = forwarderWindow("agent-vm");
    const first = w.note("agent-vm", up);
    const toB = w.switchTo("work-vm");
    const noteB = w.note("work-vm", up);
    const toA = w.switchTo("agent-vm");
    const noteA = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    w.resolveBuild("work-vm");
    await Promise.all([first, toB, noteB, toA, noteA]);
    await tick();
    deep("A->B->A: exactly one forwarder is serving, and it is A's", w.openSessions(), ["agent-vm"]);
    deep("A->B->A: ...and nothing is orphaned", w.orphans(), []);
    eq("A->B->A: B's forwarder was never built — its target was superseded first",
      w.builtFor("work-vm"), 0);
    eq("A->B->A: ...so its transport was never even asked for", w.askedFor("work-vm"), 0);
  }
  {
    const w = forwarderWindow("agent-vm", { serialized: false });
    const first = w.note("agent-vm", up);
    await tick();
    const toB = w.switchTo("work-vm");
    const toA = w.switchTo("agent-vm");
    w.resolveBuild("agent-vm");
    w.resolveBuild("work-vm");
    await Promise.all([first, toB, toA]);
    await tick();
    eq("control: the pre-fix A->B->A builds two forwarders for A", w.builtFor("agent-vm"), 2);
    deep("control: ...and leaves the first one open with nothing referencing it",
      w.orphans(), ["agent-vm"]);
  }

  // 5) THE RECONNECT EDGE. A reading that did not reach the VM lets the live watcher go —
  //    whatever the host still says about its power state — and the next reading that DOES
  //    reach it starts exactly one new session, which re-opens whatever is queued in the
  //    spool. This is the flow behind planLifecycle's stop rule.
  {
    const w = forwarderWindow("agent-vm");
    const first = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await first;
    await tick();
    eq("reconnect: a reachable reading starts the watcher", w.watchers(), 1);
    eq("reconnect: ...and it is alive", w.liveWatchers(), 1);
    // Unreachable, while the HOST still reports the VM as running — the case that used to
    // leave the watcher and the tunnels holding sockets to a VM this window cannot reach.
    await w.note("agent-vm", { online: false, vmState: "running" });
    await tick();
    eq("reconnect: an unreachable reading kills the watcher", w.liveWatchers(), 0);
    deep("reconnect: ...and leaves nothing serving", w.openSessions(), []);
    deep("reconnect: ...and nothing orphaned", w.orphans(), []);
    eq("reconnect: ...and clears the armed edge, so the next connect is a retry", w.armedName(), null);
    await w.note("agent-vm", up);
    await tick();
    eq("reconnect: the next reachable reading starts EXACTLY one new session", w.builtFor("agent-vm"), 2);
    deep("reconnect: ...and it is the only one serving", w.openSessions(), ["agent-vm"]);
    eq("reconnect: ...with one live watcher", w.liveWatchers(), 1);
    eq("reconnect: ...and no orphaned watcher from the first", w.watchers(), 2);
    w.close();
  }

  // 6) A STOP DURING THE GUEST CAPABILITY CHECK. The session is published and then parked
  //    in its own await; the teardown for a disable / unreachable reading / switch /
  //    deactivation is queued BEHIND that step, so the session itself has to re-check.
  //    Nothing may be spawned: no watcher, no reconcile — no SSH after the window said no.
  for (const [label, act] of [
    ["disable", (w) => w.setEnabled(false)],
    ["an unreachable reading", (w) => w.note("agent-vm", { online: false, vmState: "running" })],
    ["a switch", (w) => w.switchTo("work-vm")],
    ["deactivation", (w) => { w.close(); return Promise.resolve(); }],
  ]) {
    const w = forwarderWindow("agent-vm", { holdCapability: true });
    const started = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await tick();
    eq(`capability(${label}): the session is parked in the guest check`, w.watchers(), 0);
    const acted = act(w);
    await w.answerCapability("agent-vm");
    await Promise.all([started, acted]);
    await tick();
    eq(`capability(${label}): no watcher is ever spawned`, w.watchers(), 0);
    eq(`capability(${label}): ...and no reconcile is ever run`, w.reconciles(), 0);
    deep(`capability(${label}): ...and nothing is left serving`, w.openSessions(), []);
    deep(`capability(${label}): ...and nothing is orphaned`, w.orphans(), []);
  }
  {
    // The control: the same disable, against a session built WITHOUT the window's
    // eligibility callback. The guest answers, the watcher goes up and the spool is
    // reconciled — forwarding traffic after the user switched forwarding off — and only
    // then does the queued teardown reach it.
    const w = forwarderWindow("agent-vm", { holdCapability: true, gated: false });
    const started = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await tick();
    const off = w.setEnabled(false);
    await w.answerCapability("agent-vm");
    await Promise.all([started, off]);
    await tick();
    eq("control: without the re-check the watcher is spawned AFTER the disable", w.watchers(), 1);
    ok("control: ...and the spool is reconciled too", w.reconciles() >= 1);
  }

  // 8) A CAPABILITY CHECK THAT COULD NOT BE MADE. The reading reached the VM, the session
  //    was built, and its one exec failed (the connection dropped, sshd is not up yet, the
  //    VM is on its way down). Nothing was established — so the window must let that
  //    session go and RE-ARM on the next reachable reading. Left armed, as it was, every
  //    later reading planned "none" and no watcher, reconcile or ack ever followed: a
  //    queued `construct expose` waited out its whole timeout for no reason.
  {
    let fail = true;
    const w = forwarderWindow("agent-vm", { guest: () => (fail ? { spoolCode: 255 } : {}) });
    const first = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await first;
    await tick();
    eq("unanswered: the check ran", w.checks(), 1);
    eq("unanswered: ...and spawned nothing", w.watchers(), 0);
    deep("unanswered: ...and nothing is left serving", w.openSessions(), []);
    deep("unanswered: ...and nothing is orphaned", w.orphans(), []);
    eq("unanswered: THE EDGE IS CLEARED, so the next reading is a retry", w.armedName(), null);
    // The next 30 s reading, with the VM answering this time.
    fail = false;
    const second = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await second;
    await tick();
    eq("unanswered: the next reachable reading asks again", w.checks(), 2);
    deep("unanswered: ...and this one serves", w.openSessions(), ["agent-vm"]);
    eq("unanswered: ...with its watcher up", w.liveWatchers(), 1);
    deep("unanswered: ...and still nothing orphaned", w.orphans(), []);
    w.close();
  }
  {
    // THE CONTROL, and the reason the two answers are not one: a guest that ANSWERED
    // "no spool" is not retried. It costs one exec per connection and nothing else —
    // the zero-change bar for a VM provisioned before `construct expose` existed.
    const w = forwarderWindow("agent-vm", { guest: { spool: false } });
    const first = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await first;
    await tick();
    eq("unsupported: the guest was asked once", w.checks(), 1);
    eq("unsupported: ...and the edge is KEPT", w.armedName(), "agent-vm");
    await w.note("agent-vm", up);
    await w.note("agent-vm", up);
    await tick();
    eq("unsupported: ...so two more reachable readings ask nothing", w.checks(), 1);
    eq("unsupported: ...and spawn nothing", w.watchers(), 0);
    eq("unsupported: ...and build no second session", w.builtFor("agent-vm"), 1);
    w.close();
  }
  {
    // PRE-FIX CONTROL: the shape where start()'s answer was discarded. The instance stays
    // armed on a session that stood itself down, so the next reachable reading — and every
    // one after it — plans "none": the retry never happens.
    const w = forwarderWindow("agent-vm", { guest: { spoolCode: 255 }, keepArmed: true });
    const first = w.note("agent-vm", up);
    w.resolveBuild("agent-vm");
    await first;
    await tick();
    eq("control: the pre-fix shape stays armed on a check that never answered",
      w.armedName(), "agent-vm");
    await w.note("agent-vm", up);
    await tick();
    ok("control: ...so the next reachable reading does nothing at all",
      w.log.filter((l) => l === "lifecycle:none:agent-vm").length === 1);
    eq("control: ...and the guest is never asked again", w.checks(), 1);
    eq("control: ...leaving a queued `construct expose` with no watcher to answer it",
      w.watchers(), 0);
    w.close();
  }

  // 7) A REMOTE INSTANCE WITH NO CREDENTIAL resolves to no transport: nothing is built,
  //    nothing is armed twice, and the window is not left in a half-started state.
  {
    const w = forwarderWindow("work-vm");
    const started = w.note("work-vm", up);
    w.resolveBuild("work-vm", null);
    await started;
    await tick();
    deep("no-credential: nothing is serving", w.openSessions(), []);
    deep("no-credential: ...and nothing is orphaned", w.orphans(), []);
    await w.note("work-vm", up);
    eq("no-credential: ...and it is not retried on every refresh", w.askedFor("work-vm"), 1);
  }
}

// ── extension.js's wiring, pinned at the source ────────────────────────────────
// extension.js cannot be required under plain node (it needs `vscode`), so the wiring the
// model above stands in for is pinned here — the same way instances.test.js pins the mic
// chain's. What these guard: activation must spawn nothing, every start and stop must go
// through the one chain, and the trigger must be the status flow's own reading.
(() => {
  console.log("\n  -- extension.js wiring (source-pinned) --");
  const extSrc = fs.readFileSync(path.join(__dirname, "..", "extension.js"), "utf8");
  ok("activation: nothing forwarding-related is started at activation",
    extSrc.indexOf("setTimeout(() => { noteForwarderConnected(); }, 3000);") >= 0 &&
    extSrc.indexOf("void startForwarder(); }, 3000)") < 0);
  ok("activation: the only free reachability fact is the window's own Remote-SSH attachment",
    extSrc.indexOf("if (!remote.isConnectedToVm(safeRemoteAuthority(), activeCfg())) return;") >= 0 &&
    extSrc.indexOf('noteForwarderPresence(actionTarget(), { online: true, vmState: "running" });') >= 0);
  ok("trigger: both refresh pipelines hand their OWN captured target and reading to it",
    extSrc.indexOf("noteForwarderPresence(target, state);") >= 0 &&
    extSrc.indexOf("noteForwarderPresence(refreshTarget, state);") >= 0);
  ok("trigger: the decision is the pure forwarder.planLifecycle",
    extSrc.indexOf("const plan = forwarder.planLifecycle({") >= 0 &&
    extSrc.indexOf("armed: forwarderArmed,") >= 0);
  ok("chain: every start and stop is queued on ONE serialized chain",
    extSrc.indexOf("const forwarderChain = instances.createHandover({") >= 0 &&
    extSrc.indexOf("void forwarderChain.enable(target, (t) => startForwarder(t));") >= 0 &&
    // ...and nothing starts the forwarder beside it.
    !/void startForwarder\(\)/.test(extSrc));
  ok("chain: a switch tears down on the chain and starts nothing",
    // ...and a registry rewrite under the SAME name counts as a switch: the transport is
    // an `ssh -L` to an ENDPOINT, which a same-name rewrite moves (identityChanged).
    extSrc.indexOf("if (forwarderInstance !== inst.name || forwarderArmed !== inst.name || identityChanged) {\n    void requestForwarderStop();\n  }") >= 0);
  ok("stop: every stop request invalidates the slot SYNCHRONOUSLY and queues the disposal",
    extSrc.indexOf("function requestForwarderStop() {\n  forwarderSlot.claim(\"\");\n  forwarderArmed = null;\n  return forwarderChain.disable();\n}") >= 0 &&
    // ...and nothing bypasses it.
    extSrc.split("forwarderChain.disable()").length === 2);
  ok("outcome: the guest's answer comes back through the pure sibling and decides the edge",
    extSrc.indexOf("const outcome = await session.start();") >= 0 &&
    extSrc.indexOf("noteForwarderStarted(t, claim, session, outcome);") >= 0 &&
    extSrc.indexOf("const plan = forwarder.planStartOutcome({") >= 0 &&
    extSrc.indexOf("current: forwarderSlot.owns(claim) && forwarderSession === session") >= 0);
  ok("outcome: ...and a retry clears the armed edge AND the session, in that one step",
    extSrc.indexOf('if (plan.action !== "retry") return;') >= 0 &&
    extSrc.indexOf("  forwarderArmed = null;\n  stopForwarder();\n}") >= 0 &&
    // ...without a second disable on the chain (it already runs ON the chain).
    extSrc.split("forwarderChain.disable()").length === 2);
  ok("eligibility: the session re-checks the SAME four conditions around its own await",
    extSrc.indexOf("const stillWanted = () => forwarderSlot.owns(claim)") >= 0 &&
    extSrc.indexOf("&& !forwarderChain.closed") >= 0 &&
    extSrc.indexOf("&& forwardsEnabled()") >= 0 &&
    extSrc.indexOf("&& !instances.targetSuperseded(instanceGate, t);") >= 0 &&
    extSrc.indexOf("eligible: stillWanted,") >= 0);
  ok("slot: the claim is taken BEFORE the one await and re-checked after it",
    extSrc.indexOf("const claim = forwarderSlot.claim(t.name);") >= 0 &&
    extSrc.indexOf("const transport = await buildForwarderTransport(inst);") >= 0 &&
    extSrc.indexOf("if (!forwarderSlot.owns(claim)) {") >= 0 &&
    extSrc.indexOf("const claim = forwarderSlot.claim(t.name);") <
      extSrc.indexOf("const transport = await buildForwarderTransport(inst);") &&
    extSrc.indexOf("const transport = await buildForwarderTransport(inst);") <
      extSrc.indexOf("if (!forwarderSlot.owns(claim)) {"));
  ok("slot: ...along with the setting, the shutdown flag and the generation",
    extSrc.indexOf("if (forwarderChain.closed || !forwardsEnabled()) {") >= 0 &&
    extSrc.indexOf("if (instances.targetSuperseded(instanceGate, t)) {\n    logLine(`forwards: the start for") >= 0);
  ok("slot: stopForwarder() invalidates the in-flight starts before it disposes",
    extSrc.indexOf('function stopForwarder() {\n  // Invalidate first') >= 0 &&
    extSrc.indexOf('forwarderSlot.claim("");') >= 0);
  ok("slot: a late snapshot from a session we let go of is dropped",
    extSrc.indexOf("if (!forwarderSlot.owns(claim)) return;") >= 0 &&
    !extSrc.includes("if (forwarderInstance !== activeInstance().name) return;"));
  ok("decision: the single-session rule is instances.planEnable, from one state helper",
    extSrc.indexOf("const plan = instances.planEnable(forwarderSlotState(), t.name);") >= 0 &&
    extSrc.indexOf("function forwarderSlotState() {") >= 0);
  ok("deactivate: the chain is closed BEFORE the one disposal outside it",
    extSrc.indexOf("try { void forwarderChain.close(); } catch (_) {}") <
      extSrc.lastIndexOf("stopForwarder();") &&
    extSrc.indexOf("try { void forwarderChain.close(); } catch (_) {}") >= 0);
})();

// ── B14: the fallback range is sliced per instance ─────────────────────────────
(function portSlices() {
  const dflt = f.instancePortSlice("agent-vm");
  ok("slice: the default instance keeps the historical 18800–18815",
    dflt.base === f.PORT_BASE && dflt.count === f.PORT_COUNT && dflt.base === 18800);
  ok("slice: an unnamed instance is the default one",
    f.instancePortSlice("").base === 18800 && f.instancePortSlice(null).base === 18800);
  const work = f.instancePortSlice("work-vm");
  ok("slice: another instance never gets the default's slice", work.base !== 18800);
  ok("slice: it is deterministic", f.instancePortSlice("work-vm").base === work.base);
  ok("slice: every slice is 16 ports and aligned to the base",
    work.count === 16 && (work.base - 18800) % 16 === 0);
  ok("slice: every slice stays inside 18800–19311",
    ["a", "work-vm", "build-vm", "zzz", "x9", "far-vm", "q"].every((n) => {
      const s = f.instancePortSlice(n);
      return s.base >= 18800 && s.base + s.count <= 19312;
    }));
  ok("slice: no name ever lands back on the default's slice",
    ["work-vm", "build-vm", "far-vm", "b", "c", "d", "e", "f"].every((n) => f.instancePortSlice(n).base !== 18800));
  // The session picks it up, and an explicit base/count still wins (tests, a setting).
  const auto = makeForwarder({ instance: { name: "work-vm" } }).fwd;
  ok("slice: a session serving an instance probes ITS slice",
    f.portCandidates(9999, auto._portOpts)[1] === work.base);
  const dfltSession = makeForwarder({ instance: { name: "agent-vm" } }).fwd;
  ok("slice: the default instance's session probes exactly the historical ports",
    f.portCandidates(9999, dfltSession._portOpts)[1] === 18800);
  const explicit = makeForwarder({ instance: { name: "work-vm" }, portBase: 20000, portCount: 4 }).fwd;
  ok("slice: an explicit base/count still wins",
    explicit._portOpts.base === 20000 && explicit._portOpts.count === 4);
})();

// ── Summary ────────────────────────────────────────────────────────────────────
(async () => {
  await claimProtocol();
  await lazyStart();
  await localFlow();
  await supervision();
  await remoteFlow();
  await lifecycleWiring();
  console.log(`\n  forwarder unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
