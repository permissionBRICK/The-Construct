#!/usr/bin/env node
// construct-t3park-patch.mjs — opt-in runtime patch for the installed T3 Code
// server (`t3` npm package): park a thread when a Claude turn dies on a
// usage/session limit and auto-resume it once the limit window resets.
//
// T3 Code upstream won't ship this feature, so Construct patches the installed
// dist bundle the same way it patches the Claude Code VS Code extension
// (reversible, verified, stock-by-default). The patch:
//
//   1. stashes the SDK's structured `rate_limit_event` per session
//      (SDKRateLimitInfo: status allowed|allowed_warning|rejected + resetsAt),
//   2. when a turn RESULT represents an account-limit rejection (including
//      Claude's `subtype: success` + `is_error: true` wrapper), schedules
//      an automatic `thread.turn.start` continuation via the local
//      orchestration HTTP API at resetsAt (+60s margin), persisted to
//      <T3CODE_HOME>/userdata/t3park-pending.json (defaulting to
//      ~/.t3/userdata) so it survives service restarts, and
//   3. projects the park through T3's native `thread.snooze` command after
//      the failed turn settles, making it visible in existing clients.
//
// Usage: construct-t3park-patch.mjs apply|revert|status [--bundle <path>]
//
// apply   verifies both anchor sites exist EXACTLY once in the bundle before
//         touching anything; a changed upstream bundle -> exit 2, t3 runs
//         stock (fail-open — the caller decides whether that's fatal).
//         Also mints a long-lived t3 API session token (the resume dispatch
//         authenticates like any other client) unless one is already stored.
// revert  restores the pristine bundle from the .t3park-orig backup.
// status  prints JSON: { patched, version, bundle }.
//
// Exit codes: 0 ok/no-op, 1 hard error, 2 anchors not found (bundle changed).

import { readFileSync, writeFileSync, copyFileSync, existsSync, renameSync, chmodSync, statSync } from "node:fs";
import { execFileSync } from "node:child_process";

const VERSION = "v5";
const MARKER = "/*__T3PARK " + VERSION + "*/";
const MARKER_RE = /\/\*__T3PARK (v\d+)\*\//;
const TOKEN_FILE = "/etc/construct/t3park-token";

const args = process.argv.slice(2);
const mode = args[0];
let bundle = "/usr/lib/node_modules/t3/dist/bin.mjs";
const bi = args.indexOf("--bundle");
if (bi !== -1 && args[bi + 1]) bundle = args[bi + 1];
const backup = bundle + ".t3park-orig";

if (!["apply", "revert", "status", "mint-token"].includes(mode)) {
  console.error("usage: construct-t3park-patch.mjs apply|revert|status|mint-token [--bundle <path>]");
  process.exit(1);
}

// ---------------------------------------------------------------------------
// Anchors — exact strings from the t3 dist bundle (tab-indented, un-minified;
// verified against t3@0.0.33). Each must occur exactly once or we refuse.
// ---------------------------------------------------------------------------

// Inside handleSdkTelemetryMessage: the rate_limit_event branch. We prepend a
// stash call so the session context remembers the latest SDKRateLimitInfo.
const ANCHOR_RATELIMIT = '\t\tif (message.type === "rate_limit_event") {\n';
const PATCH_RATELIMIT = ANCHOR_RATELIMIT +
  "\t\t\tglobalThis.__t3park && globalThis.__t3park.noteRateLimit(context, message);\n";

// Inside handleResultMessage: status + errorMessage computation. Claude Code
// 2.1.232 can report an account-limit response as `subtype: success` while
// also setting `is_error: true` and `api_error_status: 429`; T3's stock status
// is therefore `completed`. The hook may override only that classified limit
// result to `failed`, schedule the park, and add the auto-resume banner.
const ANCHOR_RESULT =
  "\t\tconst status = turnStatusFromResult(message);\n" +
  "\t\tconst errorMessage = resultUserFacingError(message);\n";
