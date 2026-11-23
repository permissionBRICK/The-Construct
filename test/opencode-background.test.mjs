// Regression tests for Construct's OpenCode 2 background watcher plugin
// (extension/vm/opencode-background.js): wake-ups, Stop disarming them, refusing a
// wake-up a Stop overtook, the stop report on the next prompt, and the reminder on
// rapid re-checks. A fake plugin context stands in for OpenCode; jobs are real processes.
// Run: node test/opencode-background.test.mjs
import assert from "node:assert/strict"
import { spawn } from "node:child_process"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
const stateHome = fs.mkdtempSync(path.join(os.tmpdir(), "opencode-background-test-"))
process.env.XDG_STATE_HOME = stateHome
const stateRoot = path.join(stateHome, "opencode", "background")
const plugin = (await import(pathToFileURL(path.join(root, "extension/vm/opencode-background.js")).href)).default

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

/** An async event stream the plugin subscribes to, fed by the test. */
function channel() {
  const items = []
  let wake
  return {
    push(event) {
      items.push(event)
      wake?.()
    },
    async *iterate(signal) {
      while (!signal?.aborted) {
        if (items.length) {
          yield items.shift()
          continue
        }
        await new Promise((resolve) => {
          wake = resolve
          signal?.addEventListener("abort", resolve, { once: true })
        })
        wake = undefined
      }
    },
  }
}

/**
 * A plugin instance with a fake OpenCode 2 context. Like OpenCode, a synthetic message for an
 * idle session runs at once: it joins the session's history and the `context` hook sees the
 * request before "the model" does (`requests`). With `hold: true` OpenCode keeps admitted
 * messages queued until `run(sessionID)`.
 */
async function harness({ parents = {}, getFailures = 0, hold = false } = {}) {
  const hooks = {}
  const tools = {}
  const events = channel()
  const synthetics = []
  const requests = []
  const interrupts = []
  const interruptCalls = []
  const history = new Map()
  const queued = new Map()
  let failuresLeft = getFailures
  let n = 0

  async function run(sessionID) {
    const pending = queued.get(sessionID) ?? []
    queued.delete(sessionID)
    const messages = history.get(sessionID) ?? []
    for (const item of pending) messages.push({ id: item.id, role: "user", content: item.text, metadata: item.metadata })
    history.set(sessionID, messages)
    const request = { sessionID, messages: [...messages] }
    const before = interrupts.length
    await hooks.context?.(request)
    if (interrupts.length === before) requests.push(request)
  }

  const ctx = {
    location: { directory: root },
    event: { subscribe: (options) => events.iterate(options?.signal) },
    session: {
      hook: async (name, callback) => {
        hooks[name] = callback
        return { dispose: async () => {} }
      },
      get: async ({ sessionID }) => {
        if (failuresLeft > 0) {
          failuresLeft--
          throw new Error("server not ready")
        }
        return { id: sessionID, ...(parents[sessionID] ? { parentID: parents[sessionID] } : {}) }
      },
      synthetic: async (input) => {
        const item = { ...input, id: `msg_${++n}` }
        synthetics.push(item)
        queued.set(input.sessionID, [...(queued.get(input.sessionID) ?? []), item])
        if (!hold && input.resume !== false) await run(input.sessionID)
        return { id: item.id }
      },
      // Like OpenCode: the call returns at once, the interrupted event follows.
      interrupt: async (input) => {
        const { sessionID } = input
        interrupts.push(sessionID)
        interruptCalls.push(input)
        setTimeout(() => events.push({ type: "session.execution.interrupted", data: { sessionID, reason: "user" } }), 5)
        return { interrupted: true }
      },
    },
    tool: { transform: async (callback) => callback({ add: (tool) => (tools[tool.name] = tool) }) },
    permission: { assert: async () => {} },
  }
  const dispose = await plugin.setup(ctx)
  const context = (sessionID) => ({ sessionID, agent: "build", messageID: "msg_tool", id: "call_1" })
  const start = (sessionID, command, wait = true) =>
    tools.background.execute({ command, description: command, wait }, context(sessionID))
  const output = (sessionID, id, full = false) => tools.background_output.execute({ id, full }, context(sessionID))
  const kill = (sessionID, id) => tools.background_kill.execute({ id }, context(sessionID))
  const event = async (type, data) => {
    events.push({ type, data })
    await sleep(30)
  }
  const interrupted = (sessionID, reason = "user") => event("session.execution.interrupted", { sessionID, reason })
  /** A user prompt: the `prompt` hook runs first, then the prompt joins the history. */
  const userPrompt = async (sessionID, text = "continue") => {
    const admitted = synthetics.length
    await hooks.prompt({ sessionID, messageID: "msg_user", prompt: { text }, delivery: "steer" })
    const notes = synthetics.slice(admitted)
    // Delivered together with the prompt (both steer).
    queued.set(sessionID, (queued.get(sessionID) ?? []).filter((item) => !notes.includes(item)))
    history.set(sessionID, [
      ...(history.get(sessionID) ?? []),
      ...notes.map((note) => ({ id: note.id, role: "user", content: note.text, metadata: note.metadata })),
      { id: `msg_user_${++n}`, role: "user", content: text },
    ])
    return notes
  }
  const wakes = (sessionID) =>
    synthetics.filter((s) => s.sessionID === sessionID && s.metadata?.source === "construct-background")
  return { hooks, synthetics, requests, interrupts, interruptCalls, history, start, output, kill, event, interrupted, userPrompt, wakes, run, dispose }
}

