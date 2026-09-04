"use strict";
// CLIENT FOR THE REMOTE HOST SERVICE (`constructd`) — the JS half of the remote
// backend (docs/remote-host.md, service/README.md, plan §4.4).
//
// Built to the §4.8 module rules, which is why there is no `require("vscode")`
// anywhere in this file:
//   • the CORE is free of the VS Code API — every dependency is injected
//     (`fetchImpl`, `spawnImpl`, `tlsConnect`, `secrets`, `log`), so the whole client
//     unit-tests under plain node with a fake HTTP layer;
//   • the TRANSPORT is injected, never owned;
//   • its state lives in its OWN namespace (the pin file below, and one SecretStorage
//     key per host) and it reads nothing of another module's;
//   • the pin-file format is a DOCUMENTED CONTRACT shared with
//     lib/AgentVm.Remote.ps1 — the installer console and this extension must agree
//     about which certificate a host presents, or one of them would ask again.
//
// THREE CREDENTIAL PROVIDERS, one shape ({ kind, ... }):
//   token       Authorization: Bearer <secret> — pure Node HTTPS. The secret comes
//               from VS Code SecretStorage (injected as `secrets`).
//   negotiate   Node has no SSPI, so the request is DELEGATED to a spawned
//               powershell.exe -EncodedCommand that dot-sources lib/AgentVm.Remote.ps1
//               and calls Invoke-ConstructApi -UseDefaultCredentials. Same pattern as
//               src/drivers/hyperv-local.js's Get-VM probe.
//   credential  the same helper, with an explicit domain user + password. The PASSWORD
//               IS WRITTEN TO THE CHILD'S STDIN, never into the command line — an
//               encoded command is plainly visible in the process table.
//
// PINNING. The service's certificate is self-signed, so chain validation cannot work
// and is replaced by a SHA-256 fingerprint pinned at enrolment. Every https request
// made here reads the presented certificate and compares it; a mismatch fails the
// request. No pin → no call.

const http = require("http");
const https = require("https");
const tls = require("tls");
const net = require("net");
const fs = require("fs");
const path = require("path");
const cp = require("child_process");

/** The port `constructd` listens on by default (service/README.md Constructd:ListenUrl). */
const DEFAULT_PORT = 7462;
/** Directory (under %LOCALAPPDATA%) shared with lib/AgentVm.Remote.ps1. */
const CONTAINER = "The-Construct";
const REMOTE_DIR = "remote";
/** SecretStorage key prefix for a host's API token. */
const TOKEN_KEY_PREFIX = "construct.remote.token:";

// ── URLs and host keys ───────────────────────────────────────────────────────

/**
 * Normalise what a user typed into the service's base URL. Mirrors
 * ConvertTo-ConstructServiceUrl in lib/AgentVm.Remote.ps1 exactly — the two derive the
 * same host slug, so they share one pin file:
 *   "buildbox"                     -> "https://buildbox:7462"
 *   "buildbox:7462"                -> "https://buildbox:7462"
 *   "https://buildbox.local:7462/" -> "https://buildbox.local:7462"
 * A bare name gets https (a typo'd scheme must never silently downgrade the transport)
 * and the service's own default port. Throws on something that is not a URL. Pure.
 */
function normalizeServiceUrl(value) {
  const raw = String(value == null ? "" : value).trim();
  if (!raw) throw new Error("No service URL given.");
  const withScheme = /^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(raw) ? raw : "https://" + raw;
  let u;
  try { u = new URL(withScheme); } catch (_) {
    throw new Error(`"${value}" is not a usable service URL (expected something like https://buildbox.example.local:7462).`);
  }
  if (u.protocol !== "https:" && u.protocol !== "http:") {
    throw new Error(`Service URL "${value}" must use https (or http for a local development service), not "${u.protocol.replace(":", "")}".`);
  }
  if (!u.hostname) throw new Error(`Service URL "${value}" has no host name.`);
  const port = u.port ? Number(u.port) : DEFAULT_PORT;
  // Node's URL#hostname KEEPS the brackets of an IPv6 literal while .NET's Uri.Host
  // drops them. Strip and re-add, so the bare form (what a socket wants, and what the
  // host slug is derived from) is identical on both sides of the contract.
  const bare = bareHost(u.hostname);
  const host = bare.indexOf(":") >= 0 ? "[" + bare + "]" : bare;
  return `${u.protocol}//${host}:${port}`;
}

