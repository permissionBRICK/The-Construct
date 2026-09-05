// Focused fixture tests for the fail-open T3/OpenCode monitoring patcher.
// Run: node t3-opencode-monitor-patch.test.mjs

import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { spawnSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
const patcher = join(here, "..", "vm", "construct-t3-opencode-monitor-patch.mjs");
const temporary = mkdtempSync(join(tmpdir(), "construct-t3-opencode-monitor-"));

const fixture = `
const emitted = [];
function* buildEventBase(input) { return input; }
function* emit(event) { emitted.push(event); }
function messageRoleForPart(context, part) { return context.messageRoleById.get(part.messageID); }
function* handleSubscribedEvent(context, event) {
\tconst turnId = "turn-1";
\tswitch (event.type) {
\t\t\t\tcase "message.part.updated": {
\t\t\t\t\tconst part = event.properties.part;
\t\t\t\t\tif (part.type !== "tool") context.partById.set(part.id, part);
\t\t\t\t\tif (part.type === "tool") {}
\t\t\t\t\tbreak;
\t\t\t\t}
\t}
}
export function translate(part, role = "assistant") {
\tconst context = {
\t\tsession: { threadId: "thread-1" },
\t\tpartById: new Map(),
\t\tmessageRoleById: new Map([[part.messageID, role]]),
\t};
\tArray.from(handleSubscribedEvent(context, {
\t\ttype: "message.part.updated",
\t\tproperties: { part },
\t}));
\treturn emitted.splice(0);
}
`;

function run(mode, bundle) {
  return spawnSync(process.execPath, [patcher, mode, "--bundle", bundle], { encoding: "utf8" });
}

try {
  const bundle = join(temporary, "bin.mjs");
  writeFileSync(bundle, fixture);

  const before = run("status", bundle);
  assert.equal(before.status, 0, before.stderr);
  assert.deepEqual(JSON.parse(before.stdout), {
    patched: false,
    compatible: true,
    version: null,
    bundle,
  });

  const applied = run("apply", bundle);
  assert.equal(applied.status, 0, applied.stderr);
  const once = readFileSync(bundle, "utf8");
  assert.match(once, /__CONSTRUCT_T3_OPENCODE_MONITOR v1/);
  assert.match(once, /type: "task\.started"/);
  assert.match(once, /type: "task\.completed"/);
  assert.match(once, /part\.tool === "background"/);
  assert.match(once, /part\.tool === "background_kill"/);
  assert.match(once, /<background-task id=/);

  const reapplied = run("apply", bundle);
  assert.equal(reapplied.status, 0, reapplied.stderr);
  assert.equal(readFileSync(bundle, "utf8"), once, "apply must be idempotent");

  const module = await import(pathToFileURL(bundle).href + "?patched=1");
  const baseTool = {
    id: "part-1",
    callID: "call-1",
    messageID: "message-1",
    type: "tool",
    state: { status: "completed", title: "Watch build", metadata: { id: "job-7", wait: true } },
  };
  assert.deepEqual(module.translate({ ...baseTool, tool: "background" }), [
    {
      threadId: "thread-1",
      turnId: "turn-1",
      raw: { type: "message.part.updated", properties: { part: { ...baseTool, tool: "background" } } },
      type: "task.started",
      payload: { taskId: "job-7", taskType: "local_bash", description: "Watch build" },
    },
  ]);
  assert.deepEqual(
    module.translate({
      ...baseTool,
      tool: "background",
      state: { ...baseTool.state, metadata: { id: "job-7", wait: false } },
    }),
    [],
  );

  const wake = {
    id: "part-2",
    messageID: "message-2",
    type: "text",
    text: '<background-task id="job-7" status="failed">details',
  };
  assert.equal(module.translate(wake, "user")[0]?.payload.status, "failed");
  assert.deepEqual(module.translate(wake, "assistant"), []);

  const killed = module.translate({
    ...baseTool,
    tool: "background_kill",
    state: { status: "completed", title: "Kill", metadata: { id: "job-7" } },
  });
  assert.equal(killed[0]?.type, "task.completed");
  assert.deepEqual(killed[0]?.payload, { taskId: "job-7", status: "stopped" });

  const reverted = run("revert", bundle);
  assert.equal(reverted.status, 0, reverted.stderr);
  assert.equal(readFileSync(bundle, "utf8"), fixture, "revert must restore the stock adapter block");

  // Previously installed v1 blocks lived after the cache write. Revert removes
  // the block without depending on the old anchor's position.
  const partLine = '\t\t\t\t\tconst part = event.properties.part;\n';
  const cacheLine = '\t\t\t\t\tcontext.partById.set(part.id, part);\n';
  const blockStart = once.indexOf(partLine) + partLine.length;
  const blockEnd = once.indexOf('\t\t\t\t\tif (part.type !== "tool")', blockStart);
  const injectedBlock = once.slice(blockStart, blockEnd);
  const oldFixture = fixture.replace('if (part.type !== "tool") context.partById.set(part.id, part);', 'context.partById.set(part.id, part);');
  writeFileSync(bundle, oldFixture.replace(cacheLine, cacheLine + injectedBlock));
  assert.equal(run("revert", bundle).status, 0);
  assert.equal(readFileSync(bundle, "utf8"), oldFixture);

  const incompatible = join(temporary, "unknown.mjs");
  const unknownSource = "export const untouched = true;\n";
  writeFileSync(incompatible, unknownSource);
  const skipped = run("apply", incompatible);
  assert.equal(skipped.status, 0, skipped.stderr);
  assert.equal(readFileSync(incompatible, "utf8"), unknownSource);
  assert.match(skipped.stderr, /leaving this T3 bundle unchanged/);

  console.log("T3/OpenCode monitoring patch tests passed");
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