/** Waits until the plugin's 1 s watcher has seen a job exit. */
const settle = () => sleep(2500)
const writeStop = (sessionID, at = Date.now()) => {
  fs.mkdirSync(path.join(stateRoot, sessionID), { recursive: true })
  fs.writeFileSync(path.join(stateRoot, sessionID, "stopped"), String(at))
}

await test("a finished wait: true task wakes an idle session", async () => {
  const h = await harness()
  const started = await h.start("ses_wake", "echo hello")
  assert.match(started.content, /still running: this call returned immediately and did not wait for it/)
  assert.match(started.content, /Otherwise end your turn now/)
  assert.equal(started.metadata.id, "bg_1")
  assert.equal(started.metadata.wait, true)
  await settle()
  const [wake] = h.wakes("ses_wake")
  assert.ok(wake, "woken")
  assert.deepEqual(wake.metadata, { source: "construct-background", id: "bg_1" })
  assert.equal(wake.delivery, "queue")
  assert.match(wake.text, /^<background-task id="bg_1" status="exited 0 \(success\)">/)
  assert.equal(h.requests.length, 1, "the model saw the wake-up")
  assert.deepEqual(await h.userPrompt("ses_wake"), [], "a delivered result is not reported again")
  h.dispose()
})

await test("an interrupted execution disarms the task; the next prompt reports it once", async () => {
  const h = await harness()
  await h.event("session.execution.started", { sessionID: "ses_abort" })
  await h.start("ses_abort", "sleep 1")
  await h.interrupted("ses_abort")
  await settle()
  assert.equal(h.wakes("ses_abort").length, 0, "a stopped session must not be woken")
  assert.ok(fs.existsSync(path.join(stateRoot, "ses_abort", "stopped")), "the stop is persisted")
  const [note, ...rest] = await h.userPrompt("ses_abort")
  assert.equal(rest.length, 0)
  assert.deepEqual(note.metadata, { source: "construct-background-stopped" })
  assert.equal(note.description, undefined, "no description: timelines hide it")
  assert.equal(note.delivery, "steer")
  assert.equal(note.resume, false, "the user's prompt wakes the session, not the note")
  assert.match(note.text, /<background-tasks-stopped>/)
  assert.match(note.text, /bg_1 \(sleep 1\): exited 0 \(success\)/)
  assert.doesNotMatch(note.text, /^<background-task id=/m, "must not look like a wake-up")
  assert.deepEqual(await h.userPrompt("ses_abort"), [], "reported only once")
  h.dispose()
})

