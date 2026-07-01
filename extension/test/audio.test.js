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

// ── Native host recorder argv (the REAL capture path) ───────────────────────────
// A webview cannot open the mic (VS Code's webview iframe Permissions-Policy omits
// `microphone`, so getUserMedia is rejected → silence). Capture is a native host
// recorder instead; these prove the argv matches the recorder contract byte-format.
(() => {
  // Per-OS default input device selector.
  ok("ffmpegInput win32 -> dshow default", JSON.stringify(a.ffmpegInputArgs("win32")) === JSON.stringify(["-f", "dshow", "-i", "audio=default"]));
  ok("ffmpegInput darwin -> avfoundation default", JSON.stringify(a.ffmpegInputArgs("darwin")) === JSON.stringify(["-f", "avfoundation", "-i", ":default"]));
  ok("ffmpegInput linux -> pulse default", JSON.stringify(a.ffmpegInputArgs("linux")) === JSON.stringify(["-f", "pulse", "-i", "default"]));

  // ffmpeg recorder: pins the exact recorder contract (raw S16LE / 16k / mono, stdout).
  const ff = a.buildHostRecorder("ffmpeg", "win32");
  eq("buildHostRecorder ffmpeg: file", ff.file, "ffmpeg");
  ok("buildHostRecorder ffmpeg: mono (-ac 1)", ff.args.join(" ").indexOf("-ac 1") !== -1);
  ok("buildHostRecorder ffmpeg: 16 kHz (-ar 16000)", ff.args.join(" ").indexOf("-ar 16000") !== -1);
  ok("buildHostRecorder ffmpeg: raw s16le container", ff.args.indexOf("s16le") !== -1);
  ok("buildHostRecorder ffmpeg: pcm_s16le codec", ff.args.indexOf("pcm_s16le") !== -1);
  ok("buildHostRecorder ffmpeg: stdout sink (trailing '-')", ff.args[ff.args.length - 1] === "-");
  ok("buildHostRecorder ffmpeg: quiet (no banner/chatter on stdout)", ff.args.indexOf("quiet") !== -1 && ff.args.indexOf("-hide_banner") !== -1);
  ok("buildHostRecorder ffmpeg: uses the OS input selector", ff.args.indexOf("dshow") !== -1);

  // sox fallback reuses the pinned REC_ARGV (already raw/16k/S16/mono to stdout).
  const sx = a.buildHostRecorder("sox", "win32");
  eq("buildHostRecorder sox: file is rec", sx.file, "rec");
  ok("buildHostRecorder sox: argv == REC_ARGV", JSON.stringify(sx.args) === JSON.stringify(a.REC_ARGV));

  // Default tool is ffmpeg.
  eq("buildHostRecorder default tool: ffmpeg", a.buildHostRecorder(undefined, "linux").file, "ffmpeg");
})();

