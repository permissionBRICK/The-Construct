"use strict";
// Microphone passthrough for the Construct control panel — host side.
//
// WHY THIS EXISTS — Claude Code's speech-to-text ("/voice", the chat mic button)
// records with the *local* `rec`/`arecord` on the machine the agent runs on. Over
// Remote-SSH the agent runs on the VM, which is deviceless: no mic, and the speech
// gate is disabled for remote windows anyway. This feature bridges both halves:
//   • VM side (vm/*.sh, pushed over SSH on enable) — a `rec`/`arecord` SHIM in
//     /usr/local/bin that, instead of touching hardware, connects to a loopback
//     TCP port and streams whatever raw PCM arrives there to stdout, dying on
//     SIGTERM exactly like the real recorders; plus a reversible one-substring
//     patch that neutralises the remoteName speech gate so the mic button reappears.
//   • host side (this file) — a hidden webview captures the LOCAL mic with
//     getUserMedia, an AudioWorklet downsamples it to the recorder contract
//     (16 kHz mono signed-16 LE), and the PCM is streamed to a local TCP server.
//     `ssh -R <vmPort>:127.0.0.1:<hostPort> agent-vm` reverse-forwards that server
//     onto the VM's loopback, where the shim reads it.
//
// ON-DEMAND (the central design decision) — Claude spawns `rec` only while it is
// actually recording and SIGTERMs it the instant recording stops, so on the VM the
// shim's TCP *connection* is the record-window signal. The host therefore opens the
// mic ONLY while a tunnel client is connected and releases it on disconnect; the mic
// is never hot continuously. When the webview mic is blocked (no permission / no
// device / running headless) we fall back to a bundled `sox` capture — same contract.
//
// WHERE THIS RUNS — like lifecycle.js/vmpower.js this is part of the UI extension,
// so its Node runs on the user's LOCAL machine even when the window is remote. That
// single vantage point owns both the local mic and the SSH tunnel to the VM.
//
// TESTABILITY — every pure/parsing/builder piece (the guard-patch transform + its
// reversal + double-apply guard, the vm/ script builders, the `ssh -R` argv, port
// selection, the PCM/format constants) is a standalone exported function with no
// vscode/fs/net dependency, and the enable/disable orchestration takes injectable
// ssh-runner + spawner seams. Only the actual mic capture + a live VM can be
// exercised at runtime; those paths degrade honestly (see test/audio.test.js).

const net = require("net");
const path = require("path");

// ── Recorder contract (from the installed anthropic.claude-code-*/extension.js) ──
// Claude records raw PCM, signed 16-bit little-endian, 16 kHz, MONO, on stdout,
// and stops the recorder with SIGTERM. Everything downstream (the AudioWorklet
// downsample, the TCP framing, the shim) is pinned to exactly this format so the
// bytes the VM shim emits are byte-identical to what real `rec`/`arecord` would.
const FORMAT = Object.freeze({
  sampleRate: 16000,   // Hz
  channels: 1,         // mono
  bitDepth: 16,        // signed 16-bit
  encoding: "signed",  // S16LE
  endianness: "le",
});
// Bytes per sample-frame (mono S16 → 2). Handy for framing math + tests.
const BYTES_PER_FRAME = FORMAT.channels * (FORMAT.bitDepth / 8);

// The recorder argv Claude uses. We reproduce these EXACTLY in the VM shim (which
// impersonates `rec`/`arecord`) and in the host `sox` fallback so the format is
// never in doubt. `-` = stdout for sox/rec; arecord writes stdout by default.
const REC_ARGV = ["-q", "--buffer", "1024", "-t", "raw", "-r", "16000", "-e", "signed", "-b", "16", "-c", "1", "-"];
const ARECORD_ARGV = ["-q", "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "raw"];

// ── Host-side mic capture (the REAL capture path) ───────────────────────────────
// A VS Code webview CANNOT open the local mic: VS Code embeds every webview in an
// iframe whose Permissions-Policy `allow` attribute is fixed to
//   allow="cross-origin-isolated; autoplay; local-network-access; clipboard-read; clipboard-write;"
// — with NO `microphone`. The extension has no way to add `microphone` to that
// embedder-controlled attribute, so `getUserMedia({audio:true})` inside the webview
// is rejected with NotAllowedError and no PCM ever flows (the shim then reads a
// connected-but-empty socket → the "completely silent signal" symptom). So we
// capture on the HOST with a native recorder instead — the UI extension's Node runs
// locally, so it can spawn one — emitting the exact recorder contract on stdout.
//
// We reproduce Claude's own recorder contract byte-for-byte: raw PCM, signed 16-bit
// little-endian, 16 kHz, MONO. Prefer ffmpeg (ships on the Construct host toolchain,
// cross-platform, default input device via dshow/avfoundation/pulse); fall back to
// sox `rec` (already bundled for provisioning). Both write raw PCM to stdout so the
// bytes are identical to what the VM shim would otherwise forward.
//
// buildHostRecorder is PURE (returns {file, args} for a given tool + platform) so the
// argv is unit-tested without spawning anything; the actual spawn is a caller seam.

// ffmpeg's input-device selector differs per OS. On Windows dshow needs an EXACT
// capture-device NAME (there is NO `audio=default` pseudo-device — passing it fails
// to open, so the recorder exits and the mic is silent). `device` is that resolved
// name (from resolveWinMicDevice / the construct.micDevice override); it is a single
// argv token, so no shell quoting is needed even for names with spaces/parens. macOS
// avfoundation and Linux pulse/alsa DO accept a default selector. When no Windows
// device is known we fall back to `audio=default` only as a last resort (it will
// likely fail to open → honest "no audio", never silence-forever).
function ffmpegInputArgs(platform, device) {
  if (platform === "win32") return ["-f", "dshow", "-i", "audio=" + (device || "default")];
  if (platform === "darwin") return ["-f", "avfoundation", "-i", (device ? (":" + device) : ":default")];
  return ["-f", "pulse", "-i", device || "default"];
}

// argv for a device-listing probe: `ffmpeg -list_devices true -f dshow -i dummy`
// prints the host's DirectShow devices to STDERR then exits non-zero (expected). Pure.
function buildDshowListArgs() {
  return ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"];
}

// Parse the friendly audio-device names out of `ffmpeg -list_devices` stderr. Handles
// BOTH historical formats: modern ffmpeg tags each name line with a `(audio)`/`(video)`
// suffix; older ffmpeg groups them under "DirectShow audio devices" section headers.
// "Alternative name" lines are skipped (they're the @device_... moniker, not a name).
// Returns the audio device names in listed order (first = the one we default to). Pure.
function parseDshowAudioDevices(text) {
  const names = [];
  let section = null; // 'audio' | 'video' | null (older-format section tracking)
  for (const raw of String(text || "").split(/\r?\n/)) {
    const line = raw.trim();
    if (/DirectShow audio devices/i.test(line)) { section = "audio"; continue; }
    if (/DirectShow video devices/i.test(line)) { section = "video"; continue; }
    if (/Alternative name/i.test(line)) continue; // the @device_... moniker line
    const m = line.match(/"([^"]+)"/);
    if (!m) continue;
    const isAudio = /\(audio\)\s*$/i.test(line);
    const isVideo = /\(video\)\s*$/i.test(line);
    // Modern format: trust the explicit (audio) tag. Older format: no tag → use the
    // current section header. A video-tagged line is never an audio device.
    if (isAudio || (!isVideo && section === "audio")) names.push(m[1]);
  }
  return names;
}

