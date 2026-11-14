// Regression tests for Construct's OpenCode background watcher plugin
// (extension/vm/opencode-background.js): wake-ups, Stop disarming them, the stop
// report on the next prompt, and the reminder on rapid re-checks.
// Run: node test/opencode-background.test.mjs
import assert from "node:assert/strict"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const stateHome = fs.mkdtempSync(path.join(os.tmpdir(), "opencode-background-test-"))
process.env.XDG_STATE_HOME = stateHome
const stateRoot = path.join(stateHome, "opencode", "background")
const { BackgroundTasks } = await import(pathToFileURL(path.join(root, "extension/vm/opencode-background.js")).href)

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
let failures = 0
async function test(name, fn) {
  try {
    await fn()
    console.log(`ok - ${name}`)
  } catch (err) {
    failures++
    console.log(`not ok - ${name}\n${err?.stack ?? err}`)
  }
}

/** A plugin instance with a fake OpenCode client. */
async function harness({ parents = {}, beforeAccept } = {}) {
  const prompts = []
  const aborts = []
  const status = {}
  const client = {
    session: {
      status: async () => ({ data: status }),
      promptAsync: async (request) => {
        if (beforeAccept) await beforeAccept(request)
        prompts.push({ id: request.path.id, text: request.body.parts.map((part) => part.text).join("\n") })
        return { data: true }
      },
      abort: async (request) => {
        aborts.push(request.path.id)
        return { data: true }
      },
      get: async ({ path: { id } }) => ({ data: { id, parentID: parents[id] } }),
    },
  }
  const plugin = await BackgroundTasks({ client, directory: root })
  const ctx = (sessionID) => ({ sessionID, directory: root, ask: async () => {}, metadata: () => {} })
  const start = (sessionID, command, wait = true) =>
    plugin.tool.background.execute({ command, description: command, wait }, ctx(sessionID))
  const output = (sessionID, id, full = false) => plugin.tool.background_output.execute({ id, full }, ctx(sessionID))
  const event = (type, properties) => plugin.event({ event: { type, properties } })
  const abort = (sessionID) =>
    event("session.error", { sessionID, error: { name: "MessageAbortedError", data: { message: "Aborted" } } })
  const nextPrompt = async (sessionID) => {
    const out = { message: { id: "msg_next", sessionID }, parts: [{ id: "prt_user", type: "text", text: "continue" }] }
    await plugin["chat.message"]({ sessionID }, out)
    return out.parts.slice(1)
  }
  return { plugin, prompts, aborts, status, start, output, event, abort, nextPrompt }
}

/** Waits until the plugin's 1 s watcher has seen a job exit. */
const settle = () => sleep(2500)

await test("a finished wait: true task wakes an idle session", async () => {
  const h = await harness()
  const started = await h.start("ses_wake", "echo hello")
  assert.match(started.output, /still running: this call returned immediately and did not wait for it/)
  assert.match(started.output, /Otherwise end your turn now/)
  await settle()
  assert.equal(h.prompts.length, 1)
  assert.equal(h.prompts[0].id, "ses_wake")
  assert.match(h.prompts[0].text, /^<background-task id="bg_1" status="exited 0 \(success\)">/)
  assert.deepEqual(await h.nextPrompt("ses_wake"), [], "a delivered result is not reported again")
  await h.plugin.dispose()
})

await test("an aborted turn disarms the task; the next prompt reports it once", async () => {
  const h = await harness()
  await h.start("ses_abort", "sleep 1")
  await h.abort("ses_abort")
  await h.event("session.idle", { sessionID: "ses_abort" })
  await settle()
  assert.equal(h.prompts.length, 0, "a stopped session must not be woken")
  assert.ok(fs.existsSync(path.join(stateRoot, "ses_abort", "stopped")), "the stop is persisted")
  const parts = await h.nextPrompt("ses_abort")
  assert.equal(parts.length, 1)
  assert.equal(parts[0].synthetic, true)
  assert.equal(parts[0].type, "text")
  assert.equal(parts[0].messageID, "msg_next")
  assert.match(parts[0].id, /^prt_[0-9a-f]{12}[0-9A-Za-z]{14}$/)
  assert.ok(parts[0].id > "prt_", "the id has the ascending layout")
  assert.match(parts[0].text, /<background-tasks-stopped>/)
  assert.match(parts[0].text, /bg_1 \(sleep 1\): exited 0 \(success\)/)
  assert.doesNotMatch(parts[0].text, /^<background-task id=/m, "must not look like a wake-up to T3")
  assert.deepEqual(await h.nextPrompt("ses_abort"), [], "reported only once")
  await h.plugin.dispose()
})

await test("a result parked while busy is not delivered after a Stop", async () => {
  const h = await harness()
  h.status.ses_parked = { type: "busy" }
  await h.start("ses_parked", "echo parked")
  await settle()
  assert.equal(h.prompts.length, 0, "parked while busy")
  await h.abort("ses_parked")
  delete h.status.ses_parked
  await h.event("session.idle", { sessionID: "ses_parked" })
  assert.equal(h.prompts.length, 0, "the Stop disarms the parked wake-up")
  const [part] = await h.nextPrompt("ses_parked")
  assert.match(part.text, /bg_1 \(echo parked\): exited 0/)
  await h.plugin.dispose()
})

