"use strict";
// VM -> host desktop notifications: the pure half.
//
// An agent on the VM runs `construct notify "…"`, which drops one JSON file into
// a spool dir on the VM (/run/construct/notify, tmpfs). The host extension claims
// those files over SSH and raises a REAL Windows toast (notification centre), not
// a VS Code toast — the point is to be seen while VS Code is minimised.
//
// Deliberately ONE-WAY: no replies, no buttons that run anything. Asking the user
// something belongs in the agent's own chat, not in a desktop popup. The text is
// written by an agent, i.e. untrusted input crossing a trust boundary into a shell
// and into XML — so everything here sanitises first and interpolates never.
//
// Pure module: no vscode/child_process dependency, so it is unit-testable in plain
// node. extension.js does the spawning.

/** Spool dir on the VM. tmpfs on purpose: a reboot must not replay old messages. */
const SPOOL_DIR = "/run/construct/notify";

/** Caps. A toast that doesn't fit on screen is just noise, and these bound what a
 *  looping agent can push at the host. */
const MAX_TITLE = 100;
const MAX_BODY = 400;
/** Entries older than this are dropped unshown (VS Code was closed for a while —
 *  "your build finished" from yesterday is not worth a popup). */
const DEFAULT_TTL_MS = 60 * 60 * 1000;
/** Toasts raised per poll; the rest are summarised in one closing toast. */
const MAX_PER_TICK = 5;

/** The app identity Windows shows on the toast + groups it under in the notification
 *  centre. Registered under HKCU by the toast script itself (no admin, no installer). */
const APP_ID = "PermissionBrick.TheConstruct";
const APP_NAME = "The Construct";

/** Clicking the toast opens the control panel through the extension's URI handler.
 *  activationType="protocol" means Windows just launches this URI — no COM server,
 *  no background activator, nothing that could run VM-supplied text as a command. */
const LAUNCH_URI = "vscode://permissionbrick.construct-control-panel/open";

const LEVELS = ["info", "warning", "error"];

/** Normalise a level from the VM to one of LEVELS (unknown/absent -> "info"). */
function normalizeLevel(v) {
  const s = String(v == null ? "" : v).trim().toLowerCase();
  if (s === "warn") return "warning";
  if (s === "err" || s === "critical") return "error";
  return LEVELS.includes(s) ? s : "info";
}

/**
 * Make agent-authored text safe to display: drop control characters (including the
 * newlines that would break the one-entry-per-line claim protocol), collapse runs of
 * whitespace, and cap the length with an ellipsis. Returns "" for nothing usable.
 */