await test("a shutdown or a failed execution is not a Stop", async () => {
  const h = await harness()
  await h.event("session.execution.started", { sessionID: "ses_shutdown" })
  await h.start("ses_shutdown", "sleep 1")
  await h.interrupted("ses_shutdown", "shutdown")
  await h.event("session.execution.started", { sessionID: "ses_shutdown" })
  await h.event("session.execution.failed", { sessionID: "ses_shutdown", error: { type: "x" } })
  await settle()
  assert.ok(!fs.existsSync(path.join(stateRoot, "ses_shutdown", "stopped")))
  assert.equal(h.wakes("ses_shutdown").length, 1)
  h.dispose()
})

await test("a wake-up is held back while the session runs and delivered when the turn ends", async () => {
  const h = await harness()
  await h.event("session.execution.started", { sessionID: "ses_busy" })
  await h.start("ses_busy", "echo held")
  await settle()
  assert.equal(h.wakes("ses_busy").length, 0, "never queued behind running work")
  await h.hooks["experimental.idle"]({ sessionID: "ses_busy", background: [] })
  assert.equal(h.wakes("ses_busy").length, 1, "the idle hook hands it over in the same execution")
  await h.event("session.execution.succeeded", { sessionID: "ses_busy" })
  assert.equal(h.wakes("ses_busy").length, 1, "only once")
  h.dispose()
})

await test("a wake-up held back while busy is not delivered after a Stop", async () => {
  const h = await harness()
  await h.event("session.execution.started", { sessionID: "ses_parked" })
  await h.start("ses_parked", "echo parked")
  await settle()
  await h.interrupted("ses_parked")
  await h.event("session.execution.succeeded", { sessionID: "ses_parked" })
  await sleep(1200)
  assert.equal(h.wakes("ses_parked").length, 0, "the Stop disarms the held-back wake-up")
  const [note] = await h.userPrompt("ses_parked")
  assert.match(note.text, /bg_1 \(echo parked\): exited 0/)
  h.dispose()
})

await test("T3's stop marker disarms a settled session, including its subagents' tasks", async () => {
  const h = await harness({ parents: { ses_child: "ses_root" } })
  await h.start("ses_root", "sleep 2")
  await h.start("ses_child", "sleep 2")
  await sleep(50)
  writeStop("ses_root")
  await sleep(3500)
  assert.equal(h.synthetics.length, 0, "neither the root nor the subagent session is woken")
  const listing = await h.output("ses_root", "")
  assert.match(listing.content, /bg_1 .*\[stopped: does not wake you\]/)
  const [note] = await h.userPrompt("ses_root")
  assert.equal(note.text.match(/^- bg_1 \(sleep 2\)/gm)?.length, 2, "the root's and the subagent's task are both listed")
  h.dispose()
})

await test("tasks started after a Stop wake normally again; job ids skip the marker", async () => {
  const h = await harness()
  await h.interrupted("ses_again")
  await sleep(20)
  const started = await h.start("ses_again", "echo after")
  assert.equal(started.metadata.id, "bg_1", "the stopped marker is not a task")
  await settle()
  assert.equal(h.wakes("ses_again").length, 1)
  assert.match(h.wakes("ses_again")[0].text, /<background-task id="bg_1"/)
  h.dispose()
})

await test("a still-running stopped task is reported with a way to wait for it", async () => {
  const h = await harness()
  await h.start("ses_running", "sleep 30")
  await h.interrupted("ses_running")
  const [note] = await h.userPrompt("ses_running")
  assert.match(note.text, /bg_1 \(sleep 30\): running/)
  assert.match(note.text, /while kill -0 \d+ 2>\/dev\/null; do sleep 5; done/)
  const single = await h.output("ses_running", "bg_1")
  assert.match(single.content, /no longer wakes you/)
  await h.kill("ses_running", "bg_1")
  h.dispose()
})