/** An IPv6 literal without its URL brackets — what a socket (and the host slug) wants,
 *  and what .NET's Uri.Host already gives the PowerShell client. Pure. */
function bareHost(hostname) {
  const h = String(hostname == null ? "" : hostname);
  return h.startsWith("[") && h.endsWith("]") ? h.slice(1, -1) : h;
}

/** The SNI name for a host, or "" when there must not be one: TLS forbids an IP literal
 *  as a ServerName, and Node throws rather than eliding it. Pure. */
function sniName(hostname) {
  const h = bareHost(hostname);
  return net.isIP(h) ? "" : h;
}

/**
 * Is this host THIS machine, over the loopback interface? "localhost", 127.0.0.0/8 and
 * ::1. Mirrors Test-ConstructLoopbackHost in lib/AgentVm.Remote.ps1. Pure.
 *
 * It exists for one decision: plain http is tolerable only when the bytes never leave
 * the machine (the fake service the tests drive, a development run). To anywhere else it
 * would carry a bearer token in clear AND offer no certificate to pin.
 */
function isLoopbackHost(hostname) {
  const h = bareHost(hostname).trim().toLowerCase();
  if (!h) return false;
  if (h === "localhost") return true;
  const kind = net.isIP(h);
  if (kind === 4) return h.startsWith("127.");
  if (kind === 6) return h === "::1" || h === "::ffff:127.0.0.1" || /^0*:(0*:)*0*1$/.test(h);
  return false;
}

/** Refuse a transport that protects neither the bytes nor the identity of the far end.
 *  https is fine (the pin is enforced separately); http only to loopback. Throws. */
function assertTransportSafe(baseUrl) {
  const p = urlParts(baseUrl);
  if (p.https || isLoopbackHost(p.host)) return;
  throw new Error(
    `Refusing to talk to the Construct host service at ${p.base} over plain http: your credentials would cross the network unencrypted, and there is no certificate to verify the host with. Use https (plain http is accepted only for a service on this machine).`
  );
}

/** The parts a request needs: { base, host, port, https }. Pure. */
function urlParts(baseUrl) {
  const base = normalizeServiceUrl(baseUrl);
  const u = new URL(base);
  return {
    base,
    host: bareHost(u.hostname),   // no brackets — what a socket wants
    port: Number(u.port),
    https: u.protocol === "https:",
  };
}

/**
 * File-name-safe key for one host, so two hosts never share a pin or a token.
 * Mirrors Get-ConstructRemoteHostSlug: "<host>_<port>", lowercased, anything outside
 * [a-z0-9._-] replaced by "_". Pure.
 */
function hostSlug(baseUrl) {
  const p = urlParts(baseUrl);
  return `${p.host}_${p.port}`.toLowerCase().replace(/[^a-z0-9._-]/g, "_");
}

/** %LOCALAPPDATA%\The-Construct\remote, or null when no base dir resolves. Mirrors
 *  Get-ConstructRemoteStoreDir (and host.js's localAppData). Pure. */
function remoteStoreDir(env) {
  const e = env || process.env;
  const base = e.LOCALAPPDATA || e.TEMP || "";
  return base ? path.join(base, CONTAINER, REMOTE_DIR) : null;
}

/** Absolute path of a host's pin file, or null. Pure. */
function pinPath(baseUrl, env) {
  const dir = remoteStoreDir(env);
  return dir ? path.join(dir, hostSlug(baseUrl) + ".pin") : null;
}