// ── makeHostMicProvider — on-demand spawn/pipe/kill + fallback ───────────────────
(() => {
  // A fake spawned recorder child: EventEmitter with a stdout EventEmitter + kill().
  function fakeRec() {
    const c = new EventEmitter();
    c.stdout = new EventEmitter();
    c.killed = false;
    c.kill = (sig) => { c.killed = true; c.killSig = sig; c.emit("exit", 0, sig); };
    return c;
  }

  // Happy path: spawns ffmpeg, pipes stdout → writeChunk, kill on stop.
  {
    const spawned = [];
    const spawn = (file, args) => { const c = fakeRec(); spawned.push({ file, args, c }); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32", device: "Mic" });
    const chunks = [];
    let doneCalled = false;
    const stop = provider((b) => chunks.push(b), () => { doneCalled = true; });
    eq("provider: spawned exactly one recorder", spawned.length, 1);
    eq("provider: spawned ffmpeg first", spawned[0].file, "ffmpeg");
    // PCM emitted on stdout reaches writeChunk verbatim.
    spawned[0].c.stdout.emit("data", Buffer.from([1, 2, 3, 4]));
    ok("provider: stdout PCM piped to writeChunk", chunks.length === 1 && chunks[0].length === 4);
    ok("provider: done not called while streaming", doneCalled === false);
    // Stop (disconnect) kills the recorder — mic released, on-demand.
    stop();
    ok("provider: stop() SIGTERMs the recorder", spawned[0].c.killed === true && spawned[0].c.killSig === "SIGTERM");
  }

  // Fallback: ffmpeg not installed (ENOENT on 'error') → try sox `rec`.
  {
    const spawned = [];
    const spawn = (file, args) => { const c = fakeRec(); spawned.push({ file, args, c }); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "linux" });
    let doneCalled = false;
    provider(() => {}, () => { doneCalled = true; });
    // ffmpeg child errors (not installed).
    spawned[0].c.emit("error", new Error("spawn ffmpeg ENOENT"));
    eq("provider: falls back to a second recorder on ENOENT", spawned.length, 2);
    eq("provider: fallback is sox rec", spawned[1].file, "rec");
    ok("provider: done NOT called while a fallback remains", doneCalled === false);
  }

  // No recorder at all: both tools ENOENT → done() + onError('no-recorder'), so the
  // socket closes and the shim reports "no audio" (never silence forever).
  {
    const spawned = [];
    const spawn = (file) => { const c = fakeRec(); spawned.push({ file, c }); return c; };
    let doneCalled = false, errReason = null;
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "linux", onError: (r) => { errReason = r; } });
    provider(() => {}, () => { doneCalled = true; });
    spawned[0].c.emit("error", new Error("ENOENT")); // ffmpeg missing
    spawned[1].c.emit("error", new Error("ENOENT")); // sox missing
    ok("provider: exhausted recorders -> done() ends the capture", doneCalled === true);
    eq("provider: exhausted recorders -> onError('no-recorder')", errReason, "no-recorder");
  }

  // A recorder that exits on its own (device busy) ends the capture via done().
  {
    const spawned = [];
    const spawn = (file) => { const c = fakeRec(); spawned.push(c); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32", device: "Mic" });
    let doneCalled = false;
    provider(() => {}, () => { doneCalled = true; });
    spawned[0].emit("exit", 1, null); // recorder died unexpectedly
    ok("provider: recorder self-exit -> done() (honest: capture ended)", doneCalled === true);
  }

  // After stop(), a late stdout chunk is NOT forwarded (no writes to a dead socket).
  {
    const spawned = [];
    const spawn = (file) => { const c = fakeRec(); spawned.push(c); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32", device: "Mic" });
    const chunks = [];
    const stop = provider((b) => chunks.push(b), () => {});
    stop();
    spawned[0].stdout.emit("data", Buffer.from([9, 9])); // arrives after teardown
    eq("provider: no PCM forwarded after stop()", chunks.length, 0);
  }

  // A custom single-tool preference list is honored (only sox tried).
  {
    const spawned = [];
    const spawn = (file) => { const c = fakeRec(); spawned.push(file); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "linux", tools: ["sox"] });
    provider(() => {}, () => {});
    eq("provider: honors a custom tools list (sox only)", spawned[0], "rec");
    eq("provider: custom tools list length respected", spawned.length, 1);
  }
})();