const PATCH_RESULT =
  "\t\tconst __t3parkResult = globalThis.__t3park ? globalThis.__t3park.onTurnResult(context, turnStatusFromResult(message), resultUserFacingError(message), message) : null;\n" +
  "\t\tconst status = __t3parkResult ? __t3parkResult.status : turnStatusFromResult(message);\n" +
  "\t\tconst errorMessage = __t3parkResult ? __t3parkResult.errorMessage : resultUserFacingError(message);\n";

// ---------------------------------------------------------------------------
// Runtime footer, appended to the bundle. Top-level ESM, so imports are legal;
// everything is namespaced under globalThis.__t3park and __t3park_* aliases.
// No backticks / template literals in here — it is embedded in this string.
// ---------------------------------------------------------------------------

const FOOTER = "\n" + MARKER + "\n" + String.raw`import { readFileSync as __t3park_read, writeFileSync as __t3park_write, mkdirSync as __t3park_mkdir } from "node:fs";
import { homedir as __t3park_home } from "node:os";
import { resolve as __t3park_resolve } from "node:path";
(() => {
  if (globalThis.__t3park) return;
  const envMs = (name, fallback) => {
    const value = Number(process.env[name]);
    return Number.isFinite(value) && value >= 0 ? value : fallback;
  };
  const RESUME_MARGIN_MS = envMs("T3PARK_RESUME_MARGIN_MS", 60000);
  const MIN_DELAY_MS = envMs("T3PARK_MIN_DELAY_MS", 15000);
  const RETRY_MS = envMs("T3PARK_RETRY_MS", 300000);
  const VISIBILITY_RETRY_MS = envMs("T3PARK_VISIBILITY_RETRY_MS", 250);
  const VISIBILITY_SETTLE_MS = envMs("T3PARK_VISIBILITY_SETTLE_MS", 2000);
  const TEST_DELAY_MS = process.env.T3PARK_TEST_DELAY_MS === undefined
    ? null : envMs("T3PARK_TEST_DELAY_MS", null);
  const MAX_ATTEMPTS = 12;
  const VISIBILITY_MAX_ATTEMPTS = 40;
  const MAX_PARK_MS = 8 * 24 * 3600 * 1000; // sanity clamp, mirrors omniloop
  const LIMIT_TEXT_RE = /(you'?ve hit your .{0,60}limit|usage limit reached|claude usage limit)/i;
  const explicitHome = String(process.env.T3CODE_HOME || "").trim();
  const S = {
    pendingDir: explicitHome ? __t3park_resolve(explicitHome) + "/userdata" : __t3park_home() + "/.t3/userdata",
    timers: new Map(),
    visibilityTimers: new Map(),
    rateLimits: new WeakMap(),
  };
  S.pendingPath = S.pendingDir + "/t3park-pending.json";
  const log = (...a) => console.log("[t3park]", ...a);
  const debug = (...a) => { if (process.env.T3PARK_DEBUG === "true") log(...a); };
  const loadPending = () => {
    try { return JSON.parse(__t3park_read(S.pendingPath, "utf8")); } catch { return {}; }
  };
  const savePending = (p) => {
    try {
      __t3park_mkdir(S.pendingDir, { recursive: true });
      __t3park_write(S.pendingPath, JSON.stringify(p, null, 1));
    } catch (e) { log("could not persist pending parks:", e.message); }
  };
  const dropPending = (threadId) => {
    const timer = S.timers.get(threadId);
    if (timer) clearTimeout(timer);
    const visibilityTimer = S.visibilityTimers.get(threadId);
    if (visibilityTimer) clearTimeout(visibilityTimer);
    const p = loadPending();
    if (p[threadId]) { delete p[threadId]; savePending(p); }
    S.timers.delete(threadId);
    S.visibilityTimers.delete(threadId);
  };
  const apiBase = () => "http://127.0.0.1:" + (process.env.T3CODE_PORT || "5177");
  const apiHeaders = () => {
    const tokenFile = process.env.T3PARK_TOKEN_FILE || "/etc/construct/t3park-token";
    const token = __t3park_read(tokenFile, "utf8").trim();
    return { "Authorization": "Bearer " + token, "Content-Type": "application/json" };
  };
  const readThread = async (headers, threadId) => {
    const shellRes = await fetch(apiBase() + "/api/orchestration/shell", { headers });
    if (!shellRes.ok) throw new Error("shell -> " + shellRes.status);
    const shell = await shellRes.json();
    return (shell.threads || []).find((t) => t.id === threadId);
  };
  const hasManualContinuation = (thread, parkedAt) => {
    if (!(parkedAt > 0) || !thread || !thread.latestUserMessageAt) return false;
    const latestUserAt = Date.parse(thread.latestUserMessageAt);
    return Number.isFinite(latestUserAt) && latestUserAt > parkedAt;
  };
  const retryVisibility = (threadId, snoozedUntil, parkedAt, attempt, reason) => {
    if (attempt >= VISIBILITY_MAX_ATTEMPTS) {
      log("could not make parked thread", threadId, "visible after", VISIBILITY_MAX_ATTEMPTS,
        "attempts; auto-resume remains scheduled:", reason);
      S.visibilityTimers.delete(threadId);
      return;
    }
    const prior = S.visibilityTimers.get(threadId);
    if (prior) clearTimeout(prior);
    const timer = setTimeout(() => {
      S.visibilityTimers.delete(threadId);
      showPark(threadId, snoozedUntil, parkedAt, attempt + 1);
    }, VISIBILITY_RETRY_MS);
    if (typeof timer.unref === "function") timer.unref();
    S.visibilityTimers.set(threadId, timer);
  };
  const showPark = async (threadId, snoozedUntil, parkedAt, attempt) => {
    try {
      const pending = loadPending();
      if (!pending[threadId]) { S.visibilityTimers.delete(threadId); return; }
      const headers = apiHeaders();
      const thread = await readThread(headers, threadId);
      if (!thread) { log("thread", threadId, "no longer exists; dropping park"); dropPending(threadId); return; }
      if (hasManualContinuation(thread, parkedAt)) {
        log("thread", threadId, "was continued manually after parking; dropping auto-resume");
        dropPending(threadId);
        return;
      }
      const lt = thread.latestTurn;
      const sessionStatus = thread.session && thread.session.status;
      const turnSettled = !!(lt && lt.state !== "running" && lt.state !== "pending" && lt.completedAt);
      const sessionSettled = sessionStatus !== "starting" && sessionStatus !== "running";
      const latestProjectionAt = Math.max(
        Date.parse(lt && lt.completedAt) || 0,
        Date.parse(thread.session && thread.session.updatedAt) || 0,
      );
      const projectionQuiet = latestProjectionAt > 0 && Date.now() - latestProjectionAt >= VISIBILITY_SETTLE_MS;
      if (!turnSettled || !sessionSettled || !projectionQuiet) {
        retryVisibility(threadId, snoozedUntil, parkedAt, attempt,
          "failed turn has not reached its quiet window yet");
        return;
      }
      let requestedUntil = snoozedUntil;
      const existingUntil = Date.parse(thread.snoozedUntil) || 0;
      const existingSnoozedAt = Date.parse(thread.snoozedAt) || 0;
      // T3 preserves snoozedAt when the same wake time is sent twice. Nudge a
      // stale restored snooze so its timestamp moves after the final failure.
      if (existingUntil === requestedUntil && existingSnoozedAt <= latestProjectionAt) requestedUntil += 1;
      const command = {
        type: "thread.snooze",
        commandId: globalThis.crypto.randomUUID(),
        threadId: threadId,
        snoozedUntil: new Date(requestedUntil).toISOString(),
      };
      const res = await fetch(apiBase() + "/api/orchestration/dispatch", {
        method: "POST", headers, body: JSON.stringify(command),
      });
      if (!res.ok) throw new Error("snooze dispatch -> " + res.status + ": " + (await res.text()).slice(0, 200));
      S.visibilityTimers.delete(threadId);
      log("made parked thread", threadId, "visible as snoozed until", command.snoozedUntil);
    } catch (e) {
      retryVisibility(threadId, snoozedUntil, parkedAt, attempt, e.message);
    }
  };
  const resume = async (threadId, attempt) => {
    try {
      const headers = apiHeaders();
      const pending = loadPending();
      const parkedAt = Number(pending[threadId] && pending[threadId].parkedAt) || 0;
      const thread = await readThread(headers, threadId);
      if (!thread) { log("thread", threadId, "no longer exists; dropping park"); dropPending(threadId); return; }
      if (hasManualContinuation(thread, parkedAt)) {
        log("thread", threadId, "was continued manually after parking; dropping auto-resume");
        dropPending(threadId);
        return;
      }
      const lt = thread.latestTurn;
      if (lt && (lt.state === "running" || lt.state === "pending")) {
        log("thread", threadId, "is already active again; dropping park");
        dropPending(threadId);
        return;
      }
      const uuid = () => globalThis.crypto.randomUUID();
      const cmd = {
        type: "thread.turn.start",
        commandId: uuid(),
        threadId: threadId,
        message: {
          messageId: uuid(),
          role: "user",
          text: "[construct auto-resume] This session was parked after hitting a Claude usage limit; the limit window has reset. Continue the task from where you left off — re-check any partial work from the interrupted turn before proceeding.",
          attachments: [],
        },
        createdAt: new Date().toISOString(),
        runtimeMode: thread.runtimeMode || "approval-required",
        interactionMode: thread.interactionMode || "default",
      };
      if (thread.modelSelection) cmd.modelSelection = thread.modelSelection;
      const res = await fetch(apiBase() + "/api/orchestration/dispatch", {
        method: "POST", headers, body: JSON.stringify(cmd),
      });
      if (!res.ok) throw new Error("dispatch -> " + res.status + ": " + (await res.text()).slice(0, 200));
      log("resumed thread", threadId, "after usage-limit park; dispatch status", res.status);
      dropPending(threadId);
    } catch (e) {
      if (attempt < MAX_ATTEMPTS) {
        log("resume of", threadId, "failed (" + e.message + "); retrying in", RETRY_MS / 60000, "min");
        S.timers.set(threadId, setTimeout(() => resume(threadId, attempt + 1), RETRY_MS));
      } else {
        log("resume of", threadId, "failed after", MAX_ATTEMPTS, "attempts; giving up:", e.message);
        dropPending(threadId);
      }
    }
  };
  const schedule = (threadId, resumeAt, reason, persist, parkedAt) => {
    const prior = S.timers.get(threadId);
    if (prior) clearTimeout(prior);
    const parkedTimestamp = Number(parkedAt) || Date.now();
    if (persist) {
      const p = loadPending();
      p[threadId] = {
        resumeAt: resumeAt,
        parkedAt: parkedTimestamp,
        reason: String(reason || "").slice(0, 300),
      };
      savePending(p);
    }
    const naturalDelay = Math.min(Math.max(resumeAt + RESUME_MARGIN_MS - Date.now(), MIN_DELAY_MS), MAX_PARK_MS);
    const delay = TEST_DELAY_MS === null ? naturalDelay : TEST_DELAY_MS;
    const t = setTimeout(() => resume(threadId, 0), delay);
    if (typeof t.unref === "function") t.unref();
    S.timers.set(threadId, t);
    const visibleUntil = Math.max(resumeAt + RESUME_MARGIN_MS, Date.now() + delay + 1000);
    showPark(threadId, visibleUntil, parkedTimestamp, 0);
    log("parked thread", threadId, "until", new Date(Date.now() + delay).toISOString(),
      "(limit reset", new Date(resumeAt).toISOString() + ", source", (persist ? "turn" : "restore") + ")");
  };
  globalThis.__t3park = {
    noteRateLimit(context, message) {
      try {
        if (message && message.rate_limit_info) {
          S.rateLimits.set(context, { info: message.rate_limit_info, ts: Date.now() });
          const info = message.rate_limit_info;
          const threadId = context && context.session && context.session.threadId;
          if (info.status === "rejected") log("rate-limit telemetry", threadId || "unknown-thread",
            "status", info.status, "type", info.rateLimitType || "unknown", "resetsAt", info.resetsAt || "unknown");
          else debug("rate-limit telemetry", threadId || "unknown-thread", "status", info.status);
        }
      } catch (e) { log("noteRateLimit error:", e.message); }
    },
    onTurnResult(context, status, errorMessage, result) {
      try {
        const unchanged = { status: status, errorMessage: errorMessage };
        const threadId = context && context.session && context.session.threadId;
        if (!threadId) return unchanged;
        const stash = S.rateLimits.get(context);
        const rejected = !!(stash && stash.info && stash.info.status === "rejected" && Date.now() - stash.ts < 30 * 60000);
        const resultText = result && typeof result.result === "string" ? result.result : "";
        const text = String(errorMessage || resultText || "");
        const status429 = !!(result && result.api_error_status === 429);
        const isError = !!(result && result.is_error === true);
        const textMatch = LIMIT_TEXT_RE.test(text);
        const failedish = status === "failed" || isError || status429;
        if (!failedish || (!rejected && !status429 && !textMatch)) {
          debug("turn result not parked", threadId, "stockStatus", status, "isError", isError,
            "status429", status429, "rejected", rejected, "textMatch", textMatch);
          return unchanged;
        }
        let resetsAt = rejected && stash.info.resetsAt ? Number(stash.info.resetsAt) : null;
        if (resetsAt && resetsAt < 1e12) resetsAt = resetsAt * 1000; // epoch s -> ms
        if (!resetsAt || resetsAt <= Date.now() || resetsAt - Date.now() > MAX_PARK_MS) {
          resetsAt = Date.now() + Number(process.env.T3PARK_FALLBACK_MIN || 60) * 60000;
        }
        log("classified usage-limit result", threadId, "stockStatus", status, "isError", isError,
          "status429", status429, "rejected", rejected, "textMatch", textMatch);
        schedule(threadId, resetsAt, text, true, Date.now());
        return {
          status: "failed",
          errorMessage: (text || "Claude usage limit reached.") +
            " — Construct parked this thread and will auto-resume it around " +
            new Date(resetsAt + RESUME_MARGIN_MS).toISOString().replace(/\.\d+Z$/, "Z") + ".",
        };
      } catch (e) {
        log("onTurnResult error:", e.message);
        return { status: status, errorMessage: errorMessage };
      }
    },
  };
  try {
    const p = loadPending();
    const ids = Object.keys(p);
    if (ids.length) log("restoring", ids.length, "pending park(s) after restart");
    for (const tid of ids) schedule(tid, Number(p[tid].resumeAt) || Date.now(), p[tid].reason, false,
      Number(p[tid].parkedAt) || 0);
  } catch (e) { log("pending-park restore failed:", e.message); }
})();
`;