/** SecretStorage key for a host's API token. Pure. */
function tokenSecretKey(baseUrl) {
  return TOKEN_KEY_PREFIX + hostSlug(baseUrl);
}

// ── Fingerprints ─────────────────────────────────────────────────────────────

/**
 * One canonical spelling of a SHA-256 fingerprint: uppercase hex, colon-separated
 * pairs. Accepts the value with colons, spaces or neither (node's `fingerprint256` is
 * colon-separated, PowerShell's stored form is the same, a human may paste either), so
 * the two writers can never produce a file the other rejects. "" when it is not 64 hex
 * digits. Pure — mirrors Format-ConstructFingerprint.
 */
function formatFingerprint(value) {
  const hex = String(value == null ? "" : value).replace(/[^0-9A-Fa-f]/g, "").toUpperCase();
  if (hex.length !== 64) return "";
  return (hex.match(/.{2}/g) || []).join(":");
}

/** Do two spellings denote the same certificate? "" never matches. Pure. */
function fingerprintsMatch(expected, actual) {
  const a = formatFingerprint(expected);
  const b = formatFingerprint(actual);
  return !!a && a === b;
}

/** Read a host's pinned fingerprint, or "". Never throws. */
function readPin(baseUrl, opts = {}) {
  const file = opts.pinPath || pinPath(baseUrl, opts.env);
  if (!file) return "";
  const readFileSync = opts.readFileSync || fs.readFileSync;
  try { return formatFingerprint(readFileSync(file, "utf8")); } catch (_) { return ""; }
}

/**
 * Pin a host's fingerprint — written to the SAME file lib/AgentVm.Remote.ps1 reads, so
 * a host added in VS Code is already trusted when Auto-Install.ps1 later runs in a
 * console (and vice versa). The fingerprint is a public hash, not a secret, so it is
 * plain text; the TOKEN is what SecretStorage/DPAPI protect. Throws on I/O failure.
 */
function writePin(baseUrl, fingerprint, opts = {}) {
  const fp = formatFingerprint(fingerprint);
  if (!fp) throw new Error(`"${fingerprint}" is not a SHA-256 certificate fingerprint (64 hex digits).`);
  const file = opts.pinPath || pinPath(baseUrl, opts.env);
  if (!file) throw new Error("No %LOCALAPPDATA% to store the certificate pin in.");
  const mkdirSync = opts.mkdirSync || fs.mkdirSync;
  const writeFileSync = opts.writeFileSync || fs.writeFileSync;
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, fp, "utf8");
  return file;
}

/**
 * The fingerprint the service is presenting right now, read over a TLS connection of
 * our own that deliberately accepts ANY certificate — looking at it is the entire
 * point, and nothing is trusted as a result. Resolves "" for an http URL (no
 * certificate to pin; development/fake services only). Rejects when the host cannot be
 * reached, so the caller can tell "wrong URL" from "wrong certificate".
 * `opts.tlsConnect` is the test seam.
 */
function fetchFingerprint(baseUrl, opts = {}) {
  const p = urlParts(baseUrl);
  if (!p.https) return Promise.resolve("");
  const connect = opts.tlsConnect || tls.connect;
  const timeoutMs = opts.timeoutMs || 10000;
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (err, value) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { socket && socket.destroy(); } catch (_) {}
      if (err) reject(err); else resolve(value);
    };
    const timer = setTimeout(
      () => finish(new Error(`Timed out connecting to ${p.host}:${p.port}.`)),
      timeoutMs
    );
    let socket;
    try {
      // No `servername` for an IP literal: TLS forbids one, and Node throws rather than
      // eliding it (which would make every IP-addressed host unreadable).
      const connOpts = { host: p.host, port: p.port, rejectUnauthorized: false };
      const sni = sniName(p.host);
      if (sni) connOpts.servername = sni;
      socket = connect(connOpts, () => {
        const cert = socket.getPeerCertificate();
        const fp = formatFingerprint(cert && cert.fingerprint256);
        if (!fp) return finish(new Error(`The service at ${p.base} presented no usable certificate.`));
        finish(null, fp);
      });
    } catch (e) {
      return finish(e);
    }
    socket.on("error", (e) => finish(new Error(`Could not read the certificate of ${p.base}: ${e.message}`)));
  });
}