// ── Windows dshow device enumeration (the "audio=default is invalid" fix) ────────
(() => {
  // Modern ffmpeg tags each device line with (audio)/(video).
  const modern = [
    '[dshow @ 0] "Integrated Camera" (video)',
    '[dshow @ 0]   Alternative name "@device_pnp_cam"',
    '[dshow @ 0] "Microphone (Realtek Audio)" (audio)',
    '[dshow @ 0]   Alternative name "@device_cm_mic"',
    '[dshow @ 0] "Line In (Realtek)" (audio)',
  ].join("\n");
  const md = a.parseDshowAudioDevices(modern);
  eq("dshow parse (modern): audio device count", md.length, 2);
  eq("dshow parse (modern): first is the mic", md[0], "Microphone (Realtek Audio)");
  ok("dshow parse (modern): ignores the video device", md.indexOf("Integrated Camera") === -1);
  ok("dshow parse (modern): skips Alternative name lines", md.every((n) => n.indexOf("@device") === -1));

  // Older ffmpeg groups devices under section headers (no (audio) tag).
  const older = [
    "[dshow @ 0] DirectShow video devices",
    '[dshow @ 0]  "Integrated Camera"',
    "[dshow @ 0] DirectShow audio devices",
    '[dshow @ 0]  "Microphone (HD Webcam)"',
    '[dshow @ 0]   Alternative name "@device_cm_x"',
  ].join("\n");
  const od = a.parseDshowAudioDevices(older);
  eq("dshow parse (older): only audio-section devices", od.length, 1);
  eq("dshow parse (older): the audio device", od[0], "Microphone (HD Webcam)");
  eq("dshow parse: empty input -> []", a.parseDshowAudioDevices("").length, 0);

  // List-probe argv.
  const la = a.buildDshowListArgs();
  ok("dshow list args: -list_devices true", la.join(" ").indexOf("-list_devices true") !== -1);
  ok("dshow list args: dshow dummy input", la.indexOf("dshow") !== -1 && la.indexOf("dummy") !== -1);

  // Device-aware input selector + recorder argv (a name with spaces/parens is one token).
  eq("ffmpegInput win32 + device", JSON.stringify(a.ffmpegInputArgs("win32", "Mic (X)")), JSON.stringify(["-f", "dshow", "-i", "audio=Mic (X)"]));
  ok("buildHostRecorder win32 + device embeds audio=<name>", a.buildHostRecorder("ffmpeg", "win32", "Mic (X)").args.indexOf("audio=Mic (X)") !== -1);

  // resolveWinMicDevice: a list child that emits stderr then closes.
  function listChild(stderrText) {
    const c = new EventEmitter(); c.stderr = new EventEmitter();
    c._drive = () => { if (stderrText) c.stderr.emit("data", Buffer.from(stderrText)); c.emit("close", 1); };
    return c;
  }
  {
    let child; const spawn = () => (child = listChild(modern));
    let got = "unset"; a.resolveWinMicDevice(spawn, (d) => { got = d; });
    child._drive();
    eq("resolveWinMicDevice: returns the first audio device", got, "Microphone (Realtek Audio)");
  }
  {
    const spawn = () => { throw new Error("spawn ffmpeg ENOENT"); };
    let got = "unset"; a.resolveWinMicDevice(spawn, (d) => { got = d; });
    eq("resolveWinMicDevice: ffmpeg missing -> null", got, null);
  }
  {
    let child; const spawn = () => (child = listChild("no devices listed"));
    let got = "unset"; a.resolveWinMicDevice(spawn, (d) => { got = d; });
    child._drive();
    eq("resolveWinMicDevice: empty list -> null", got, null);
  }

  // A fake recorder child (stdout + kill/exit).
  function fakeRec2() {
    const c = new EventEmitter(); c.stdout = new EventEmitter();
    c.kill = (s) => { c.killed = true; c.emit("exit", 0, s); };
    return c;
  }

  // Provider (win32): enumerates the device, then records with it; caches across connects.
  {
    const spawned = [];
    const spawn = (file, args) => {
      const isList = (args || []).indexOf("-list_devices") !== -1;
      const c = isList ? listChild(modern) : fakeRec2();
      spawned.push({ file, args, isList, c });
      return c;
    };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32" });
    provider(() => {}, () => {});
    ok("provider(win32): first spawn is the device-list probe", spawned[0].isList === true);
    spawned[0].c._drive();
    eq("provider(win32): then spawns the recorder", spawned[1].file, "ffmpeg");
    ok("provider(win32): recorder uses the enumerated device", spawned[1].args.indexOf("audio=Microphone (Realtek Audio)") !== -1);
    provider(() => {}, () => {}); // second connection
    ok("provider(win32): caches the device (no re-enumeration)", spawned[2].isList === false && spawned[2].file === "ffmpeg");
  }

  // Provider (win32): no capture device found -> onError('no-device') + fall back to sox.
  {
    const spawned = [];
    const spawn = (file, args) => {
      const isList = (args || []).indexOf("-list_devices") !== -1;
      const c = isList ? listChild("no audio devices at all") : fakeRec2();
      spawned.push({ file, args, isList, c });
      return c;
    };
    let reason = null;
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32", onError: (r) => { reason = r; } });
    provider(() => {}, () => {});
    spawned[0].c._drive();
    eq("provider(win32,no device): onError('no-device')", reason, "no-device");
    eq("provider(win32,no device): falls back to sox rec", spawned[1].file, "rec");
  }

  // Provider (win32): an explicit device override skips enumeration entirely.
  {
    const spawned = [];
    const spawn = (file, args) => { const c = fakeRec2(); spawned.push({ file, args }); return c; };
    const provider = a.makeHostMicProvider({ _spawn: spawn, _platform: "win32", device: "USB Mic" });
    provider(() => {}, () => {});
    eq("provider(win32,override): no list probe, spawns ffmpeg directly", spawned.length, 1);
    ok("provider(win32,override): uses the override device", spawned[0].args.indexOf("audio=USB Mic") !== -1);
  }
})();

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