function sanitizeText(s, max) {
  let out = String(s == null ? "" : s)
    // Control characters would break the one-entry-per-line claim protocol
    // (and let a payload forge extra spool lines); U+2028/2029 likewise.
    .replace(/[\u0000-\u001F\u007F\u2028\u2029]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  const limit = Math.max(1, Number(max) || MAX_BODY);
  if (out.length > limit) out = out.slice(0, limit - 1).trimEnd() + "…";
  return out;
}

/**
 * The `claim()` shell function — the heart of the protocol, shared by the one-shot
 * drain and the long-lived watcher so the two can never drift apart.
 *
 * Every pending entry is CLAIMED with an atomic rename before it is printed. That
 * rename is what makes delivery exactly-once when several VS Code windows each watch
 * the same VM: they race, one wins, the losers find the file gone and skip it. Output
 * is one JSON object per line (the writer keeps entries single-line; head/tr enforce
 * it here too, so an oversized or hand-written file cannot desync the rest).
 *
 * The recovery pass matters more than it looks: a watcher can die BETWEEN claiming an
 * entry and printing it (killed window, SIGPIPE on a dead connection, VM shutdown),
 * which would stranded that entry under a name nothing looks at again. Anything left
 * claimed for over a minute is renamed BACK — a live claim lasts microseconds, so this
 * only ever catches the dead — and the host's own TTL decides whether it is still
 * worth showing. Deleting them instead (the obvious sweep) silently loses messages.
 */
function claimFunction() {
  return `claim() {
  [ -d "$d" ] || return 0
  for c in "$d"/*.claimed.*; do
    [ -e "$c" ] || continue
    [ -n "$(find "$c" -maxdepth 0 -mmin +1 2>/dev/null)" ] || continue
    mv -- "$c" "\${c%.claimed.*}" 2>/dev/null || rm -f -- "$c"
  done
  for f in "$d"/*.json; do
    [ -e "$f" ] || continue
    c="$f.claimed.$$"
    mv -- "$f" "$c" 2>/dev/null || continue
    head -c 8192 -- "$c" | tr -d '\\r\\n'
    printf '\\n'
    rm -f -- "$c"
  done
}`;
}

/** One-shot drain: claim and print everything queued right now, then exit. The
 *  watcher below does this on connect; this standalone form is what a "collect now"
 *  path (and the tests) use. */
function buildClaimScript(dir = SPOOL_DIR) {
  return `set -u
d='${String(dir).replace(/'/g, "'\\''")}'
${claimFunction()}
claim
`;
}

/** Seconds the VM-side watcher sleeps between checks when inotifywait is missing. */
const WATCH_FALLBACK_SECONDS = 3;
/** Seconds between heartbeat lines (see buildWatchScript). */
const WATCH_HEARTBEAT_SECONDS = 60;
/** Lines the host must ignore: the watcher's heartbeat. */
const HEARTBEAT_LINE = "#";

/**
 * The long-lived watcher: ONE SSH connection that blocks on the VM until something
 * is queued, then streams it — instead of reconnecting every few seconds to ask.
 *
 * It opens by draining whatever accumulated while the host was away (so a reconnect
 * needs no extra round trip), then waits on inotify and drains again on each event.
 * Without inotify-tools it falls back to a short sleep loop; that loop runs ON THE VM
 * over the existing connection, so it still costs no SSH handshakes.
 *
 * The claim is the same atomic rename as everywhere else, which is what keeps
 * delivery exactly-once when several VS Code windows each hold their own watcher:
 * they race on the rename, one wins, the losers see the file vanish and skip it.
 *
 * The heartbeat earns its keep: if the host's ssh dies without the VM noticing, the
 * next heartbeat write hits a closed pipe and SIGPIPE reaps the orphaned watcher.
 */
function buildWatchScript(opts = {}) {
  const dir = String(opts.dir || SPOOL_DIR).replace(/'/g, "'\\''");
  const fallback = Number(opts.fallbackSeconds) > 0 ? Math.round(Number(opts.fallbackSeconds)) : WATCH_FALLBACK_SECONDS;
  const heartbeat = Number(opts.heartbeatSeconds) > 0 ? Math.round(Number(opts.heartbeatSeconds)) : WATCH_HEARTBEAT_SECONDS;
  return `set -u
d='${dir}'
${claimFunction()}
# Leave nothing behind when this watcher ends: the host closing the connection sends
# a SIGHUP (or breaks the pipe under our next write), and without this the inotifywait
# we started would linger on the VM claiming entries into a dead pipe.
iw=""
cleanup() { if [ -n "$iw" ]; then kill "$iw" 2>/dev/null || true; fi; }
trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE
last=$SECONDS
beat() {
  if [ $((SECONDS - last)) -ge ${heartbeat} ]; then last=$SECONDS; printf '${HEARTBEAT_LINE}\\n'; fi
}
# inotifywait in MONITOR mode (-m), not one-shot: it keeps watching WHILE we drain, so
# an entry queued during a claim pass waits in the pipe instead of being missed. A
# wait-then-claim loop has exactly that blind spot, and the entry then sits unseen
# until some later event happens to wake us. Read through a process substitution
# rather than a pipeline so the loop runs in THIS shell (a pipeline's subshell would
# hide the watcher's pid from cleanup and survive the parent's death as an orphan).
watch_events() {
  command -v inotifywait >/dev/null 2>&1 || return 1
  [ -d "$d" ] || return 1
  exec 3< <(inotifywait -m -q -e close_write,moved_to --format '' "$d" 2>/dev/null)
  iw=$!
  while :; do
    IFS= read -r -t ${heartbeat} -u 3 _
    rc=$?
    # >128 = read timed out (no events): beat and keep waiting.
    # non-zero and <=128 = EOF: inotifywait died; hand back to the caller.
    if [ "$rc" -ne 0 ] && [ "$rc" -le 128 ]; then break; fi
    claim
    beat
  done
  cleanup
  iw=""
  exec 3<&-
  return 0
}
claim
while :; do
  watch_events || true
  # Reached when there is no inotify (older VM), the spool dir isn't there yet, or the
  # watch ended: degrade to a slow poll ON THE VM. Still one connection, still no SSH
  # handshakes — just seconds of latency instead of milliseconds.
  sleep ${fallback}
  claim
  beat
done
`;
}

/**
 * Split a stream chunk into complete lines, keeping the trailing partial line for
 * the next chunk (a JSON entry can straddle two reads). Heartbeats and blank lines
 * are dropped here so callers only ever see candidate entries. `rest` is capped:
 * a peer that streams megabytes without a newline must not grow host memory.
 */
function splitStream(buffered, chunk) {
  const MAX_REST = 64 * 1024;
  const all = String(buffered == null ? "" : buffered) + String(chunk == null ? "" : chunk);
  const parts = all.split("\n");
  let rest = parts.pop();
  if (rest.length > MAX_REST) rest = "";
  const lines = parts.map((l) => l.trim()).filter((l) => l && l !== HEARTBEAT_LINE);
  return { lines, rest };
}

/**
 * Parse claimed spool lines into sanitised entries. Anything unparseable or shaped
 * wrong is dropped silently — a corrupt spool file must not break the poll.
 */
function parseEntries(stdout) {
  const out = [];
  for (const line of String(stdout == null ? "" : stdout).split("\n")) {
    const s = line.trim();
    if (!s || s[0] !== "{") continue;
    let o;
    try { o = JSON.parse(s); } catch (_) { continue; }
    if (!o || typeof o !== "object") continue;
    const body = sanitizeText(o.body, MAX_BODY);
    if (!body) continue; // a notification with nothing to say is not a notification
    const ts = Number(o.ts);
    out.push({
      ts: Number.isFinite(ts) && ts > 0 ? ts : 0,
      level: normalizeLevel(o.level),
      title: sanitizeText(o.title, MAX_TITLE) || APP_NAME,
      body,
      source: sanitizeText(o.source, 60),
    });
  }
  return out;
}

/**
 * Split claimed entries into what to actually show. Oldest first, stale ones
 * dropped, and no more than `max` toasts at once (the caller mentions the rest in
 * one line rather than firing 40 popups after a weekend).
 */
function selectDeliverable(entries, opts = {}) {
  const now = Number(opts.now) || Date.now();
  const ttlMs = Number(opts.ttlMs) > 0 ? Number(opts.ttlMs) : DEFAULT_TTL_MS;
  const max = Number(opts.max) > 0 ? Number(opts.max) : MAX_PER_TICK;
  const list = (entries || []).slice().sort((a, b) => (a.ts || 0) - (b.ts || 0));
  // ts === 0 means the writer gave us no timestamp: keep it rather than guess it stale.
  const fresh = list.filter((e) => !e.ts || now - e.ts <= ttlMs);
  return { deliver: fresh.slice(0, max), stale: list.length - fresh.length, extra: Math.max(0, fresh.length - max) };
}

/** XML text-node escaping for the toast payload. */
function xmlEscape(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&apos;" }[c]));
}

/**
 * The ToastGeneric payload. `duration="long"` (~25s) for warnings/errors so they
 * don't fade before you look up; everything else uses the default and drops into
 * the notification centre either way. No <actions>: this channel is one-way.
 */
function toastXml(entry) {
  const level = normalizeLevel(entry && entry.level);
  const title = sanitizeText(entry && entry.title, MAX_TITLE) || APP_NAME;
  const body = sanitizeText(entry && entry.body, MAX_BODY);
  const attribution = sanitizeText(entry && entry.source, 60);
  const duration = level === "info" ? "" : ' duration="long"';
  return `<toast activationType="protocol" launch="${xmlEscape(LAUNCH_URI)}"${duration}>`
    + '<visual><binding template="ToastGeneric">'
    + `<text>${xmlEscape(title)}</text>`
    + `<text>${xmlEscape(body)}</text>`
    + (attribution ? `<text placement="attribution">${xmlEscape(attribution)}</text>` : "")
    + "</binding></visual></toast>";
}

/**
 * The PowerShell that raises the toast, as a self-contained script. The payload
 * rides as base64 (alphabet [A-Za-z0-9+/=]) so agent text can never break out of
 * the single-quoted literal — the same trick provisioning uses for git identities.
 *
 * It registers the AppUserModelId under HKCU first (Windows silently drops toasts
 * from an unknown app id; no admin or Start-menu shortcut needed), and reports the
 * notifier's Setting: when the user or a group policy has notifications switched
 * off, Show() succeeds silently, so we exit non-zero instead and let the caller
 * fall back to a VS Code notification rather than lose the message.
 */
function buildToastScript(entry) {
  const b64 = Buffer.from(toastXml(entry), "utf8").toString("base64");
  return [
    "$ErrorActionPreference='Stop'",
    "try {",
    `  $appId='${APP_ID}'`,
    "  $k='HKCU:\\Software\\Classes\\AppUserModelId\\'+$appId",
    "  if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }",
    `  if ((Get-ItemProperty -Path $k -Name DisplayName -ErrorAction SilentlyContinue).DisplayName -ne '${APP_NAME}') {`,
    `    New-ItemProperty -Path $k -Name DisplayName -Value '${APP_NAME}' -PropertyType String -Force | Out-Null`,
    "  }",
    "  [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] | Out-Null",
    "  [Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime] | Out-Null",
    "  $xml = New-Object Windows.Data.Xml.Dom.XmlDocument",
    `  $xml.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${b64}')))`,
    "  $n = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)",
    "  if ($n.Setting -ne 'Enabled') { [Console]::Error.WriteLine('toast suppressed: '+$n.Setting); exit 2 }",
    "  $n.Show([Windows.UI.Notifications.ToastNotification]::new($xml))",
    "  exit 0",
    "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }",
  ].join("\n");
}

/**
 * The spawn recipe for the toast. NOT `cmd /c start` (see lifecycle.js): a
 * powershell.exe spawned straight from the extension host inherits no console and
 * cannot allocate one, so nothing flashes on screen — which is exactly what we want
 * here, and the reason the visible flows have to go out of their way to force a
 * window. -WindowStyle Hidden and windowsHide are belt and braces on top.
 */
function buildToastCommand(entry) {
  const script = buildToastScript(entry);
  return {
    file: "powershell.exe",
    args: [
      "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
      "-ExecutionPolicy", "Bypass",
      "-EncodedCommand", Buffer.from(script, "utf16le").toString("base64"),
    ],
    options: { windowsHide: true, stdio: ["ignore", "ignore", "pipe"] },
    script,
  };
}

/** One-line summary for the Construct output channel. */
function logLineFor(entry) {
  return `notify: [${entry.level}] ${entry.title} — ${entry.body}${entry.source ? ` (${entry.source})` : ""}`;
}

/**
 * argv for the watcher's ssh. Long-lived, so unlike ssh.runRemote's one-shot it asks
 * for keepalives: a link that dies silently (VM suspended, laptop slept, Wi-Fi
 * switched) is noticed within ~a minute and the child EXITS, which is what triggers
 * the host's reconnect. Connection base (key vs. ~/.ssh/config alias) is shared with
 * ssh.buildSshArgs so there is one definition of how we reach the VM. `hasKey` is
 * threaded in rather than probed, so this stays pure and testable.
 */
function buildWatchArgs(ssh, cfg, hasKey, script) {
  const c = ssh.resolveCfg({ cfg });
  const command = ssh.wrapScriptCommand(script || buildWatchScript());
  const common = [
    "-T",                                   // no tty: this is a data stream, not a shell
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${c.connectTimeout}`,
    "-o", "ServerAliveInterval=20",         // notice a dead link…
    "-o", "ServerAliveCountMax=3",          // …within ~60s, then exit so we reconnect
  ];
  if (hasKey) {
    return ["-i", ssh.keyPath(c), "-o", "IdentitiesOnly=yes", ...common, `${c.user}@${c.vmHost}`, command];
  }
  return [...common, c.hostAlias, command];
}

/**
 * Reconnect backoff: quick first retry (a VM that just rebooted is back in seconds),
 * doubling to a minute so a powered-off VM costs nothing. Pure.
 */
function reconnectDelayMs(attempt) {
  const n = Number(attempt);
  const step = Number.isFinite(n) && n > 0 ? Math.min(Math.floor(n), 10) : 1;
  return Math.min(RECONNECT_MAX_MS, RECONNECT_BASE_MS * Math.pow(2, step - 1));
}
const RECONNECT_BASE_MS = 2000;
const RECONNECT_MAX_MS = 60000;
/** A connection that lasted at least this long counts as healthy: the next drop
 *  starts backing off from scratch instead of inheriting an old streak. */
const CONNECTION_HEALTHY_MS = 60000;

module.exports = {
  SPOOL_DIR, APP_ID, APP_NAME, LAUNCH_URI, LEVELS,
  MAX_TITLE, MAX_BODY, DEFAULT_TTL_MS, MAX_PER_TICK,
  WATCH_FALLBACK_SECONDS, WATCH_HEARTBEAT_SECONDS, HEARTBEAT_LINE,
  RECONNECT_BASE_MS, RECONNECT_MAX_MS, CONNECTION_HEALTHY_MS,
  normalizeLevel, sanitizeText, buildClaimScript, buildWatchScript, buildWatchArgs,
  splitStream, parseEntries, selectDeliverable, reconnectDelayMs,
  xmlEscape, toastXml, buildToastScript, buildToastCommand, logLineFor,
};