/**
 * Build the argv for a native host-side recorder that emits the recorder contract
 * (raw S16LE / 16 kHz / mono) on stdout. `tool` is "ffmpeg" (default) or "sox".
 * `platform` selects the OS input device (default process.platform). Pure — returns
 * { file, args }; the caller spawns it and pipes stdout to the tunnel socket.
 *
 * ffmpeg: `-f s16le -acodec pcm_s16le -ar 16000 -ac 1 -` after the input selector,
 * `-loglevel quiet -nostats` so stdout is PURE PCM (no banner/progress chatter that
 * would corrupt the stream). sox: `rec` with the recorder argv (REC_ARGV) which
 * already targets raw/16k/S16/mono to stdout. */
function buildHostRecorder(tool, platform, device) {
  const plat = platform || process.platform;
  if (tool === "sox") {
    // sox `rec` reads the default capture device and honours REC_ARGV's output format.
    return { file: "rec", args: REC_ARGV.slice() };
  }
  // ffmpeg (default). Input selector first, then the pinned raw-PCM output on stdout.
  return {
    file: "ffmpeg",
    args: [
      "-hide_banner", "-loglevel", "quiet", "-nostats",
      ...ffmpegInputArgs(plat, device),
      "-ac", "1",            // mono
      "-ar", "16000",        // 16 kHz
      "-f", "s16le",         // raw signed 16-bit little-endian container
      "-acodec", "pcm_s16le",// …explicit codec so the bytes are unambiguous
      "-",                   // stdout
    ],
  };
}

// ── Ports ────────────────────────────────────────────────────────────────────
// vmPort — the loopback port on the VM the shim connects to; the reverse tunnel
// binds it. hostPort — the local TCP server the tunnel forwards to. hostPort=0
// lets the OS pick a free ephemeral port (learned after listen), which we then
// plug into the `ssh -R` argv, so two windows don't collide on a fixed local port.
// vmPort is fixed (8767, matching the panel's copy) because the shim, installed on
// the VM, must know it ahead of time; it is loopback-only on the VM so it doesn't
// clash with anything user-facing.
const DEFAULT_VM_PORT = 8767;

/** The VM-side install locations, referenced by the enable/disable scripts + tests. */
const REC_SHIM_PATH = "/usr/local/bin/rec";
const ARECORD_SHIM_PATH = "/usr/local/bin/arecord";

// ── Guard patch (speech-gate neutraliser) ─────────────────────────────────────
// Claude's isSpeechToTextEnabled() bails for any remote window:
//     isSpeechToTextEnabled(){if(<obj>.env.remoteName)return!1; …authMethod… ;return l5()}
// We neutralise ONLY the remoteName gate by turning the truthy test into an always-
// false one, leaving the auth-method check and everything else intact:
//     …env.remoteName)return!1   →   …env.remoteName&&!1)return!1
// so `remoteName && false` is always falsy and that early `return!1` never fires.
//
// VERSION-GENERIC: the minified object prefix differs between builds (`le.env.`,
// `env.`, …), so we anchor on the build-invariant tail `remoteName)return!1`, NOT
// on `env.`. Confirmed on this VM's 2.1.196/2.1.197 the anchor occurs EXACTLY once
// (the two other `remoteName` uses — `!x.env.remoteName,` and `.remoteName){` — do
// not match), so the replacement is unambiguous. REVERSIBLE + double-apply-guarded:
// applyGuardPatch is a no-op if already patched, revertGuardPatch restores the
// original byte-for-byte, and we only ever touch THIS VM's copy (on enable/disable).
const GATE_ANCHOR = "remoteName)return!1";        // original (unpatched) substring
const GATE_PATCHED = "remoteName&&!1)return!1";   // neutralised substring