// ---------------------------------------------------------------------------

function fail(msg, code = 1) { console.error("t3park: " + msg); process.exit(code); }

function currentVersion(src) {
  const m = MARKER_RE.exec(src);
  return m ? m[1] : null;
}

function countOccurrences(haystack, needle) {
  let n = 0, i = 0;
  while ((i = haystack.indexOf(needle, i)) !== -1) { n++; i += needle.length; }
  return n;
}

function syntaxCheck(path) {
  execFileSync(process.execPath, ["--check", path], { stdio: "pipe" });
}

function ensureToken() {
  if (process.env.T3PARK_SKIP_TOKEN === "true") return;
  if (existsSync(TOKEN_FILE) && readFileSync(TOKEN_FILE, "utf8").trim().length > 0) return;
  try {
    const out = execFileSync("t3", ["auth", "session", "issue", "--ttl", "365d", "--token-only", "--label", "construct-t3park", "--log-level", "none"], { stdio: ["ignore", "pipe", "pipe"] }).toString().trim();
    if (!out) throw new Error("empty token output");
    writeFileSync(TOKEN_FILE, out + "\n");
    chmodSync(TOKEN_FILE, 0o600);
    console.log("t3park: minted API session token -> " + TOKEN_FILE);
  } catch (e) {
    console.error("t3park: WARNING: could not mint API token (" + (e.message || e) + "); auto-resume will retry reading " + TOKEN_FILE + " at fire time");
  }
}