await test("re-checking a running armed task within a minute returns a reminder", async () => {
  const h = await harness()
  await h.start("ses_poll", "echo first; sleep 30")
  await sleep(200)
  const first = await h.output("ses_poll", "bg_1")
  assert.match(first.content, /first/)
  const second = await h.output("ses_poll", "bg_1")
  assert.match(second.content, /nothing to poll/)
  assert.match(second.content, /End your turn now/)
  const full = await h.output("ses_poll", "bg_1", true)
  assert.match(full.content, /output \(from the start\)/, "full: true still shows the output")
  const killed = await h.kill("ses_poll", "bg_1")
  assert.equal(killed.metadata.id, "bg_1")
  h.dispose()
})

await test("a wake-up OpenCode still held when a Stop landed is refused before the model sees it", async () => {
  const h = await harness({ hold: true })
  await h.start("ses_held", "echo racing")
  await settle()
  assert.equal(h.wakes("ses_held").length, 1, "handed to OpenCode, which keeps it queued")
  const stoppedAt = Date.now()
  writeStop("ses_held", stoppedAt)
  await h.run("ses_held")
  assert.equal(h.requests.length, 0, "the model is never asked: the turn is interrupted first")
  assert.deepEqual(h.interrupts, ["ses_held"])
  assert.equal(h.interruptCalls[0].resume, true, "a prompt steered in meanwhile still runs")
  await sleep(30)
  assert.equal(fs.readFileSync(path.join(stateRoot, "ses_held", "stopped"), "utf8"), String(stoppedAt), "the plugin's own interrupt is not a new Stop")
  assert.ok(fs.existsSync(path.join(stateRoot, "ses_held", "bg_1", "refused")))
  const [note] = await h.userPrompt("ses_held")
  assert.match(note.text, /bg_1 \(echo racing\): exited 0/, "the result is reported on the next prompt instead")
  await h.run("ses_held")
  assert.equal(h.requests.length, 1)
  assert.ok(
    h.requests[0].messages.every((m) => m.metadata?.source !== "construct-background"),
    "the refused wake-up stays out of every later request",
  )
  assert.deepEqual(h.interrupts, ["ses_held"], "a turn the user started is never interrupted")
  h.dispose()
})

await test("a Stop after a wake-up turn started ends that turn, never a newer user turn", async () => {
  const h = await harness()
  await h.start("ses_turn", "echo woke")
  await settle()
  assert.equal(h.requests.length, 1, "accepted")
  await h.event("session.execution.started", { sessionID: "ses_turn" })
  writeStop("ses_turn", Date.now() + 1)
  await sleep(1500)
  assert.deepEqual(h.interrupts, ["ses_turn"], "the wake-up turn is ended")

  await h.start("ses_resumed", "echo woke")
  await settle()
  assert.equal(h.requests.length, 2)
  await h.event("session.execution.started", { sessionID: "ses_resumed" })
  await h.userPrompt("ses_resumed") // the user's own prompt during the wake-up turn
  writeStop("ses_resumed", Date.now() + 1)
  await sleep(1500)
  assert.deepEqual(h.interrupts, ["ses_turn"], "the user's turn is left alone")
  assert.equal(h.requests.length, 2)
  await h.run("ses_resumed")
  assert.equal(h.requests.length, 3, "an accepted wake-up stays in later requests")
  assert.ok(h.requests[2].messages.some((m) => m.metadata?.source === "construct-background"))
  h.dispose()
})

const wakeMessage = (wake) => ({ role: "user", content: wake.text, metadata: wake.metadata })