/** Is the speech gate already neutralised in `source`? Pure. */
function isGuardPatched(source) {
  return String(source == null ? "" : source).indexOf(GATE_PATCHED) !== -1;
}

/** Does `source` contain the (unpatched) gate we know how to neutralise? Pure. */
function hasGuardGate(source) {
  return String(source == null ? "" : source).indexOf(GATE_ANCHOR) !== -1;
}

/**
 * Neutralise the remoteName speech gate. Returns { changed, source }:
 *   • already patched → { changed:false, source } (idempotent no-op)
 *   • gate present    → { changed:true, source: <patched> } (first match only)
 *   • gate absent     → { changed:false, source } (unknown build; leave untouched)
 * Only the FIRST occurrence is rewritten (there is exactly one on known builds; a
 * hypothetical second remoteName gate would be a different call site we don't want
 * to blindly rewrite). Pure — the caller reads/writes the file. */
function applyGuardPatch(source) {
  const s = String(source == null ? "" : source);
  if (isGuardPatched(s)) return { changed: false, source: s };
  const i = s.indexOf(GATE_ANCHOR);
  if (i === -1) return { changed: false, source: s };
  const patched = s.slice(0, i) + GATE_PATCHED + s.slice(i + GATE_ANCHOR.length);
  return { changed: true, source: patched };
}

/**
 * Revert the neutralised gate back to the original. Returns { changed, source }:
 *   • patched   → { changed:true, source: <original> } (restores byte-for-byte)
 *   • unpatched → { changed:false, source } (idempotent no-op)
 * Only the FIRST occurrence is reverted (matching applyGuardPatch). Pure. */
function revertGuardPatch(source) {
  const s = String(source == null ? "" : source);
  const i = s.indexOf(GATE_PATCHED);
  if (i === -1) return { changed: false, source: s };
  const reverted = s.slice(0, i) + GATE_ANCHOR + s.slice(i + GATE_PATCHED.length);
  return { changed: true, source: reverted };
}

// ── base64-as-data helper (shared by the VM script builders) ───────────────────
// Every piece of data we hand a remote shell — file contents, ports — is embedded
// base64-encoded and decoded ON the VM, so user/host data can NEVER break out of
// the shell (mirrors ssh.runRemoteScript / remote.buildCloneScript). base64 is a
// fixed alphabet (A–Za–z0–9+/=) with no shell metacharacters, so it is safe inside
// a single-quoted string; the surrounding script we author ourselves.
function b64(s) {
  return Buffer.from(String(s), "utf8").toString("base64");
}

// ── VM script builders ─────────────────────────────────────────────────────────
// The three vm/*.sh scripts are the source of truth and are shipped verbatim; these
// builders read them, embed the shim + the chosen vmPort as data, and produce the
// exact bash the VM runs. Keeping the scripts as real files (not string-built here)
// lets `bash -n` lint them and keeps the logic reviewable; the builders only inject
// data the scripts read from the environment. Pure (fs is the caller's job — the
// enable/disable orchestration reads the .sh text and passes it in).

/**
 * Build the remote enable script: writes the shim into /usr/local/bin/rec (+ an
 * arecord symlink), chmod +x, and applies the guard patch to the claude-code
 * extension.js. `shimText` is the literal contents of construct-rec-shim.sh;
 * `enableText` is construct-audio-enable.sh. Both are embedded base64 and decoded
 * on the VM. `vmPort` is passed as an env var (a bare integer we control, but we
 * still validate it's an integer so a bad caller can't inject). Pure. */
function buildEnableScript(enableText, shimText, vmPort) {
  const p = normalizePort(vmPort, DEFAULT_VM_PORT);
  // The shim contents ride as base64-as-data; the enable script decodes it to the
  // install path. CONSTRUCT_VM_PORT is a validated integer literal (no quoting risk).
  return [
    "set -eu",
    "export CONSTRUCT_VM_PORT=" + p,
    "CONSTRUCT_SHIM_B64='" + b64(shimText) + "'",
    "export CONSTRUCT_SHIM_B64",
    // Hand the enable script itself to bash via the same base64-as-data channel so
    // its own contents never pass through a quoting layer.
    "CONSTRUCT_ENABLE_B64='" + b64(enableText) + "'",
    'f=$(mktemp) && printf %s "$CONSTRUCT_ENABLE_B64" | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc',
  ].join("\n");
}

/**
 * Build the remote disable script: removes the shim (+ symlink) and reverts the
 * guard patch. `disableText` is construct-audio-disable.sh, embedded base64. Pure. */
function buildDisableScript(disableText) {
  return [
    "set -eu",
    "CONSTRUCT_DISABLE_B64='" + b64(disableText) + "'",
    'f=$(mktemp) && printf %s "$CONSTRUCT_DISABLE_B64" | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc',
  ].join("\n");
}

/** Resolve the vm/ script directory (sibling of src/). */
function vmScriptsDir() {
  return path.join(__dirname, "..", "vm");
}