// ── Requests ─────────────────────────────────────────────────────────────────

/** "/whoami" | "whoami" | "/api/v1/whoami" -> "/api/v1/whoami", so call sites can
 *  write routes the way service/README.md does. Pure. */
function apiPath(route) {
  let p = String(route == null ? "" : route).trim();
  if (!p.startsWith("/")) p = "/" + p;
  if (!p.startsWith("/api/")) p = "/api/v1" + p;
  return p;
}

/**
 * Map a response to the message a user should see. ONE place, so every caller branches
 * on `status` rather than pattern-matching prose. RFC 7807 problem documents are
 * unwrapped to their title/detail — that is the sentence the service wrote. Pure.
 */
function mapError(status, body, context) {
  const where = context ? ` (${context})` : "";
  let detail = "";
  if (body && typeof body === "object") {
    detail = [body.title, body.detail].filter(Boolean).join(" — ");
  } else if (typeof body === "string" && body.trim()) {
    detail = body.trim().slice(0, 400);
  }
  const suffix = detail ? `: ${detail}` : "";
  switch (status) {
    case 0: return `Could not reach the Construct host service${where}${suffix}`;
    case 401: return `The Construct host service rejected these credentials${where}${suffix}`;
    case 403: return `The Construct host service refused this${where} — you are not enrolled, or it is not your VM${suffix}`;
    case 404: return `The Construct host service has no such thing${where}${suffix}`;
    case 409: return `The Construct host service cannot do that right now${where}${suffix}`;
    default: return `The Construct host service answered HTTP ${status}${where}${suffix}`;
  }
}

/** An error carrying the HTTP status, so callers can branch (401 -> re-authenticate). */
function apiError(status, body, context) {
  const e = new Error(mapError(status, body, context));
  e.status = status;
  e.body = body;
  return e;
}

/** Parse a response body: JSON when it parses, the raw string otherwise, null when
 *  empty. Pure. */
function parseBody(text) {
  const s = String(text == null ? "" : text);
  if (!s.trim()) return null;
  try { return JSON.parse(s); } catch (_) { return s; }
}

/**
 * An https.Agent that will only ever hand the request a socket whose peer certificate
 * matches `pin`.
 *
 * WHY AN AGENT AND NOT `checkServerIdentity`: the certificate is self-signed, so
 * `rejectUnauthorized` has to be off — and with it off Node never calls
 * `checkServerIdentity` at all. A pin checked there is not checked. (Verified against a
 * real self-signed server: the request succeeded and the Authorization header was
 * delivered.) So the verification moves one layer down, to where it cannot be skipped:
 * the connection is made here, the presented certificate is compared, and the socket is
 * handed to the HTTP layer through the agent's callback ONLY on a match. On a mismatch
 * the socket is destroyed and the callback gets an error, so the request is never given
 * a socket to write its headers — including `Authorization` — to.
 *
 * `tlsConnect` is the test seam; `maxSockets: 1, keepAlive: false` because an agent is
 * built per request and must not outlive it.
 */
