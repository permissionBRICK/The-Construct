#!/usr/bin/env node
// Regression test for the T3 Claude usage-limit runtime patch.
// Run: node test/t3park-patch.test.mjs

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const patcher = join(root, "extension/vm/construct-t3park-patch.mjs");
const tmp = mkdtempSync(join(tmpdir(), "construct-t3park-test-"));
const bundle = join(tmp, "bin.mjs");
const tokenPath = join(tmp, "token");
const original = `
function turnStatusFromResult(message) {
  return message.subtype === "success" ? "completed" : "failed";
}
function resultUserFacingError(message) {
  if (message.subtype === "success" || !Array.isArray(message.errors)) return;
  return message.errors[0];
}
export function handleSdkTelemetryMessage(context, message) {
\t\tif (message.type === "rate_limit_event") {
    return "telemetry";
  }
}
export function handleResultMessage(context, message) {
  if (message.type !== "result") return;
\t\tconst status = turnStatusFromResult(message);
\t\tconst errorMessage = resultUserFacingError(message);
  return { status, errorMessage };
}
`;

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  console.log("  PASS  " + name);
}

function runPatcher(mode) {
  return execFileSync(process.execPath, [patcher, mode, "--bundle", bundle], {
    encoding: "utf8",
    env: { ...process.env, T3PARK_SKIP_TOKEN: "true" },
  });
}

async function waitFor(predicate, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolveWait) => setTimeout(resolveWait, 20));
  }
  throw new Error("timed out waiting for condition");
}

writeFileSync(tokenPath, "fixture-token\n");
writeFileSync(bundle, original);

const dispatches = [];
const authorizations = [];
const threadId = "11111111-1111-4111-8111-111111111111";
const manualThreadId = "33333333-3333-4333-8333-333333333333";
const threads = new Map([[threadId, {
  id: threadId,
  latestUserMessageAt: new Date(Date.now() - 60000).toISOString(),
  latestTurn: { state: "running", completedAt: null },
  session: { status: "running", updatedAt: new Date().toISOString() },
  runtimeMode: "full-access",
  interactionMode: "default",
  modelSelection: { instanceId: "claudeAgent", model: "claude-sonnet-5" },
}]]);
const fakeApi = createServer(async (req, res) => {
  authorizations.push(req.headers.authorization);
  res.setHeader("Content-Type", "application/json");
  if (req.url === "/api/orchestration/shell") {
    res.end(JSON.stringify({ threads: [...threads.values()] }));
    return;
  }
  if (req.url === "/api/orchestration/dispatch" && req.method === "POST") {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const dispatch = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    dispatches.push(dispatch);
    if (dispatch.type === "thread.snooze") {
      const thread = threads.get(dispatch.threadId);
      if (thread) {
        thread.snoozedUntil = dispatch.snoozedUntil;
        thread.snoozedAt = new Date().toISOString();
      }
    }
    res.end(JSON.stringify({ sequence: 42 }));
    return;
  }
  res.statusCode = 404;
  res.end("{}");
});
await new Promise((resolveListen) => fakeApi.listen(0, "127.0.0.1", resolveListen));
const port = fakeApi.address().port;