// ── ssh -R reverse tunnel argv ─────────────────────────────────────────────────
/**
 * Build the argv for the persistent `ssh -R <vmPort>:127.0.0.1:<hostPort>` reverse
 * tunnel. Unlike ssh.runRemote (one-shot command), this is a LONG-LIVED connection
 * audio.js spawns and tracks: `-N` (no remote command, just the forward) and
 * ServerAliveInterval so a dropped link is noticed and the child exits (we respawn
 * / mark the tunnel down). We reuse ssh.buildSshArgs' connection base (key/alias)
 * but insert the `-R` forward and `-N`, and pass NO remote command.
 *
 * The forward binds the VM's loopback ONLY (127.0.0.1 is the default -R bind
 * address unless GatewayPorts is on, which it isn't), so nothing on the VM's LAN
 * can reach the host mic. Both ports are validated integers. `hasKey` is threaded
 * through exactly like ssh.buildSshArgs so tests stay hermetic. Pure. */
function buildTunnelArgs(ssh, cfg, vmPort, hostPort, hasKey) {
  const c = ssh.resolveCfg({ cfg });
  const vp = normalizePort(vmPort, DEFAULT_VM_PORT);
  const hp = normalizePort(hostPort, 0);
  const common = [
    "-N",                                   // no remote command — forward only
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${c.connectTimeout}`,
    "-o", "ServerAliveInterval=15",         // notice a dead link…
    "-o", "ServerAliveCountMax=3",          // …and let the child exit so we can mark it down
    "-o", "ExitOnForwardFailure=yes",       // if the -R bind fails (port in use), fail fast
    "-R", `${vp}:127.0.0.1:${hp}`,
  ];
  if (hasKey) {
    return ["-i", ssh.keyPath(c), "-o", "IdentitiesOnly=yes", ...common, `${c.user}@${c.vmHost}`];
  }
  return [...common, c.hostAlias];
}

/** Coerce a port to a safe integer in [0,65535]; fall back to `dflt` for anything
 *  non-integer / out of range / non-number. Guards a bad caller from injecting into
 *  the argv or the enable script (the value reaches a shell as CONSTRUCT_VM_PORT=<n>).
 *  Only real numbers are accepted — null/undefined/strings/booleans all fall to the
 *  default (Number(null)===0 and Number("")===0 would otherwise sneak a 0 through).
 *  Pure. */
function normalizePort(port, dflt) {
  if (typeof port !== "number" || !Number.isInteger(port) || port < 0 || port > 65535) return dflt;
  return port;
}

// ── On-demand capture gating (host TCP server + tunnel + mic arming) ────────────
//
// AudioSession orchestrates the whole host side and is the object extension.js
// holds while passthrough is enabled. It is deliberately transport/mic-agnostic:
// the actual mic capture (hidden webview or sox) is supplied as callbacks so the
// gating logic — the genuinely tricky part — unit-tests with fakes.
//
//   • listen()   — start the local TCP server. Never rejects; resolves the chosen
//                  host port (needed for the tunnel argv) or null on failure.
//   • a client connects (the VM shim, via the reverse tunnel) → onCapture(write,stop)
//                  is invoked to ARM the mic; every PCM chunk it produces is written
//                  to the socket. That connection IS the record window.
//   • the client disconnects (Claude SIGTERMed `rec`) → the mic is RELEASED.
//   • close()    — tear everything down (server + any active capture).
//
// Only one recording happens at a time (Claude never runs two `rec`s), but we still
// tolerate a second connection by serving the newest and dropping the previous, so
// a stale half-open socket can't wedge capture.
class AudioSession {
  /**
   * opts:
   *   host        bind address for the local server (default 127.0.0.1 — the tunnel
   *               forwards to loopback, so nothing else need reach it).
   *   onCapture   (writeChunk, done) => stopFn. Called when a client connects; must
   *               start the mic and call writeChunk(buf) per PCM chunk, done() on a
   *               capture error/end. Returns a stop function invoked on disconnect.
   *   onState     (info) => void. Notified on capturing-start / capturing-stop /
   *               server-close so the extension can push {type:'audio',…}.
   *   _net        injectable net module (default require('net')) for tests.
   */
  constructor(opts = {}) {
    this.host = opts.host || "127.0.0.1";
    this.onCapture = typeof opts.onCapture === "function" ? opts.onCapture : null;
    this.onState = typeof opts.onState === "function" ? opts.onState : () => {};
    this._net = opts._net || net;
    this.server = null;
    this.port = null;
    this._activeSocket = null;   // the currently-served shim connection
    this._stopCapture = null;    // stop fn returned by onCapture for the active socket
    this._closed = false;
  }

  /** Start the local server; resolve the bound port (or null on failure). Idempotent-
   *  ish: a second listen() while listening just resolves the existing port. */
  listen(port) {
    if (this.server) return Promise.resolve(this.port);
    return new Promise((resolve) => {
      let settled = false;
      const done = (v) => { if (!settled) { settled = true; resolve(v); } };
      let server;
      try {
        server = this._net.createServer((socket) => this._onConnection(socket));
      } catch (_) {
        return done(null);
      }
      server.on("error", () => {
        // A listen or runtime server error: tear down and report no port. The
        // extension surfaces an honest "couldn't start audio" and resets the switch.
        try { server.close(); } catch (_) {}
        this.server = null;
        done(null);
      });
      this.server = server;
      // 0 = OS picks a free ephemeral port, learned back via address().
      const p = normalizePort(port == null ? 0 : port, 0);
      try {
        server.listen(p, this.host, () => {
          const addr = server.address();
          this.port = addr && typeof addr === "object" ? addr.port : p;
          done(this.port);
        });
      } catch (_) {
        this.server = null;
        done(null);
      }
    });
  }

  /** A shim connected over the reverse tunnel — arm the mic and pipe PCM to it. */
  _onConnection(socket) {
    if (this._closed) { try { socket.destroy(); } catch (_) {} return; }
    // Newest connection wins: drop any previous one so a stale half-open shim can't
    // hold the mic. Claude only records one stream at a time, so this is defensive.
    if (this._activeSocket) this._releaseCapture();
    this._activeSocket = socket;

    let ended = false;
    const release = () => {
      if (ended) return;
      ended = true;
      if (this._activeSocket === socket) this._releaseCapture();
    };
    socket.on("close", release);
    socket.on("error", release);            // a broken pipe (shim died / tunnel dropped) = stop
    socket.on("end", release);

    // Arm the mic. writeChunk pushes one PCM buffer to the shim; if the socket is
    // gone (write returns false / throws) we stop the capture rather than buffer.
    const writeChunk = (buf) => {
      if (ended || socket.destroyed) return false;
      try { return socket.write(buf); } catch (_) { release(); return false; }
    };
    const captureDone = () => { try { socket.end(); } catch (_) {} release(); };

    if (!this.onCapture) {
      // No capturer wired (shouldn't happen when enabled): close cleanly so the shim
      // gets EOF and reports "no audio" rather than hanging.
      try { socket.end(); } catch (_) {}
      this._activeSocket = null;
      return;
    }
    try {
      this._stopCapture = this.onCapture(writeChunk, captureDone) || null;
    } catch (_) {
      // Mic couldn't be armed (blocked / no device): close the socket so the shim
      // ends and Claude falls back / reports no audio. onState carries the honest bit.
      this._stopCapture = null;
      try { socket.destroy(); } catch (_) {}
      this._activeSocket = null;
      this.onState({ capturing: false, error: "capture-failed" });
      return;
    }
    this.onState({ capturing: true });
  }

  /** Stop the active capture + drop its socket (idempotent). */
  _releaseCapture() {
    const stop = this._stopCapture;
    this._stopCapture = null;
    const sock = this._activeSocket;
    this._activeSocket = null;
    if (stop) { try { stop(); } catch (_) {} }
    if (sock) { try { sock.destroy(); } catch (_) {} }
    this.onState({ capturing: false });
  }

  /** Tear the whole session down: release capture + close the server. Idempotent. */
  close() {
    if (this._closed) return;
    this._closed = true;
    this._releaseCapture();
    const s = this.server;
    this.server = null;
    if (s) { try { s.close(); } catch (_) {} }
    this.onState({ capturing: false, closed: true });
  }
}

// ── Host orchestrator ──────────────────────────────────────────────────────────
//
// HostAudio ties the pieces together for the extension: enable pushes the vm/
// scripts over SSH + runs the enable script, opens the local TCP server, and spawns
// the persistent `ssh -R` reverse tunnel; disable stops the tunnel + server and runs
// the disable script. The actual mic capture is delegated to a `mic` provider the
// caller supplies (extension.js implements it with the hidden webview / sox), so the
// whole orchestration unit-tests with injected fakes and no vscode/network.
//
// The `mic` provider is `(writeChunk, done) => stopFn` — exactly AudioSession's
// onCapture. HostAudio forwards it, adding nothing, so the on-demand gating lives in
// AudioSession (arm on connect, release on disconnect).
//
// Injected seams (all default to the real thing):
//   _ssh          the ssh module (runRemoteScript + resolveCfg/keyPath for the argv)
//   _spawn        child_process.spawn (the persistent tunnel child)
//   _readScript   (basename) => string; reads a vm/*.sh file (default fs read)
//   _hasKey       () => boolean; whether the ssh key exists (default fs.existsSync)
//   _net          passed through to AudioSession
class HostAudio {
  constructor(opts = {}) {
    this._ssh = opts._ssh || require("./ssh");
    this._spawn = opts._spawn || require("child_process").spawn;
    this._net = opts._net;
    this._readScript = opts._readScript || defaultReadScript;
    this._hasKey = opts._hasKey || (() => {
      try { return require("fs").existsSync(this._ssh.keyPath(this._ssh.resolveCfg({}))); }
      catch (_) { return false; }
    });
    this.cfg = opts.cfg;
    this.vmPort = normalizePort(opts.vmPort, DEFAULT_VM_PORT);
    // Status sink: (info) => void, where info is {enabled?,capturing?,tunnel?,error?}.
    this.onStatus = typeof opts.onStatus === "function" ? opts.onStatus : () => {};
    // Mic provider: onCapture for AudioSession. May be set after construction.
    this.mic = typeof opts.mic === "function" ? opts.mic : null;
    this.enabled = false;
    this.capturing = false;
    this.gatePatched = false; // whether the VM's chat-mic speech gate is neutralised
    this.session = null;
    this.tunnel = null;      // the persistent ssh -R child
    this._enabling = false;  // guard against re-entrant enable()
    // How long to confirm the ssh -R tunnel stays up before calling enable a success.
    // ExitOnForwardFailure=yes makes ssh exit if the remote -R bind fails, but that
    // (and connect failures) arrive asynchronously after spawn — so we wait out a
    // short window and treat an early exit/error as a startup failure. Injectable for tests.
    this._tunnelSettleMs = opts._tunnelSettleMs != null ? opts._tunnelSettleMs : 1200;
  }

  /** Human-readable tunnel label for the status line, e.g. "vm:8767 → host mic". */
  tunnelLabel(hostPort) {
    return `vm:${this.vmPort} → host mic` + (hostPort ? ` (:${hostPort})` : "");
  }

  /**
   * Enable passthrough: push the vm/ scripts + run enable over SSH, start the local
   * server, and spawn the reverse tunnel. Resolves { ok, error? }. Never rejects.
   * On any failure it rolls back (disable) so we never leave a half-open tunnel or a
   * server bound with nothing behind it. On-demand capture arms itself later when the
   * shim connects.
   */
  async enable() {
    if (this.enabled || this._enabling) return { ok: this.enabled };
    this._enabling = true;
    // Track whether step 1 (the remote enable) actually mutated the VM. If a LATER
    // local step fails, we must revert the VM (shim + gate patch) too — not just tear
    // down the local side — or the VM is left mutated with no live instance to clean up.
    let remoteEnabled = false;
    try {
      // 1) Push scripts + run enable on the VM. The shim + port ride as base64 data.
      const shimText = this._readScript("construct-rec-shim.sh");
      const enableText = this._readScript("construct-audio-enable.sh");
      const script = buildEnableScript(enableText, shimText, this.vmPort);
      const r = await this._ssh.runRemoteScript(script, { timeoutMs: 60000, cfg: this.cfg });
      if (!r || r.code !== 0) {
        this._enabling = false;
        const error = r && r.code < 0 ? "unreachable" : "enable-failed";
        this.onStatus({ enabled: false, capturing: false, error });
        return { ok: false, error, detail: r && r.stderr };
      }
      remoteEnabled = true;
      // Whether the chat-mic speech gate was ACTUALLY neutralised. The enable script
      // is best-effort (it exits 0 even on an unknown Claude build it can't patch), so
      // it prints CONSTRUCT_GATE_PATCHED=1/0 on stdout; we surface that honestly rather
      // than always claiming the mic button is unlocked.
      this.gatePatched = /CONSTRUCT_GATE_PATCHED=1(?![0-9])/.test((r && r.stdout) || "");

      // 2) Start the local TCP server (on-demand mic arming happens per connection).
      this.session = new AudioSession({
        host: "127.0.0.1",
        _net: this._net,
        onCapture: this.mic,
        onState: (info) => this._onSessionState(info),
      });
      const hostPort = await this.session.listen(0);
      if (hostPort == null) {
        this._enabling = false;
        await this._teardownLocal();
        await this._remoteDisable(); // step 1 mutated the VM; revert it
        this.onStatus({ enabled: false, capturing: false, error: "server-failed" });
        return { ok: false, error: "server-failed" };
      }

      // 3) Spawn the persistent reverse tunnel (ssh -R vmPort:127.0.0.1:hostPort) and
      //    CONFIRM it stays up — an ssh that dies right after spawn (missing binary,
      //    connect failure, ExitOnForwardFailure on a busy port) is a startup failure,
      //    not a live tunnel. On failure roll back BOTH sides (the remote enable ran).
      const ok = await this._startTunnel(hostPort);
      if (!ok) {
        this._enabling = false;
        await this._teardownLocal();
        await this._remoteDisable(); // step 1 mutated the VM; revert it
        this.onStatus({ enabled: false, capturing: false, error: "tunnel-failed" });
        return { ok: false, error: "tunnel-failed" };
      }

      this.enabled = true;
      this._enabling = false;
      this.onStatus({ enabled: true, capturing: false, tunnel: this.tunnelLabel(hostPort), gatePatched: this.gatePatched });
      return { ok: true };
    } catch (e) {
      this._enabling = false;
      await this._teardownLocal();
      if (remoteEnabled) await this._remoteDisable(); // only if step 1 got through
      this.onStatus({ enabled: false, capturing: false, error: "exception" });
      return { ok: false, error: "exception", detail: e && e.message };
    }
  }

  /** Spawn the reverse-tunnel child and confirm it survives a short settle window.
   *  Resolves true only if ssh is still up after the window; an early error/exit (the
   *  async way ssh reports a missing binary, connect failure, or ExitOnForwardFailure)
   *  resolves false so enable() can treat it as a real failure and roll back. On a
   *  successful start, wires persistent handlers so a LATER death flips state honestly. */
  async _startTunnel(hostPort) {
    const args = buildTunnelArgs(this._ssh, this.cfg, this.vmPort, hostPort, this._hasKey());
    let child;
    try {
      child = this._spawn("ssh", args, { windowsHide: true, stdio: "ignore" });
    } catch (_) {
      return false;
    }
    this.tunnel = child;
    const survived = await new Promise((resolve) => {
      let settled = false, timer = null;
      function onEarly() { if (settled) return; settled = true; cleanup(); resolve(false); }
      const cleanup = () => {
        if (timer) clearTimeout(timer);
        if (child.removeListener) { child.removeListener("error", onEarly); child.removeListener("exit", onEarly); }
      };
      if (!child.on) { resolve(true); return; } // not an EventEmitter (defensive)
      child.on("error", onEarly);
      child.on("exit", onEarly);
      timer = setTimeout(() => { if (settled) return; settled = true; cleanup(); resolve(true); }, this._tunnelSettleMs);
    });
    if (!survived) {
      if (this.tunnel === child) this.tunnel = null;
      return false; // caller rolls back local + remote
    }
    // Survived startup. A later death means the mic can no longer work, so flip state
    // honestly (don't leave the switch armed with no tunnel behind it). The VM shim +
    // gate patch are left in place — inert without a tunnel (same rationale as
    // dispose()); a re-enable re-establishes them.
    if (child.on) {
      child.on("error", () => this._onTunnelLost(child));
      child.on("exit", () => this._onTunnelLost(child));
    }
    return true;
  }

  /** Handle the reverse tunnel dying AFTER a successful start: release the local side
   *  and report disabled so the UI reflects that passthrough has stopped. */
  _onTunnelLost(child) {
    if (this.tunnel !== child) return; // stale / already replaced or torn down
    this.tunnel = null;
    if (!this.enabled) return; // a disable() is already tearing down
    this.enabled = false;
    if (this.session) { try { this.session.close(); } catch (_) {} this.session = null; }
    this.capturing = false;
    this.onStatus({ enabled: false, capturing: false, error: "tunnel-down", gatePatched: this.gatePatched });
  }

  /** Relay AudioSession capture state up to the status sink. */
  _onSessionState(info) {
    if (info && typeof info.capturing === "boolean") {
      this.capturing = info.capturing;
      this.onStatus({ enabled: this.enabled, capturing: this.capturing, tunnel: this.session ? this.tunnelLabel(this.session.port) : undefined, error: info.error, gatePatched: this.gatePatched });
    }
  }

  /** Kill the tunnel child + close the server (local teardown, no SSH round-trip). */
  async _teardownLocal() {
    if (this.tunnel) { try { this.tunnel.kill && this.tunnel.kill("SIGTERM"); } catch (_) {} this.tunnel = null; }
    if (this.session) { try { this.session.close(); } catch (_) {} this.session = null; }
    this.capturing = false;
  }

  /**
   * Disable passthrough: stop local capture + tunnel, then run the disable script on
   * the VM (remove shim + revert the gate patch). Resolves { ok }. Never rejects.
   * Local teardown happens first + unconditionally so the mic is released even if the
   * VM is unreachable for the cleanup script.
   */
  async disable() {
    const was = this.enabled || !!this.tunnel || !!this.session;
    this.enabled = false;
    this.gatePatched = false;
    await this._teardownLocal();
    const ok = await this._remoteDisable();
    this.onStatus({ enabled: false, capturing: false });
    return { ok, was };
  }

  /** Run the remote disable script over SSH (remove shim + revert the gate patch).
   *  Best-effort; never rejects. Returns true iff it ran and the VM reported success.
   *  Used both by disable() and by enable()'s rollback when a local step fails after
   *  the remote enable already mutated the VM. Idempotent on the VM side. */
  async _remoteDisable() {
    try {
      const disableText = this._readScript("construct-audio-disable.sh");
      const r = await this._ssh.runRemoteScript(buildDisableScript(disableText), { timeoutMs: 60000, cfg: this.cfg });
      return !!(r && r.code === 0);
    } catch (_) {
      return false; // best-effort; the local side is already released
    }
  }

  /** Synchronous best-effort cleanup for extension deactivate: kill tunnel + server.
   *  Does NOT run the remote disable script (deactivate can't await); the shim/patch
   *  are removed on the next explicit disable or are harmless if left (the shim only
   *  streams while a tunnel exists, which it no longer does). */
  dispose() {
    this.enabled = false;
    if (this.tunnel) { try { this.tunnel.kill && this.tunnel.kill("SIGTERM"); } catch (_) {} this.tunnel = null; }
    if (this.session) { try { this.session.close(); } catch (_) {} this.session = null; }
    this.capturing = false;
  }
}

/** Default vm/ script reader — reads the file text from the vm/ dir. */
function defaultReadScript(basename) {
  const fs = require("fs");
  return fs.readFileSync(require("path").join(vmScriptsDir(), basename), "utf8");
}

// ── Host mic provider (AudioSession.onCapture) ──────────────────────────────────
/**
 * Build the mic provider AudioSession/HostAudio arm on each shim connection: it
 * spawns a native host recorder, pipes its raw-PCM stdout to the tunnel socket, and
 * returns a stop function that kills the recorder on disconnect (so the mic is open
 * ONLY while Claude is recording — the on-demand contract). This REPLACES the old
 * webview capture, which could never work (VS Code's webview iframe Permissions-
 * Policy omits `microphone`, so getUserMedia is always rejected → silence).
 *
 * The provider shape is `(writeChunk, done) => stopFn` (exactly AudioSession's
 * onCapture): writeChunk(buf) pushes one PCM chunk to the shim; done() ends the
 * capture (recorder exited / couldn't start), which closes the socket so the shim
 * reports "no audio" instead of feeding silence forever.
 *
 * opts (all optional, all test seams):
 *   _spawn     child_process.spawn (default). Signature (file,args,opts)=>child.
 *   _platform  process.platform override (picks the ffmpeg input device).
 *   tools      ordered recorder preference, default ["ffmpeg","sox"]; on a spawn
 *              ENOENT (tool not installed) we try the next one so a host with only
 *              sox (bundled for provisioning) still works.
 *   device     explicit dshow capture-device name (the construct.micDevice override);
 *              when set, skips enumeration and uses it verbatim.
 *   enumerate  resolve the Windows dshow device via `ffmpeg -list_devices` (default
 *              true); set false to skip straight to sox when no override is given.
 *   onError    (reason) => void, for honest status. reasons: "no-recorder" (neither
 *              ffmpeg nor sox is installed) | "no-device" (ffmpeg is present but no
 *              usable Windows capture device — the user needs to plug in / pick a mic).
 */
function makeHostMicProvider(opts = {}) {
  const spawn = opts._spawn || require("child_process").spawn;
  const platform = opts._platform || process.platform;
  const tools = Array.isArray(opts.tools) && opts.tools.length ? opts.tools.slice() : ["ffmpeg", "sox"];
  const onError = typeof opts.onError === "function" ? opts.onError : () => {};
  const explicitDevice = opts.device || "";
  const enumerate = opts.enumerate !== false;
  // Cache the resolved dshow device for the lifetime of the provider (a session): the
  // device rarely changes and re-listing on every record-start would add latency. Toggle
  // mic off/on to re-enumerate (that rebuilds HostAudio → a fresh provider).
  let cachedDevice = explicitDevice || null;
  let resolvedOnce = !!explicitDevice;

  return (writeChunk, done) => {
    let child = null;
    let stopped = false;

    const spawnRecorder = (idx, device) => {
      if (stopped) return;
      const { file, args } = buildHostRecorder(tools[idx], platform, device);
      let c;
      try {
        c = spawn(file, args, { windowsHide: true, stdio: ["ignore", "pipe", "ignore"] });
      } catch (_) {
        tryTool(idx + 1); // spawn threw synchronously (rare) — next tool
        return;
      }
      child = c;
      // ENOENT (tool not installed) arrives async on 'error'. Fall through to the
      // next recorder rather than failing the whole capture.
      if (c.on) {
        c.on("error", () => {
          if (stopped) return;
          if (child === c) child = null;
          tryTool(idx + 1);
        });
        // A recorder that exits on its own (device busy, no usable device) ends the
        // capture — but ONLY if it was the one still in use (not a superseded fallback).
        c.on("exit", () => {
          if (stopped || child !== c) return;
          child = null;
          done();
        });
      }
      if (c.stdout && c.stdout.on) {
        c.stdout.on("data", (buf) => { if (!stopped) writeChunk(buf); });
      }
    };

    const tryTool = (idx) => {
      if (stopped) return;
      if (idx >= tools.length) { onError("no-recorder"); done(); return; } // exhausted
      // Windows ffmpeg needs an EXACT dshow device name — there's no `audio=default`.
      if (tools[idx] === "ffmpeg" && platform === "win32") {
        if (!resolvedOnce && enumerate) {
          resolveWinMicDevice(spawn, (dev) => {
            resolvedOnce = true;
            cachedDevice = dev;
            if (stopped) return;
            if (!dev) { onError("no-device"); tryTool(idx + 1); return; } // no mic → try sox
            spawnRecorder(idx, dev);
          });
          return;
        }
        if (!cachedDevice) { onError("no-device"); tryTool(idx + 1); return; } // enumeration off/failed
        spawnRecorder(idx, cachedDevice);
        return;
      }
      // macOS/Linux ffmpeg (default selector) or sox (device ignored).
      spawnRecorder(idx, cachedDevice);
    };

    tryTool(0);

    // Stop fn AudioSession calls on disconnect: kill the recorder, release the mic.
    return () => {
      stopped = true;
      const c = child;
      child = null;
      if (c && c.kill) { try { c.kill("SIGTERM"); } catch (_) {} }
    };
  };
}

/**
 * Resolve the Windows dshow capture device ONCE by running the `ffmpeg -list_devices`
 * probe and parsing its stderr. `spawn` is injected. Calls cb(name|null): the first
 * audio device, or null if ffmpeg is missing or lists no audio device. Never throws —
 * a spawn error (ffmpeg not installed) or an empty list resolves null so the caller
 * falls back to sox and reports honest "no-device"/"no-recorder" status.
 */
function resolveWinMicDevice(spawn, cb) {
  let done = false;
  const finish = (d) => { if (done) return; done = true; cb(d || null); };
  let c;
  try {
    c = spawn("ffmpeg", buildDshowListArgs(), { windowsHide: true, stdio: ["ignore", "ignore", "pipe"] });
  } catch (_) { finish(null); return; }
  let err = "";
  if (c.stderr && c.stderr.on) c.stderr.on("data", (b) => { err += b.toString(); });
  if (c.on) {
    c.on("error", () => finish(null));   // ffmpeg not installed
    c.on("close", () => {                // ffmpeg exits non-zero after listing (expected)
      const list = parseDshowAudioDevices(err);
      finish(list.length ? list[0] : null);
    });
  }
}

module.exports = {
  FORMAT, BYTES_PER_FRAME, REC_ARGV, ARECORD_ARGV,
  DEFAULT_VM_PORT, REC_SHIM_PATH, ARECORD_SHIM_PATH,
  GATE_ANCHOR, GATE_PATCHED,
  isGuardPatched, hasGuardGate, applyGuardPatch, revertGuardPatch,
  b64, buildEnableScript, buildDisableScript, vmScriptsDir, defaultReadScript,
  buildTunnelArgs, normalizePort,
  ffmpegInputArgs, buildDshowListArgs, parseDshowAudioDevices,
  buildHostRecorder, resolveWinMicDevice, makeHostMicProvider,
  AudioSession, HostAudio,
};