await test("a wake-up held back during a turn survives a crash before the turn ends", async () => {
  const first = await harness()
  await first.event("session.execution.started", { sessionID: "ses_crash" })
  await first.start("ses_crash", "echo survived")
  await settle()
  assert.equal(first.wakes("ses_crash").length, 0, "held back")
  assert.ok(!fs.existsSync(path.join(stateRoot, "ses_crash", "bg_1", "notified")), "not claimed while held back")
  first.dispose() // OpenCode dies before the turn ends: no end-of-execution event
  const second = await harness()
  await settle()
  assert.equal(second.wakes("ses_crash").length, 1, "the next start adopts the job and delivers its wake-up")
  second.dispose()
})

await test("a subagent's root is found through any number of levels, never through a cycle", async () => {
  const parents = { ses_cycle_a: "ses_cycle_b", ses_cycle_b: "ses_cycle_a" }
  for (let level = 1; level <= 12; level++) parents[`ses_deep_${level}`] = `ses_deep_${level - 1}`
  const h = await harness({ parents })
  await h.start("ses_deep_12", "sleep 1")
  await h.start("ses_cycle_a", "sleep 30")
  const meta = (id) => JSON.parse(fs.readFileSync(path.join(stateRoot, id, "bg_1", "meta.json"), "utf8"))
  assert.equal(meta("ses_deep_12").root, "ses_deep_0", "the real root, not an intermediate ancestor")
  assert.equal(meta("ses_cycle_a").root, undefined, "a cycle is never recorded as the root")
  writeStop("ses_deep_0")
  await settle()
  assert.equal(h.wakes("ses_deep_12").length, 0, "the real root's Stop covers the deep subagent")
  await h.kill("ses_cycle_a", "bg_1")
  h.dispose()
})

await test("the first prompt after a Stop lists a task whose wake-up OpenCode still holds", async () => {
  const h = await harness({ hold: true })
  await h.start("ses_queued", "echo queued")
  await settle()
  assert.equal(h.wakes("ses_queued").length, 1, "handed over, still queued in OpenCode")
  writeStop("ses_queued")
  const [note] = await h.userPrompt("ses_queued") // a steered prompt runs ahead of the queued wake-up
  assert.match(note.text, /bg_1 \(echo queued\): exited 0/)
  const listing = await h.output("ses_queued", "")
  assert.match(listing.content, /\[stopped: does not wake you\]/)
  await h.run("ses_queued")
  assert.ok(fs.existsSync(path.join(stateRoot, "ses_queued", "bg_1", "refused")))
  assert.deepEqual(await h.userPrompt("ses_queued"), [], "reported only once")
  h.dispose()
})

await test("a compaction leaves out a wake-up a Stop disarmed and leaves the decision to the request", async () => {
  const h = await harness({ hold: true })
  await h.start("ses_compact", "echo compact")
  await settle()
  writeStop("ses_compact")
  const [wake] = h.wakes("ses_compact")
  const messages = [{ role: "user", content: "earlier" }, { role: "assistant", content: "ok" }, wakeMessage(wake)]
  await h.hooks.compaction({ sessionID: "ses_compact", messages })
  assert.equal(messages.length, 2, "not summarized")
  assert.ok(!fs.existsSync(path.join(stateRoot, "ses_compact", "bg_1", "refused")), "undecided until the request")
  assert.deepEqual(h.interrupts, [])
  await h.run("ses_compact")
  assert.deepEqual(h.interrupts, ["ses_compact"], "the request still refuses it and ends the turn")
  h.dispose()
})