if (mode === "mint-token") { ensureToken(); process.exit(existsSync(TOKEN_FILE) ? 0 : 1); }

if (!existsSync(bundle)) fail("bundle not found: " + bundle + (mode === "revert" ? " (nothing to revert)" : ""), mode === "revert" ? 0 : 1);
let src = readFileSync(bundle, "utf8");
const version = currentVersion(src);

if (mode === "status") {
  console.log(JSON.stringify({ patched: version !== null, version, bundle, backup: existsSync(backup) }));
  process.exit(0);
}

if (mode === "revert") {
  if (version === null) { console.log("t3park: bundle is stock; nothing to revert"); process.exit(0); }
  if (!existsSync(backup)) fail("bundle is patched (" + version + ") but backup " + backup + " is missing — reinstall t3 (npm install -g t3) to restore stock");
  const orig = readFileSync(backup, "utf8");
  if (MARKER_RE.test(orig)) fail("backup " + backup + " is itself patched; refusing to restore it");
  const revertMode = statSync(bundle).mode & 0o7777; // npm's bin.mjs is executable (shebang-run); keep it so
  writeFileSync(bundle + ".t3park-tmp.mjs", orig);
  chmodSync(bundle + ".t3park-tmp.mjs", revertMode);
  renameSync(bundle + ".t3park-tmp.mjs", bundle);
  console.log("t3park: reverted " + bundle + " to stock");
  process.exit(0);
}