function pinnedHttpsAgent(pin, opts = {}) {
  const connect = opts.tlsConnect || tls.connect;
  const agent = new https.Agent({ keepAlive: false, maxSockets: 1 });
  agent.createConnection = (connOpts, cb) => {
    let settled = false;
    const done = (err, socket) => {
      if (settled) return;
      settled = true;
      if (err) { try { socket && socket.destroy(); } catch (_) {} }
      cb(err, err ? undefined : socket);
    };
    const target = bareHost(connOpts.host || connOpts.hostname || "");
    const servername = sniName(connOpts.servername || target);
    // rejectUnauthorized:false is what makes a self-signed certificate reachable at all;
    // the fingerprint comparison below is the identity check that replaces the chain.
    // `servername` is OMITTED for an IP literal — TLS forbids one as a ServerName and
    // Node throws instead of eliding it, which would turn every IP-addressed service
    // into a connection error.
    const tlsOpts = Object.assign({}, connOpts, { rejectUnauthorized: false });
    if (servername) { tlsOpts.servername = servername; } else { delete tlsOpts.servername; }
    const socket = connect(
      tlsOpts,
      () => {
        const cert = socket.getPeerCertificate();
        const actual = formatFingerprint(cert && cert.fingerprint256);
        if (!pin) {
          return done(new Error(
            `The certificate of ${target} has not been confirmed on this machine. Run "The Construct: Add Remote Host" once so its fingerprint can be checked and pinned.`
          ), socket);
        }
        if (!fingerprintsMatch(pin, actual)) {
          return done(new Error(
            `Certificate fingerprint mismatch for ${target}.\n    pinned:    ${formatFingerprint(pin)}\n    presented: ${actual || "(none)"}`
          ), socket);
        }
        done(null, socket);
      }
    );
    socket.on("error", (e) => done(e, socket));
    // Nothing is returned: the HTTP layer receives the socket through `cb`, and only
    // after the certificate has been verified.
    return undefined;
  };
  return agent;
}

/**
 * The DEFAULT HTTP implementation: node core http/https with the certificate pin
 * enforced on the socket, before the request exists (see pinnedHttpsAgent).
 *
 * Signature is fetch-like on purpose — `(url, init) => Promise<{status, text}>` — so a
 * test can inject a fake with no sockets at all.
 */
function nodeHttp(url, init = {}) {
  const u = new URL(url);
  const isHttps = u.protocol === "https:";
  const pin = init.pin || "";
  return new Promise((resolve, reject) => {
    const options = {
      method: init.method || "GET",
      hostname: u.hostname,
      port: u.port,
      path: u.pathname + u.search,
      headers: init.headers || {},
      timeout: init.timeoutMs || 100000,
    };
    if (isHttps) {
      options.agent = pinnedHttpsAgent(pin, init);
    }
    const req = (isHttps ? https : http).request(options, (res) => {
      let data = "";
      res.setEncoding("utf8");
      res.on("data", (d) => { if (data.length < 1024 * 1024) data += d; });
      res.on("end", () => resolve({ status: res.statusCode, text: data }));
    });
    req.on("timeout", () => { req.destroy(new Error("The request timed out.")); });
    req.on("error", reject);
    if (init.body != null) req.write(init.body);
    req.end();
  });
}

// ── The PowerShell delegate (Negotiate / explicit credentials) ───────────────