try {
  runPatcher("apply");
  const status = JSON.parse(runPatcher("status"));
  ok("apply: installs v6 with a pristine backup", () => {
    assert.equal(status.patched, true);
    assert.equal(status.version, "v6");
    assert.equal(status.backup, true);
  });
  ok("apply: remains idempotent", () => {
    assert.match(runPatcher("apply"), /already patched \(v6\)/);
    assert.equal((readFileSync(bundle, "utf8").match(/\/\*__T3PARK v6\*\//g) || []).length, 1);
  });

  process.env.T3CODE_HOME = join(tmp, "isolated-t3");
  process.env.T3CODE_PORT = String(port);
  process.env.T3PARK_TOKEN_FILE = tokenPath;
  process.env.T3PARK_TEST_DELAY_MS = "180";
  process.env.T3PARK_RESUME_MARGIN_MS = "0";
  process.env.T3PARK_MIN_DELAY_MS = "0";
  process.env.T3PARK_RETRY_MS = "50";
  process.env.T3PARK_VISIBILITY_RETRY_MS = "10";
  process.env.T3PARK_VISIBILITY_SETTLE_MS = "0";
  process.env.T3PARK_DEBUG = "true";

  const fixture = await import(pathToFileURL(bundle).href + "?v=6");
  const ordinary = fixture.handleResultMessage({ session: { threadId: "22222222-2222-4222-8222-222222222222" } }, {
    type: "result",
    subtype: "success",
    is_error: false,
    result: "ordinary success",
  });
  ok("classification: ordinary success remains completed", () => {
    assert.deepEqual(ordinary, { status: "completed", errorMessage: undefined });
  });
  const context = { session: { threadId } };
  const resetsAt = Math.floor((Date.now() + 3600000) / 1000);
  fixture.handleSdkTelemetryMessage(context, {
    type: "rate_limit_event",
    rate_limit_info: { status: "rejected", resetsAt, rateLimitType: "five_hour" },
  });
  const result = fixture.handleResultMessage(context, {
    type: "result",
    subtype: "success",
    is_error: true,
    api_error_status: 429,
    result: "You've hit your session limit · resets 4pm (UTC)",
  });

  ok("classification: success-wrapped 429 becomes failed", () => {
    assert.equal(result.status, "failed");
    assert.match(result.errorMessage, /You've hit your session limit/);
    assert.match(result.errorMessage, /Construct parked this thread/);
  });
  const pendingPath = join(process.env.T3CODE_HOME, "userdata/t3park-pending.json");
  ok("park: persists under T3CODE_HOME with the rejected thread and reset epoch", () => {
    const pending = JSON.parse(readFileSync(pendingPath, "utf8"));
    assert.equal(pending[threadId].resumeAt, resetsAt * 1000);
    assert.equal(typeof pending[threadId].parkedAt, "number");
    assert.match(pending[threadId].reason, /session limit/);
  });

  await new Promise((resolveWait) => setTimeout(resolveWait, 35));
  ok("visibility: waits for the failed turn to settle before snoozing", () => {
    assert.equal(dispatches.some((command) => command.type === "thread.snooze"), false);
  });
  const settledAt = new Date().toISOString();
  Object.assign(threads.get(threadId), {
    latestTurn: { state: "failed", completedAt: settledAt },
    session: { status: "error", updatedAt: settledAt },
  });
  await waitFor(() => dispatches.some((command) => command.type === "thread.snooze"));
  ok("visibility: publishes the park through T3's native snooze command", () => {
    const snooze = dispatches.find((command) => command.type === "thread.snooze");
    assert.equal(snooze.threadId, threadId);
    assert.equal(snooze.snoozedUntil, new Date(resetsAt * 1000).toISOString());
    assert.equal(authorizations.every((value) => value === "Bearer fixture-token"), true);
  });

  await waitFor(() => dispatches.some((command) => command.type === "thread.turn.start"));
  await waitFor(() => readFileSync(pendingPath, "utf8").trim() === "{}");
  ok("resume: dispatches an authenticated continuation", () => {
    const dispatch = dispatches.find((command) => command.type === "thread.turn.start");
    assert.equal(dispatch.type, "thread.turn.start");
    assert.equal(dispatch.threadId, threadId);
    assert.equal(dispatch.runtimeMode, "full-access");
    assert.deepEqual(dispatch.modelSelection, { instanceId: "claudeAgent", model: "claude-sonnet-5" });
    assert.match(dispatch.message.text, /construct auto-resume/);
  });
  ok("resume: removes the persisted park after accepted dispatch", () => {
    assert.deepEqual(JSON.parse(readFileSync(pendingPath, "utf8")), {});
  });

  threads.set(manualThreadId, {
    id: manualThreadId,
    latestUserMessageAt: new Date(Date.now() + 60000).toISOString(),
    latestTurn: { state: "completed", completedAt: new Date().toISOString() },
    session: { status: "ready", updatedAt: new Date().toISOString() },
    runtimeMode: "approval-required",
    interactionMode: "default",
  });
  const manualContext = { session: { threadId: manualThreadId } };
  fixture.handleSdkTelemetryMessage(manualContext, {
    type: "rate_limit_event",
    rate_limit_info: { status: "rejected", resetsAt, rateLimitType: "five_hour" },
  });
  fixture.handleResultMessage(manualContext, {
    type: "result",
    subtype: "success",
    is_error: true,
    api_error_status: 429,
    result: "You've hit your session limit",
  });
  await waitFor(() => !JSON.parse(readFileSync(pendingPath, "utf8"))[manualThreadId]);
  await new Promise((resolveWait) => setTimeout(resolveWait, 220));
  ok("manual continuation: cancels both snooze visibility and auto-resume", () => {
    assert.equal(dispatches.some((command) => command.threadId === manualThreadId), false);
  });

  runPatcher("revert");
  ok("revert: restores the fixture byte-for-byte", () => {
    assert.equal(readFileSync(bundle, "utf8"), original);
  });
} finally {
  await new Promise((resolveClose) => fakeApi.close(resolveClose));
  rmSync(tmp, { recursive: true, force: true });
}

console.log(`\n${passed} passed, 0 failed`);