await test("a refused wake-up ends its turn even when a step limit appended an assistant note", async () => {
  const h = await harness({ hold: true })
  await h.start("ses_steps", "echo steps")
  await settle()
  writeStop("ses_steps")
  const [wake] = h.wakes("ses_steps")
  const messages = [{ role: "user", content: "earlier" }, { role: "assistant", content: "ok" }, wakeMessage(wake), { role: "assistant", content: "MAX STEPS" }]
  await h.hooks.context({ sessionID: "ses_steps", messages })
  assert.deepEqual(h.interrupts, ["ses_steps"], "interrupted before the model is asked")
  assert.ok(messages.every((m) => m.metadata?.source !== "construct-background"), "left out of the request")

  // The same with the user's prompt promoted after it: that turn is the user's and runs.
  await h.start("ses_steps_user", "echo steps")
  await settle()
  writeStop("ses_steps_user")
  const [other] = h.wakes("ses_steps_user")
  const withPrompt = [wakeMessage(other), { role: "user", content: "the user's prompt" }]
  await h.hooks.context({ sessionID: "ses_steps_user", messages: withPrompt })
  assert.deepEqual(h.interrupts, ["ses_steps"], "the user's turn is not interrupted")
  assert.equal(withPrompt.length, 1)
  h.dispose()
})

await test("a root Stop never interrupts a child's prompt admitted after its wake-up turn started", async () => {
  const h = await harness({ parents: { ses_kid: "ses_top" }, hold: true })
  await h.start("ses_kid", "echo kid")
  await settle()
  await h.event("session.execution.started", { sessionID: "ses_kid" })
  // OpenCode runs the prompt hook before it admits the prompt; the wake-up's request runs in between.
  await h.hooks.prompt({ sessionID: "ses_kid", messageID: "msg_kid_user", prompt: { text: "user prompt" }, delivery: "steer" })
  await h.run("ses_kid")
  h.history.get("ses_kid").push({ id: "msg_kid_user", role: "user", content: "user prompt" })
  await h.run("ses_kid") // the next request carries the user's prompt
  assert.equal(h.requests.length, 2)
  writeStop("ses_top", Date.now() + 1)
  await sleep(1500)
  assert.deepEqual(h.interrupts, [], "the child's turn now belongs to the user")
  h.dispose()
})

await test("after a restart the root's first prompt lists a child's task whose held wake-up a Stop disarmed", async () => {
  const first = await harness({ parents: { ses_kid_r: "ses_top_r" }, hold: true })
  await first.start("ses_kid_r", "echo kid")
  await settle()
  assert.equal(first.wakes("ses_kid_r").length, 1, "handed over, still queued in OpenCode")
  writeStop("ses_top_r")
  first.dispose()
  const second = await harness({ parents: { ses_kid_r: "ses_top_r" } })
  const [note] = await second.userPrompt("ses_top_r")
  assert.match(note?.text ?? "", /bg_1 \(echo kid\): exited 0/)
  await settle()
  assert.equal(second.wakes("ses_kid_r").length, 0, "indexed for the report, not watched again")
  second.dispose()
})

await test("a task adopted from the previous plugin version honours a Stop of its root session", async () => {
  // Old meta.json files have no `root`; the subagent session's task must still obey the root's Stop.
  const dir = path.join(stateRoot, "ses_legacy_child", "bg_1")
  fs.mkdirSync(dir, { recursive: true })
  fs.writeFileSync(path.join(dir, "command.sh"), "sleep 2\n")
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
  const h = await harness({ parents: { ses_legacy_child: "ses_legacy_root" }, getFailures: 3 })
  writeStop("ses_legacy_root")
  await sleep(7000)
  assert.equal(h.synthetics.length, 0, "the root's Stop covers the adopted subagent task, after lookups failed at first")
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, "meta.json"), "utf8")).root, "ses_legacy_root", "root persisted")
  h.dispose()
})

await test("a restarted plugin does not adopt a stopped task", async () => {
  const first = await harness()
  await first.start("ses_restart", "sleep 1")
  await first.interrupted("ses_restart")
  first.dispose()
  const second = await harness()
  await settle()
  assert.equal(second.wakes("ses_restart").length, 0)
  const [note] = await second.userPrompt("ses_restart")
  assert.match(note.text, /bg_1 \(sleep 1\)/)
  second.dispose()
})

fs.rmSync(stateHome, { recursive: true, force: true })
if (failures) {
  console.log(`${failures} test(s) failed`)
  process.exit(1)
}
console.log("all opencode background tests passed")