/** A PowerShell single-quoted string literal (embedded quotes doubled). Pure. */
function psSingleQuote(s) { return "'" + String(s == null ? "" : s).replace(/'/g, "''") + "'"; }

/**
 * The PowerShell that performs ONE call with a Windows credential and prints a single
 * `CONSTRUCT_API {json}` line. Pure, so the exact script is unit-testable.
 *
 * Why a helper process at all: Node has no SSPI, so Kerberos/NTLM is unreachable from
 * here — but `Invoke-WebRequest -UseDefaultCredentials` in the user's own PowerShell has
 * it, and lib/AgentVm.Remote.ps1 already owns the pinning and the error mapping. This is
 * the same encoded-command delegation src/drivers/hyperv-local.js uses for Get-VM.
 *
 * The PASSWORD (credential mode) is read from STDIN, never embedded: an -EncodedCommand
 * is plainly readable in the process table. The request body is base64 (not a secret,
 * but it must survive quoting). Nothing here prints a credential.
 */
function buildDelegateScript(spec) {
  const { remoteLib, baseUrl, method, route, bodyJson, kind, user } = spec;
  const lines = [
    "$ErrorActionPreference = 'Stop'",
    "$ProgressPreference = 'SilentlyContinue'",
    ". " + psSingleQuote(remoteLib),
  ];
  if (kind === "credential") {
    lines.push(
      "$pw = [Console]::In.ReadLine()",
      "$sec = ConvertTo-SecureString $pw -AsPlainText -Force",
      "$cred = New-Object System.Management.Automation.PSCredential(" + psSingleQuote(user) + ", $sec)",
      "$auth = New-ConstructApiAuth -Mode credential -Credential $cred"
    );
  } else {
    lines.push("$auth = New-ConstructApiAuth -Mode negotiate");
  }
  lines.push("$body = $null");
  if (bodyJson) {
    lines.push(
      "$body = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(" +
        psSingleQuote(Buffer.from(bodyJson, "utf8").toString("base64")) + "))"
    );
  }
  lines.push(
    "$r = Invoke-ConstructApi -BaseUrl " + psSingleQuote(baseUrl) +
      " -Method " + psSingleQuote(method) +
      " -Path " + psSingleQuote(route) + " -Body $body -Auth $auth -NoThrow",
    "$envelope = [ordered]@{ status = (Get-ConstructApiLastStatus); error = (Get-ConstructApiLastError); body = $r }",
    "Write-Output ('CONSTRUCT_API ' + ($envelope | ConvertTo-Json -Depth 12 -Compress))"
  );
  return lines.join("\n");
}

/** argv for the delegate. -EncodedCommand (base64 UTF-16LE) so no shell/quoting layer
 *  can mangle the script. Pure. */
function buildDelegateLaunch(spec) {
  const command = buildDelegateScript(spec);
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  return {
    file: "powershell.exe",
    spawnArgs: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
    command,
  };
}

/** Pull the single `CONSTRUCT_API {json}` envelope out of the delegate's stdout.
 *  Anything else on stdout (a profile banner, a warning) is ignored. Pure. */
function parseDelegateOutput(stdout) {
  const m = /CONSTRUCT_API (\{[\s\S]*\})\s*$/m.exec(String(stdout || ""));
  if (!m) return null;
  try { return JSON.parse(m[1]); } catch (_) { return null; }
}

/** Run one delegated call. Never rejects for an HTTP failure — it resolves the same
 *  { status, text } shape the HTTP implementation does, so the request layer above is
 *  identical for all three providers. */
function runDelegate(spec, opts = {}) {
  const spawn = opts.spawnImpl || cp.spawn;
  const { file, spawnArgs } = buildDelegateLaunch(spec);
  return new Promise((resolve, reject) => {
    let out = "", err = "", done = false;
    const finish = (e, v) => { if (done) return; done = true; clearTimeout(timer); if (e) reject(e); else resolve(v); };
    const timer = setTimeout(() => {
      try { child && child.kill(); } catch (_) {}
      finish(new Error("The PowerShell helper that authenticates with your Windows account did not answer in time."));
    }, opts.timeoutMs || 120000);
    // The watchdog must not be a reason for the host process to stay alive.
    if (typeof timer.unref === "function") timer.unref();
    let child;
    try { child = spawn(file, spawnArgs, { windowsHide: true }); }
    catch (e) { return finish(e); }
    if (child.stdout) child.stdout.on("data", (d) => { if (out.length < 1024 * 1024) out += d.toString(); });
    if (child.stderr) child.stderr.on("data", (d) => { if (err.length < 64 * 1024) err += d.toString(); });
    child.on("error", (e) => finish(e));
    child.on("close", () => {
      const env = parseDelegateOutput(out);
      if (!env) {
        return finish(new Error(
          "The PowerShell helper that authenticates with your Windows account produced no answer" +
          (err.trim() ? ": " + err.trim().slice(0, 300) : ".")
        ));
      }
      // The helper already parsed the body; re-serialise so the caller sees exactly the
      // { status, text } shape nodeHttp produces. finish(), not resolve(), so the
      // watchdog timer is cleared — an uncleared one keeps the event loop alive.
      const text = env.body == null ? "" : (typeof env.body === "string" ? env.body : JSON.stringify(env.body));
      finish(null, { status: Number(env.status) || 0, text, delegateError: String(env.error || "") });
    });
    if (child.stdin) {
      try {
        if (spec.kind === "credential") child.stdin.write(String(spec.password == null ? "" : spec.password) + "\n");
        child.stdin.end();
      } catch (_) { /* the child may have died already; the close handler reports it */ }
    }
  });
}

// ── The client ───────────────────────────────────────────────────────────────

/**
 * A client bound to one host + one credential provider.
 *
 * opts:
 *   baseUrl    the service (any spelling normalizeServiceUrl accepts)
 *   auth       { kind: "token", token } | { kind: "negotiate" }
 *              | { kind: "credential", user, password }
 *   pin        the expected fingerprint; default: the pinned one for this host
 *   remoteLib  path to lib/AgentVm.Remote.ps1 (needed by the PowerShell providers)
 *   fetchImpl  (url, init) => Promise<{status, text}>   — default: pinning node http
 *   spawnImpl  child_process.spawn                      — default: the real one
 *   env, log
 */
function createClient(opts = {}) {
  const baseUrl = normalizeServiceUrl(opts.baseUrl);
  // Refused at CONSTRUCTION, so no caller can hold a client for a host it must not send
  // a credential to (mirrors Assert-ConstructTransportSafe in the PowerShell client).
  assertTransportSafe(baseUrl);
  const parts = urlParts(baseUrl);
  const auth = opts.auth || { kind: "negotiate" };
  const log = opts.log || (() => {});
  const fetchImpl = opts.fetchImpl || nodeHttp;
  const pin = opts.pin != null ? formatFingerprint(opts.pin) : readPin(baseUrl, opts);

  /** One request. Resolves the parsed body; throws an error carrying `.status`. */
  async function request(method, route, body) {
    const p = apiPath(route);
    const bodyJson = body == null ? null : JSON.stringify(body);
    const context = `${method} ${p}`;
    // No pin, no call — the same fail-closed rule the PowerShell client applies, for
    // EVERY provider (the transports below enforce it too; this is the early, legible
    // refusal). Only for https: a loopback development service has no certificate at all.
    if (parts.https && !pin) {
      throw apiError(0,
        `The certificate of ${parts.host} has not been confirmed on this machine. Run "The Construct: Add Remote Host" once.`,
        context);
    }
    let res;
    try {
      if (auth.kind === "token") {
        const headers = { Accept: "application/json", Authorization: "Bearer " + auth.token };
        if (bodyJson) headers["Content-Type"] = "application/json";
        res = await fetchImpl(baseUrl + p, { method, headers, body: bodyJson, pin, timeoutMs: opts.timeoutMs });
      } else {
        if (!opts.remoteLib) {
          throw new Error("This Construct install cannot authenticate with your Windows account (lib/AgentVm.Remote.ps1 was not found).");
        }
        res = await runDelegate({
          remoteLib: opts.remoteLib, baseUrl, method, route: p, bodyJson,
          kind: auth.kind, user: auth.user, password: auth.password,
        }, opts);
      }
    } catch (e) {
      log(`remotehost: ${context} failed — ${e && e.message ? e.message : e}`);
      throw apiError(0, e && e.message ? e.message : String(e), context);
    }
    const parsed = parseBody(res.text);
    if (res.status >= 200 && res.status < 300) return parsed;
    // A delegated call reports status 0 for a transport failure and carries the
    // PowerShell client's own message, which is more specific than anything here.
    const detail = res.delegateError ? { detail: res.delegateError } : parsed;
    log(`remotehost: ${context} -> HTTP ${res.status}`);
    throw apiError(res.status, detail, context);
  }

  return {
    baseUrl,
    host: parts.host,
    authKind: auth.kind,
    pin,
    request,
    whoami: () => request("GET", "/whoami"),
    listVms: () => request("GET", "/vms"),
    getVm: (name) => request("GET", `/vms/${encodeURIComponent(name)}`),
    getState: (name) => request("GET", `/vms/${encodeURIComponent(name)}/state`),
    getEndpoint: async (name) =>
      readEndpoint(await request("GET", `/vms/${encodeURIComponent(name)}/endpoint`)),
    power: (name, action) => request("POST", `/vms/${encodeURIComponent(name)}/power`, { action }),
    createVm: (spec) => request("POST", "/vms", spec),
    deleteVm: (name) => request("DELETE", `/vms/${encodeURIComponent(name)}`),
    getJob: (id) => request("GET", `/jobs/${encodeURIComponent(id)}`),
  };
}

/**
 * The ONE reading of an endpoint document — `GET /vms/{name}/endpoint`, and the `endpoint`
 * object inside a creation job's result, which have the same shape:
 *
 *     { sshHost: "buildbox.local", sshPort: 2201, publicHost: "work-vm.vpn.example" }
 *
 * `publicHost` (plan §4.12) is where this VM's WEB endpoints live — the service's rendered
 * `Constructd:PublicHostPattern`. SSH is dialled on `sshHost` either way, which is why
 * instances.toSshCfg has no idea this field exists. A service that does not send it (an
 * older build, or one with no pattern) is NOT a special case for the caller: `publicHost`
 * falls back to `sshHost`, which is exactly what "no pattern configured" means there.
 *
 * Returns null when the document carries no usable ssh host. Pure; mirrors
 * ConvertFrom-ConstructVmEndpoint in lib/AgentVm.Remote.ps1.
 */
function readEndpoint(body) {
  if (!body || typeof body !== "object" || Array.isArray(body)) return null;
  const sshHost = typeof body.sshHost === "string" ? body.sshHost.trim() : "";
  if (!sshHost) return null;
  const port = Number(body.sshPort);
  const sshPort = Number.isInteger(port) && port >= 1 && port <= 65535 ? port : 22;
  const stated = typeof body.publicHost === "string" ? body.publicHost.trim() : "";
  return { sshHost, sshPort, publicHost: stated || sshHost };
}

/**
 * The VM state the panel gates on, from the service's own enum. `absent` comes from a
 * 404 and NOTHING else: an unreachable service or a refused credential must read as
 * "can't tell", never as "not installed" — the panel offers to CREATE for `absent`.
 * `saved` and `paused` collapse to `off` because a start resumes them (docs/drivers.md
 * §4). Pure.
 */
function mapVmState(raw) {
  const s = String(raw == null ? "" : raw).trim().toLowerCase();
  if (s === "running") return "running";
  if (s === "off" || s === "saved" || s === "paused") return "off";
  if (s === "absent") return "absent";
  return "unknown";
}

module.exports = {
  DEFAULT_PORT, CONTAINER, REMOTE_DIR, TOKEN_KEY_PREFIX,
  normalizeServiceUrl, bareHost, sniName, isLoopbackHost, assertTransportSafe,
  urlParts, hostSlug, remoteStoreDir, pinPath, tokenSecretKey,
  formatFingerprint, fingerprintsMatch, readPin, writePin, fetchFingerprint,
  apiPath, mapError, apiError, parseBody, nodeHttp, pinnedHttpsAgent,
  psSingleQuote, buildDelegateScript, buildDelegateLaunch, parseDelegateOutput, runDelegate,
  createClient, mapVmState, readEndpoint,
};
