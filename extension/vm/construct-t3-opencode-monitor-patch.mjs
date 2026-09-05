#!/usr/bin/env node
// construct-t3-opencode-monitor-patch.mjs — opt-in, fail-open runtime patch
// for the OpenCode adapter in the installed T3 Code npm bundle.
//
// The companion OpenCode background plugin deliberately exposes this stable
// lifecycle contract through ordinary message.part.updated events:
//
//   - completed `background` tool + metadata.wait === true:
//       metadata.id is armed until a later wake-up
//   - user text beginning `<background-task id="..." status="...">`:
//       that id reached a terminal state
//   - completed `background_kill` tool:
//       metadata.id was stopped
//
// T3's OpenCode adapter normally emits only item.* events for these parts.
// This patch adds task.started/task.completed events so the existing
// ThreadBackgroundLivenessService classifies the armed watcher as `monitoring`.
//
// Usage: construct-t3-opencode-monitor-patch.mjs apply|revert|status [--bundle <path>]
//
// A changed upstream bundle is deliberately a successful no-op: installing or
// updating T3 must still succeed, with only the monitoring pill degraded.

import {
  chmodSync,
  existsSync,
  readFileSync,
  renameSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import { execFileSync } from "node:child_process";

const VERSION = "v1";
const MARKER = "/*__CONSTRUCT_T3_OPENCODE_MONITOR " + VERSION + "*/";

const args = process.argv.slice(2);
const mode = args[0];
let bundle = "/usr/lib/node_modules/t3/dist/bin.mjs";
const bundleIndex = args.indexOf("--bundle");
if (bundleIndex !== -1 && args[bundleIndex + 1]) bundle = args[bundleIndex + 1];

if (!["apply", "revert", "status"].includes(mode)) {
  console.error("usage: construct-t3-opencode-monitor-patch.mjs apply|revert|status [--bundle <path>]");
  process.exit(1);
}

if (!existsSync(bundle)) {
  if (mode === "status") {
    console.log(JSON.stringify({ patched: false, compatible: false, version: null, bundle }));
    process.exit(0);
  }
  console.error("t3-opencode-monitor: bundle not found: " + bundle);
  process.exit(1);
}

const ANCHOR = String.raw`					const part = event.properties.part;
`;

const BLOCK = String.raw`					${MARKER}
					if (part.type === "tool" && part.tool === "background" && part.state.status === "completed" && part.state.metadata?.wait === true) {
						const __constructTaskId = typeof part.state.metadata.id === "string" ? part.state.metadata.id.trim() : "";
						if (__constructTaskId) yield* emit({
							...yield* buildEventBase({
								threadId: context.session.threadId,
								turnId,
								raw: event
							}),
							type: "task.started",
							payload: {
								taskId: __constructTaskId,
								taskType: "local_bash",
								...typeof part.state.title === "string" && part.state.title.trim() ? { description: part.state.title } : {}
							}
						});
					}
					const __constructWake = part.type === "text" && messageRoleForPart(context, part) === "user"
						? /^<background-task id="([^"]+)" status="([^"]+)">/.exec(part.text)
						: null;
					if (__constructWake) {
						const __constructWakeStatus = __constructWake[2].toLowerCase();
						yield* emit({
							...yield* buildEventBase({
								threadId: context.session.threadId,
								turnId,
								raw: event
							}),
							type: "task.completed",
							payload: {
								taskId: __constructWake[1],
								status: __constructWakeStatus === "failed" || __constructWakeStatus === "error" ? "failed" : __constructWakeStatus === "killed" || __constructWakeStatus === "stopped" || __constructWakeStatus === "cancelled" ? "stopped" : "completed"
							}
						});
					}
					if (part.type === "tool" && part.tool === "background_kill" && part.state.status === "completed") {
						const __constructKilledTaskId = typeof part.state.metadata?.id === "string" ? part.state.metadata.id.trim() : "";
						if (__constructKilledTaskId) yield* emit({
							...yield* buildEventBase({
								threadId: context.session.threadId,
								turnId,
								raw: event
							}),
							type: "task.completed",
							payload: { taskId: __constructKilledTaskId, status: "stopped" }
						});
					}
`;

const INJECTION = ANCHOR + BLOCK;

function countOccurrences(haystack, needle) {
  let count = 0;
  let offset = 0;
  while ((offset = haystack.indexOf(needle, offset)) !== -1) {
    count += 1;
    offset += needle.length;
  }
  return count;
}

const source = readFileSync(bundle, "utf8");
const patched = source.includes(MARKER);
const anchorCount = countOccurrences(source, ANCHOR);

if (mode === "status") {
  console.log(JSON.stringify({ patched, compatible: patched || anchorCount === 1, version: patched ? VERSION : null, bundle }));
  process.exit(0);
}

function replaceAtomically(next) {
  const temporary = bundle + ".construct-opencode-monitor-tmp.mjs";
  const bundleMode = statSync(bundle).mode & 0o7777;
  writeFileSync(temporary, next);
  chmodSync(temporary, bundleMode);
  try {
    execFileSync(process.execPath, ["--check", temporary], { stdio: "pipe" });
  } catch (error) {
    try { unlinkSync(temporary); } catch {}
    console.warn(
      "t3-opencode-monitor: patched bundle failed node --check; leaving T3 unchanged: " +
        (error.stderr ? error.stderr.toString().slice(0, 300) : error.message),
    );
    return false;
  }
  renameSync(temporary, bundle);
  return true;
}

if (mode === "revert") {
  if (!patched) {
    console.log("t3-opencode-monitor: bundle is stock; nothing to revert");
    process.exit(0);
  }
  const injectionCount = countOccurrences(source, BLOCK);
  if (injectionCount !== 1) {
    console.warn(
      "t3-opencode-monitor: patched block found " + injectionCount +
        " times (expected 1); leaving this T3 bundle unchanged",
    );
    process.exit(0);
  }
  if (replaceAtomically(source.replace(BLOCK, ""))) {
    console.log("t3-opencode-monitor: reverted " + bundle);
  }
  process.exit(0);
}

if (patched) {
  console.log("t3-opencode-monitor: already patched (" + VERSION + ")");
  process.exit(0);
}

if (anchorCount !== 1) {
  console.warn(
    "t3-opencode-monitor: OpenCode message.part.updated anchor found " + anchorCount +
      " times (expected 1); leaving this T3 bundle unchanged",
  );
  process.exit(0);
}

const next = source.replace(ANCHOR, INJECTION);
if (replaceAtomically(next)) {
  console.log("t3-opencode-monitor: patched " + bundle + " (" + VERSION + ")");
}
