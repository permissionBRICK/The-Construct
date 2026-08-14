"use strict";
// Plain-node unit tests for src/notify.js — the VM -> host notification protocol.
// Run: node notify.test.js
//
// Two invariants carry most of the weight here:
//   1. Exactly-once delivery across VS Code windows — the claim is an atomic
//      rename, and the wire format is one JSON object per line.
//   2. The text is written by an agent on the VM, i.e. untrusted input crossing
//      into a PowerShell command line and an XML document. It must never be able
//      to escape either.
const notify = require("../src/notify");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── normalizeLevel ────────────────────────────────────────────────────────────
ok("level: info passes through", notify.normalizeLevel("info") === "info");
ok("level: warn -> warning", notify.normalizeLevel("WARN") === "warning");
ok("level: err -> error", notify.normalizeLevel(" err ") === "error");
ok("level: unknown -> info", notify.normalizeLevel("catastrophic") === "info");
ok("level: absent -> info", notify.normalizeLevel(null) === "info");

// ── sanitizeText ──────────────────────────────────────────────────────────────
ok("sanitize: newlines cannot forge a second entry", notify.sanitizeText('a\n{"body":"x"}', 100) === 'a {"body":"x"}');
ok("sanitize: control chars stripped", notify.sanitizeText("a\u0000b\u001bc\u007f", 100) === "a b c");
ok("sanitize: line separators stripped", notify.sanitizeText("a\u2028b", 100) === "a b");
ok("sanitize: whitespace collapsed + trimmed", notify.sanitizeText("  a   b  ", 100) === "a b");
ok("sanitize: capped with an ellipsis", notify.sanitizeText("abcdefghij", 5) === "abcd…");
ok("sanitize: empty stays empty", notify.sanitizeText("   ", 100) === "");