await test("T3's stop marker disarms a settled session, including its subagents' tasks", async () => {
  const h = await harness({ parents: { ses_child: "ses_root" } })
  await h.start("ses_root", "sleep 2")
  await h.start("ses_child", "sleep 2")
  await sleep(50)
  fs.writeFileSync(path.join(stateRoot, "ses_root", "stopped"), String(Date.now()))
  await sleep(3500)
  assert.equal(h.prompts.length, 0, "neither the root nor the subagent session is woken")
  const listing = await h.output("ses_root", "")
  assert.match(listing.output, /bg_1 .*\[stopped: does not wake you\]/)
  const [part] = await h.nextPrompt("ses_root")
  assert.equal(part.text.match(/^- bg_1 \(sleep 2\)/gm)?.length, 2, "the root's and the subagent's task are both listed")
  await h.plugin.dispose()
})

await test("tasks started after a Stop wake normally again", async () => {
  const h = await harness()
  await h.abort("ses_again")
  await sleep(20)
  await h.start("ses_again", "echo after")
  await settle()
  assert.equal(h.prompts.length, 1)
  assert.match(h.prompts[0].text, /<background-task id="bg_1"/)
  await h.plugin.dispose()
})

await test("a still-running stopped task is reported with a way to wait for it", async () => {
  const h = await harness()
  await h.start("ses_running", "sleep 30")
  await h.abort("ses_running")
  const [part] = await h.nextPrompt("ses_running")
  assert.match(part.text, /bg_1 \(sleep 30\): running/)
  assert.match(part.text, /while kill -0 \d+ 2>\/dev\/null; do sleep 5; done/)
  const single = await h.output("ses_running", "bg_1")
  assert.match(single.output, /no longer wakes you/)
  await h.plugin.tool.background_kill.execute({ id: "bg_1" }, { sessionID: "ses_running" })
  await h.plugin.dispose()
})

await test("re-checking a running armed task within a minute returns a reminder", async () => {
  const h = await harness()
  await h.start("ses_poll", "echo first; sleep 30")
  await sleep(200)
  const first = await h.output("ses_poll", "bg_1")
  assert.match(first.output, /first/)
  const second = await h.output("ses_poll", "bg_1")
  assert.match(second.output, /nothing to poll/)
  assert.match(second.output, /End your turn now/)
  const full = await h.output("ses_poll", "bg_1", true)
  assert.match(full.output, /output \(from the start\)/, "full: true still shows the output")
  await h.plugin.tool.background_kill.execute({ id: "bg_1" }, { sessionID: "ses_poll" })
  await h.plugin.dispose()
})

await test("a Stop that lands while a wake-up is in flight aborts the turn it started", async () => {
  const h = await harness({
    beforeAccept: async ({ path: { id } }) => {
      fs.writeFileSync(path.join(stateRoot, id, "stopped"), String(Date.now()))
    },
  })
  await h.start("ses_inflight", "echo racing")
  await settle()
  assert.equal(h.prompts.length, 1, "the wake-up was already on its way")
  assert.deepEqual(h.aborts, ["ses_inflight"], "and its turn is aborted right away")
  await h.plugin.dispose()
})

await test("a task adopted from the previous plugin version honours a Stop of its root session", async () => {
  // Old meta.json files have no `root`; the subagent session's task must still obey the root's Stop.
  const dir = path.join(stateRoot, "ses_legacy_child", "bg_1")
  fs.mkdirSync(dir, { recursive: true })
  fs.writeFileSync(path.join(dir, "command.sh"), "sleep 2\n")
  const { spawn } = await import("node:child_process")
  const wrapper = '"$1" "$2" </dev/null; c=$?; printf %s "$c" >"$3.part" && mv "$3.part" "$3"'
  const child = spawn("/bin/sh", ["-c", wrapper, "legacy", "/bin/sh", path.join(dir, "command.sh"), path.join(dir, "exit")], {
    detached: true,
    stdio: "ignore",
  })
  child.unref()
  const legacy = {
    id: "bg_1", dir, log: path.join(dir, "output.log"), command: "sleep 2", description: "legacy", wait: true,
    sessionID: "ses_legacy_child", directory: root, cwd: root, pid: child.pid, started: Date.now(),
  }
  fs.writeFileSync(path.join(dir, "meta.json"), JSON.stringify(legacy))
  const h = await harness({ parents: { ses_legacy_child: "ses_legacy_root" } })
  fs.mkdirSync(path.join(stateRoot, "ses_legacy_root"), { recursive: true })
  fs.writeFileSync(path.join(stateRoot, "ses_legacy_root", "stopped"), String(Date.now()))
  await sleep(3500)
  assert.equal(h.prompts.length, 0, "the root's Stop covers the adopted subagent task")
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, "meta.json"), "utf8")).root, "ses_legacy_root", "root persisted")
  await h.plugin.dispose()
})

await test("a restarted plugin does not adopt a stopped task", async () => {
  const first = await harness()
  await first.start("ses_restart", "sleep 1")
  await first.abort("ses_restart")
  await first.plugin.dispose()
  const second = await harness()
  await settle()
  assert.equal(second.prompts.length, 0)
  const [part] = await second.nextPrompt("ses_restart")
  assert.match(part.text, /bg_1 \(sleep 1\)/)
  await second.plugin.dispose()
})

fs.rmSync(stateHome, { recursive: true, force: true })
if (failures) {
  console.log(`${failures} test(s) failed`)
  process.exit(1)
}
console.log("all opencode background tests passed")