// mode === "apply"
if (version === VERSION) { console.log("t3park: already patched (" + VERSION + ")"); ensureToken(); process.exit(0); }
let srcIsStockBundle = true; // src is the on-disk bundle and it is stock
if (version !== null) {
  // Older patch version: take the pristine source from the backup instead —
  // and do NOT refresh the backup below (it is already the pristine copy).
  if (!existsSync(backup)) fail("bundle carries old patch " + version + " and backup is missing — reinstall t3 first");
  const orig = readFileSync(backup, "utf8");
  if (MARKER_RE.test(orig)) fail("backup is itself patched; reinstall t3 first");
  src = orig;
  srcIsStockBundle = false;
}

for (const [name, anchor] of [["rate_limit_event branch", ANCHOR_RATELIMIT], ["result errorMessage line", ANCHOR_RESULT]]) {
  const n = countOccurrences(src, anchor);
  if (n !== 1) fail("anchor '" + name + "' found " + n + " times (expected 1) — the t3 bundle changed upstream; patch NOT applied, t3 runs stock. Update construct-t3park-patch.mjs for this t3 version.", 2);
}

let patched = src.replace(ANCHOR_RATELIMIT, PATCH_RATELIMIT).replace(ANCHOR_RESULT, PATCH_RESULT);
// Keep the sourceMappingURL comment last when present (cosmetic only).
const smu = patched.lastIndexOf("\n//# sourceMappingURL=");
if (smu !== -1 && patched.indexOf("\n", smu + 1) === patched.length - 1) {
  patched = patched.slice(0, smu) + FOOTER + patched.slice(smu);
} else {
  patched = patched + FOOTER;
}

if (srcIsStockBundle) copyFileSync(bundle, backup); // refresh backup only from a stock bundle
const bundleMode = statSync(bundle).mode & 0o7777; // preserve the exec bit — bin.mjs IS the t3 CLI (shebang-run via symlink)
writeFileSync(bundle + ".t3park-tmp.mjs", patched);
chmodSync(bundle + ".t3park-tmp.mjs", bundleMode);
try {
  syntaxCheck(bundle + ".t3park-tmp.mjs");
} catch (e) {
  fail("patched bundle failed node --check; leaving stock in place: " + (e.stderr ? e.stderr.toString().slice(0, 300) : e.message));
}
renameSync(bundle + ".t3park-tmp.mjs", bundle);
ensureToken();
console.log("t3park: patched " + bundle + " (" + VERSION + "); backup at " + backup);