// ── buildClaimScript ──────────────────────────────────────────────────────────
const claim = notify.buildClaimScript();
ok("claim: claims with an atomic rename", /mv -- "\$f" "\$c"/.test(claim));
ok("claim: prints one line per entry", /tr -d '\\r\\n'/.test(claim) && /printf '\\n'/.test(claim));
ok("claim: removes the entry after printing", /rm -f -- "\$c"/.test(claim));
ok("claim: bounds a single entry's size", /head -c 8192/.test(claim));
ok("claim: does nothing when the spool is absent", /\[ -d "\$d" \] \|\| return 0/.test(claim));
ok("claim: a quote in the dir cannot break out", /d='\/tmp\/it'\\''s'/.test(notify.buildClaimScript("/tmp/it's")));
// A watcher can die between claiming an entry and printing it. Deleting what it left
// behind (the obvious sweep) silently loses that notification; putting it back does not.
ok("claim: recovers entries stranded by a dead watcher",
  /mv -- "\$c" "\$\{c%\.claimed\.\*\}"/.test(claim) && /-mmin \+1/.test(claim));
ok("claim: does not steal a claim that is still live", !/-mmin \+0/.test(claim) && !/-delete/.test(claim));

// ── buildWatchScript (the long-lived connection) ─────────────────────────────
const watch = notify.buildWatchScript();
ok("watch: drains what queued while disconnected, before waiting", /\nclaim\nwhile :; do/.test(watch));
ok("watch: shares one claim implementation with the one-shot form",
  watch.includes(claim.split("\n").slice(2, -2).join("\n")));
ok("watch: waits on inotify in monitor mode (no blind spot while draining)",
  /inotifywait -m -q -e close_write,moved_to/.test(watch));
ok("watch: reads events without a subshell, so cleanup can reach the watcher",
  /exec 3< <\(inotifywait/.test(watch) && /read -r -t 60 -u 3/.test(watch));
ok("watch: kills its inotifywait on any exit", /trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE/.test(watch));
ok("watch: falls back to a VM-side sleep loop without inotify-tools",
  /command -v inotifywait[^|]*\|\| return 1/.test(watch) && /sleep 3/.test(watch));
ok("watch: heartbeats so an orphaned watcher dies on the closed pipe",
  new RegExp("printf '" + notify.HEARTBEAT_LINE + "\\\\n'").test(watch));
ok("watch: never exits on its own", /while :; do/.test(watch) && !/\nexit 0\n/.test(watch));
ok("watch: honours a custom spool dir", notify.buildWatchScript({ dir: "/x/y" }).includes("d='/x/y'"));

// ── buildWatchArgs ────────────────────────────────────────────────────────────
const sshStub = {
  resolveCfg: () => ({ user: "root", vmHost: "vm.example", hostAlias: "agent-vm", connectTimeout: 12, keyName: "k" }),
  keyPath: () => "/home/u/.ssh/k",
  wrapScriptCommand: (s) => "WRAPPED:" + s.length,
};
const withKey = notify.buildWatchArgs(sshStub, {}, true);
const noKey = notify.buildWatchArgs(sshStub, {}, false);
ok("args: keepalives so a dead link is noticed and we reconnect",
  withKey.includes("ServerAliveInterval=20") && withKey.includes("ServerAliveCountMax=3"));
ok("args: no tty for a data stream", withKey.includes("-T"));
ok("args: batch mode (never prompts)", withKey.includes("BatchMode=yes"));
ok("args: uses the key when there is one", withKey.includes("-i") && withKey.includes("/home/u/.ssh/k"));
ok("args: falls back to the ssh config alias", noKey.includes("agent-vm") && !noKey.includes("-i"));
ok("args: the watch script is the last argument", /^WRAPPED:/.test(withKey[withKey.length - 1]));

// ── splitStream ───────────────────────────────────────────────────────────────
let s1 = notify.splitStream("", '{"a":1}\n{"b":2}\n{"c":');
ok("stream: complete lines are emitted", s1.lines.length === 2);
ok("stream: a partial line is carried over", s1.rest === '{"c":');
let s2 = notify.splitStream(s1.rest, '3}\n');
ok("stream: the carried line completes on the next chunk", s2.lines.length === 1 && s2.lines[0] === '{"c":3}');
ok("stream: heartbeats are not entries", notify.splitStream("", "#\n#\n").lines.length === 0);
ok("stream: blank lines are ignored", notify.splitStream("", "\n \n").lines.length === 0);
ok("stream: a runaway line without newlines cannot grow forever",
  notify.splitStream("x".repeat(70000), "y").rest === "");

// ── reconnectDelayMs ──────────────────────────────────────────────────────────
ok("backoff: first retry is quick", notify.reconnectDelayMs(1) === notify.RECONNECT_BASE_MS);
ok("backoff: doubles", notify.reconnectDelayMs(3) === notify.RECONNECT_BASE_MS * 4);
ok("backoff: capped", notify.reconnectDelayMs(50) === notify.RECONNECT_MAX_MS);
ok("backoff: a bogus attempt count still yields the base delay", notify.reconnectDelayMs(null) === notify.RECONNECT_BASE_MS);

// ── parseEntries ──────────────────────────────────────────────────────────────
const line = (o) => JSON.stringify(o);
const parsed = notify.parseEntries([
  line({ v: 1, ts: 1000, level: "warn", title: "t", body: "b", source: "root@vm" }),
  "not json",
  "",
  "{ broken",
  line({ v: 1, ts: 2000, level: "info", body: "" }),          // nothing to say -> dropped
  line({ v: 1, ts: 3000, level: "boom", body: "  spaced  " }),
  line([1, 2, 3]),                                             // wrong shape -> dropped
].join("\n"));
ok("parse: keeps the valid entries", parsed.length === 2, JSON.stringify(parsed));
ok("parse: normalizes the level", parsed[0].level === "warning");
ok("parse: junk lines are skipped", !parsed.some((e) => /not json/.test(e.body)));
ok("parse: an empty body is not a notification", !parsed.some((e) => e.ts === 2000));
ok("parse: body is sanitized", parsed[1].body === "spaced");
ok("parse: missing title falls back to the app name", parsed[1].title === notify.APP_NAME);
ok("parse: a JSON array is not an entry", notify.parseEntries(line([1, 2, 3])).length === 0);
ok("parse: a missing timestamp becomes 0", notify.parseEntries(line({ body: "x" }))[0].ts === 0);
ok("parse: garbage input yields nothing", notify.parseEntries("\u0000\u0000").length === 0);

// ── selectDeliverable ─────────────────────────────────────────────────────────
const now = 1000000;
const mk = (ts, body) => ({ ts, level: "info", title: "t", body: body || "b", source: "" });
const sel = notify.selectDeliverable([mk(now - 10), mk(now - 5000), mk(now - 99999999)], { now, ttlMs: 60000, max: 5 });
ok("select: stale entries are dropped", sel.deliver.length === 2 && sel.stale === 1);
ok("select: oldest first", sel.deliver[0].ts < sel.deliver[1].ts);
const capped = notify.selectDeliverable([mk(1), mk(2), mk(3), mk(4)], { now, ttlMs: 0, max: 2 });
ok("select: caps the burst", capped.deliver.length === 2 && capped.extra === 2);
ok("select: an entry with no timestamp is not treated as stale",
  notify.selectDeliverable([mk(0)], { now, ttlMs: 1 }).deliver.length === 1);
ok("select: nothing in, nothing out", notify.selectDeliverable([], { now }).deliver.length === 0);

// ── toastXml ──────────────────────────────────────────────────────────────────
const xmlEvil = notify.toastXml({ level: "info", title: "<b>t</b>", body: '"&<>\'', source: "root@vm" });
ok("xml: markup in the title is escaped", xmlEvil.includes("&lt;b&gt;t&lt;/b&gt;") && !xmlEvil.includes("<b>"));
ok("xml: quotes/ampersands in the body are escaped", xmlEvil.includes("&quot;&amp;&lt;&gt;&apos;"));
ok("xml: attribution carries the source", xmlEvil.includes('placement="attribution">root@vm<'));
ok("xml: no attribution element without a source",
  !notify.toastXml({ level: "info", title: "t", body: "b" }).includes("attribution"));
ok("xml: info toasts use the default duration", !xmlEvil.includes("duration="));
ok("xml: errors linger", notify.toastXml({ level: "error", title: "t", body: "b" }).includes('duration="long"'));
ok("xml: warnings linger", notify.toastXml({ level: "warning", title: "t", body: "b" }).includes('duration="long"'));
ok("xml: click activation is a fixed protocol URI",
  xmlEvil.includes('activationType="protocol"') && xmlEvil.includes(notify.LAUNCH_URI));
ok("xml: one-way — no action buttons", !/<actions>/.test(xmlEvil));

// ── buildToastScript / buildToastCommand ──────────────────────────────────────
const hostile = { level: "info", title: "t'; Remove-Item C:\\ -Recurse #", body: "$(bad) `whoami` '; exit", source: "" };
const script = notify.buildToastScript(hostile);
ok("script: the payload is base64 only", /FromBase64String\('[A-Za-z0-9+/=]+'\)/.test(script));
ok("script: no agent text is interpolated into PowerShell", !script.includes("Remove-Item") && !script.includes("`whoami`"));
ok("script: registers the AppUserModelId (Windows drops unknown-app toasts)",
  script.includes("HKCU:\\Software\\Classes\\AppUserModelId\\") && script.includes(notify.APP_ID));
ok("script: the registration makes us visible in Settings and persistent in the action centre",
  /New-ItemProperty -Path \$k -Name ShowInSettings -Value 1 -PropertyType DWord/.test(script)
  && /New-ItemProperty -Path \$k -Name ShowInActionCenter -Value 1 -PropertyType DWord/.test(script));
ok("script: a registry that refuses the registration is not fatal (the fallback id is Windows' own)",
  /catch \{ \[Console\]::Error\.WriteLine\('app id registration failed: /.test(script));
// The bug this file exists for: an app id registered only under HKCU commonly reports
// DisabledForApplication until Windows has seen a toast from it. Treating that as
// "suppressed" downgraded EVERY notification to a VS Code toast on a machine where
// notifications were perfectly fine.
ok("script: a non-Enabled setting no longer vetoes the toast",
  !script.includes("$n.Setting -ne 'Enabled'") && !/DisabledForApplication'\)? *\{? *exit/.test(script));
// DisabledForApplication is also what Windows reports once the user HAS switched us
// off, so the deliberate case is read where it is unambiguous: the per-app flag the
// Settings UI writes. Without this, ignoring the setting would make the Settings
// toggle we ask for (ShowInSettings) a lie.
ok("script: an explicit mute in Windows settings is honoured before anything is shown",
  script.includes("HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\")
  && /\$sv\.Enabled -eq 0/.test(script)
  && new RegExp(`switched off for '\\+\\$appId\\+' in Settings'\\); exit ${notify.TOAST_EXIT.SUPPRESSED}`).test(script)
  && script.indexOf("$muted") < script.indexOf("CreateToastNotifier"));
ok("script: an absent mute flag is not a refusal (Windows has simply never seen us)",
  /\$muted=\$false/.test(script) && /Get-ItemProperty -Path \$sk -Name Enabled -ErrorAction SilentlyContinue/.test(script));
ok("script: only a user-wide or policy disable suppresses (those are definitive)",
  /\$s -eq 'DisabledForUser' -or \$s -eq 'DisabledByGroupPolicy'/.test(script)
  && new RegExp(`toast suppressed by Windows: '\\+\\$blocked\\); exit ${notify.TOAST_EXIT.SUPPRESSED}`).test(script));
// A user/machine-wide switch-off vetoes the ATTEMPT, not just the candidate that
// reported it — otherwise the other identity would walk straight around it.
ok("script: a definitive disable vetoes every candidate, before anything is shown",
  script.indexOf("if ($blocked)") < script.indexOf("foreach ($o in $order)")
  && script.indexOf("if ($blocked)") < script.indexOf("$order.Count -eq 0")
  && !/if \(\$order\.Count -eq 0\) \{\n? *if \(\$blocked\)/.test(script));
ok("script: tries our own app id first, then the one Windows registers itself",
  script.indexOf(notify.APP_ID) < script.indexOf(notify.FALLBACK_APP_ID)
  && /foreach \(\$id in @\(\$appId,\$fallbackId\)\)/.test(script)
  && script.includes("$order=@($best)+@($rest)"));
ok("script: an Enabled notifier outranks an unrecognised one",
  /if \(\$o\.Enabled\) \{ \$best\+=\$o \}/.test(script) && /else \{ \$rest\+=\$o \}/.test(script));
ok("script: a Show() that throws falls through to the next candidate",
  /catch \{ \$last=\$_\.Exception\.Message; continue \}/.test(script));
ok("script: a toast shown under the fallback identity says so (the host logs it)",
  script.includes("shown under the fallback app id") && script.includes("notifier setting is "));
ok("script: constrained language mode is named, and survives not being able to say so",
  /\$lm -ne 'FullLanguage'/.test(script)
  && new RegExp(`try \\{ \\[Console\\]::Error\\.WriteLine\\('powershell language mode .*\\} catch \\{ \\}; exit ${notify.TOAST_EXIT.LANGUAGE_MODE}`).test(script));
ok("script: a host without the WinRT types gets its own exit code",
  new RegExp(`WinRT notifications are unavailable: '\\+\\$_\\.Exception\\.Message\\); exit ${notify.TOAST_EXIT.NO_WINRT}`).test(script));
ok("script: any failure exits non-zero so the caller can fall back", /exit 1 \}?$/m.test(script));
ok("script: the fallback app id is a constant, never agent text",
  script.includes(`$fallbackId='${notify.FALLBACK_APP_ID}'`));

// ── toastResult (what the host logs) ──────────────────────────────────────────
ok("result: exit 0 with nothing on stderr is a clean success",
  notify.toastResult(0, "").ok && notify.toastResult(0, "").note === "" && notify.toastResult(0, "").reason === "");
ok("result: a toast that appeared but had something to say carries a note, not a fallback",
  notify.toastResult(0, "shown under the fallback app id\n").note === "shown under the fallback app id");
ok("result: stderr is the reason when the script failed",
  notify.toastResult(1, "boom\ntoast failed: nope\n").reason === "toast failed: nope");
ok("result: a lost stderr still names the failure by exit code",
  notify.toastResult(2, "").reason === "Windows has notifications switched off"
  && notify.toastResult(3, "").reason === "WinRT notifications are unavailable on this host"
  && notify.toastResult(4, "").reason === "PowerShell is not in full language mode");
ok("result: an unknown exit code is reported verbatim", notify.toastResult(99, "").reason === "powershell exited 99");
ok("result: a killed toast process is not silence", notify.toastResult(null, "").reason === "the toast script was killed");
ok("result: a failure never reports itself as ok", !notify.toastResult(1, "").ok);

// ── powershellPath ────────────────────────────────────────────────────────────
ok("powershell: the absolute System32 path is preferred over PATH",
  notify.powershellPath({ SystemRoot: "C:\\Windows" }, () => true)
    === "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");
ok("powershell: a trailing separator doesn't double up",
  notify.powershellPath({ SystemRoot: "C:\\Windows\\" }, () => true).includes("Windows\\System32"));
ok("powershell: falls back to PATH when that binary isn't there",
  notify.powershellPath({ SystemRoot: "C:\\Windows" }, () => false) === "powershell.exe");
ok("powershell: no SystemRoot (or a throwing probe) still yields something spawnable",
  notify.powershellPath({}, () => true) === "powershell.exe"
  && notify.powershellPath({ SystemRoot: "C:\\Windows" }, () => { throw new Error("x"); }) === "powershell.exe");

const cmd = notify.buildToastCommand(hostile);
ok("cmd: runs powershell directly (no cmd /c start, which is what forces a window)",
  cmd.file === "powershell.exe" && !JSON.stringify(cmd.args).includes("start"));
ok("cmd: no visible window", cmd.args.includes("-WindowStyle") && cmd.args.includes("Hidden") && cmd.options.windowsHide === true);
ok("cmd: no profile, non-interactive", cmd.args.includes("-NoProfile") && cmd.args.includes("-NonInteractive"));
ok("cmd: script rides as -EncodedCommand", cmd.args[cmd.args.indexOf("-EncodedCommand") + 1].length > 100);
ok("cmd: -EncodedCommand is UTF-16LE base64 of the script",
  Buffer.from(cmd.args[cmd.args.indexOf("-EncodedCommand") + 1], "base64").toString("utf16le") === cmd.script);
ok("cmd: stderr is captured for the fallback reason", cmd.options.stdio[2] === "pipe");
ok("cmd: an absolute powershell path is used when the caller resolved one",
  notify.buildToastCommand(hostile, { file: "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" }).file
    === "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");

console.log(`\n  notification unit tests — ${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
