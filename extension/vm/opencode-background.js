// Construct-managed OpenCode background watcher.
// Derived from opencode-cortecs-config commit a8e69aa91e006785179cf8f0249f06afca273c4a;
// the Cortecs request plugin and provider configuration are intentionally excluded.
// Background commands for OpenCode: fire-and-forget shell jobs that outlive the
// tool call, the agent turn and opencode itself.
//
// Jobs are spawned detached (own session + process group), so no timeout, SIGTERM
// to opencode or closed TUI can take them down. State lives on disk under
// STATE_ROOT so output stays readable across restarts.
//
// With wait: true the plugin watches the job and, once it exits, queues a message
// for the session -- which wakes the agent even if its turn ended long ago.
//
// Stop disarms: when the user stops the session (an interrupted execution, or the
// `stopped` marker that Construct's T3 patch writes on every Stop), every wait: true
// task started before that moment stops waking it. The task keeps running and its
// result stays on disk; the next prompt to the session tells the agent which tasks
// were disarmed.
//
// An OpenCode 2 plugin (default export with id and setup). Its tools are direct
// tools (codemode: false), so clients see their calls: T3 Code's OpenCode monitor
// shows an armed watcher from the `background` call's metadata { id, wait } and
// ends it on the wake message (metadata.source "construct-background") or on a
// `background_kill` call.
import { spawn } from "node:child_process"
import fs from "node:fs"
import os from "node:os"
import path from "node:path"

const STATE_ROOT = path.join(
  process.env.XDG_STATE_HOME || path.join(os.homedir(), ".local", "state"),
  "opencode",
  "background",
)
const POLL_MS = 1000
// Jobs older than this are neither adopted after a restart nor kept on disk.
const ADOPT_MAX_AGE_MS = 24 * 60 * 60 * 1000
const PRUNE_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000
const TAIL_LINES = 40
const TAIL_BYTES = 4000
const SHELL = fs.existsSync("/bin/bash") ? "/bin/bash" : "/bin/sh"
// Per-session marker holding the epoch milliseconds of the last Stop. Construct's
// T3 patch writes it too, so keep the name and format stable.
const STOP_FILE = "stopped"
// Per-task markers: the wake-up was handed to OpenCode (`wake`), the model saw it
// (`accepted`), a Stop overtook it before the model saw it (`refused`), the agent
// was told the task no longer wakes it (`reported`).
const WAKE_FILE = "wake"
const ACCEPTED_FILE = "accepted"
const REFUSED_FILE = "refused"
const REPORTED_FILE = "reported"
const SOURCE = "construct-background"
const REPORT_SOURCE = "construct-background-stopped"
// A second background_output on a still-armed running task within this window
// is answered with a reminder instead of output.
const POLL_GUARD_MS = 60 * 1000
// Rounds (about a second each) a finished job waits for its root session to be known.
const ROOT_RETRIES = 60
// How long a refused wake-up's turn may take to end after the plugin interrupted it.
const REFUSE_WAIT_MS = 5000
// An interrupted event this soon after the plugin's own interrupt is not a user Stop.
const SELF_INTERRUPT_MS = 10 * 1000

const BACKGROUND_DESCRIPTION = `Run a shell command in the background, detached from this tool call.

This call returns immediately. It does NOT wait for the command: the command is
still running when you get the result of this call. It runs in its own process
group and keeps going after your turn ends and after opencode exits. Nothing
kills it on a timeout. Use it for anything slow or open ended: test suites,
builds, installs, downloads, dev servers, watchers, log tails, long migrations.

wait: true (the normal choice)
  When the command exits, its result arrives in this session as a new message
  that resumes you automatically, even after your turn has ended. So the way to
  wait is to stop: start the task, then END YOUR TURN with a one-line status,
  unless you have other work that does not need the result. Do not wait for it
  yourself -- no background_output checks, no sleep, no polling loop, no other
  blocking command; every one of those costs turns. The user's UI shows the
  session as waiting on the task until its result resumes you.

Waiting for a condition instead of a command
  To wait for something to become true -- a VM getting an address, a CI run
  finishing, a file, port or log line appearing -- write a shell loop that exits
  once the condition holds (give it a time limit), run it with wait: true, and
  end your turn. Example:
    for i in $(seq 1 120); do curl -sf http://host:8080/health && exit 0; sleep 15; done; exit 1

wait: false
  True fire and forget. Nobody wakes you. The process outlives opencode
  entirely. Use it for things you never need the result of (a dev server you
  just want running, a detached deploy). You can still inspect it later with
  background_output.

If the user stops you, wait: true tasks started before the stop no longer wake
you; they keep running and the next message tells you which ones were affected.

Use the normal shell/bash tool instead when the command is quick and you need
its result right now.`

const OUTPUT_DESCRIPTION = `Check a background task started with the background tool: its status and its output.

Use it to look at a task when you have a reason to (it was woken, you suspect it
hangs, you need a dev server's log) -- not to wait. A wait: true task wakes you
by itself when it exits; checking it repeatedly while it runs only burns turns,
and a re-check within a minute is answered with a reminder instead of output.

By default you get only output that appeared since your last check on that task.
Pass full: true for the whole output. Pass an empty id to list every background
task of this session with its status. Every task also has a plain log file on
disk (path is reported), so you can grep or tail it with the normal shell tool.`

const KILL_DESCRIPTION = `Stop a background task started with the background tool.

Terminates the whole process group (SIGTERM, then SIGKILL if it does not exit).
A killed task never sends a wake-up notification -- you already know it is gone.`

/** Plain JSON Schema input; every argument is required, as OpenCode 1 made them. */
const input = (properties) => ({
  type: "object",
  properties,
  required: Object.keys(properties),
  additionalProperties: false,
})

export default { id: "construct-background", setup }

async function setup(ctx) {
  const directory = ctx.location.directory
  /** key `sessionID/id` -> job, jobs we still owe a notification for */
  const watching = new Map()
  /** key `sessionID/id` -> job, every wait: true job of this process (for stop reports) */
  const known = new Map()
  /** sessionID -> [{ job, text }], wake-ups held back until the session's execution ends */
  const parked = new Map()
  /** sessions with a running execution, from OpenCode's execution and step events */
  const busy = new Set()
  /** key `sessionID/id` -> time background_output last returned output for it */
  const lastCheck = new Map()
  /** sessionID -> parent sessionID, or null for a root session */
  const parents = new Map()
  /** sessionID -> { at, root }, a turn that runs on an accepted wake-up */
  const wakeTurns = new Map()
  /** sessionID -> time the plugin interrupted that session itself */
  const selfInterrupts = new Map()
  /** sessionID -> callbacks waiting for that session's execution to be interrupted */
  const interruptWaiters = new Map()
  /** inbox id -> job, wake-ups handed to OpenCode in this process */
  const inboxJobs = new Map()
  const subscription = new AbortController()
  let timer
  let polling = false

  // --- job state on disk -----------------------------------------------------

  const sessionDir = (sessionID) => path.join(STATE_ROOT, sessionID)
  const jobDir = (sessionID, id) => path.join(sessionDir(sessionID), id)
  const has = (dir, file) => fs.existsSync(path.join(dir, file))
  const touch = (dir, file, text = "") => {
    try {
      fs.writeFileSync(path.join(dir, file), text)
    } catch {}
  }
  const remove = (dir, file) => fs.rmSync(path.join(dir, file), { force: true })

  function readMeta(dir) {
    try {
      return JSON.parse(fs.readFileSync(path.join(dir, "meta.json"), "utf8"))
    } catch {
      return undefined
    }
  }

  function readExit(dir) {
    try {
      const raw = fs.readFileSync(path.join(dir, "exit"), "utf8").trim()
      return { code: Number(raw), at: fs.statSync(path.join(dir, "exit")).mtimeMs }
    } catch {
      return undefined
    }
  }

  // --- stop markers ------------------------------------------------------------

  function stoppedAt(sessionID) {
    if (!sessionID) return 0
    const at = Number(safeRead(path.join(sessionDir(sessionID), STOP_FILE)).trim())
    return Number.isFinite(at) ? at : 0
  }

  /** Records a Stop of the session. The marker only ever moves forward. */
  function markStopped(sessionID, at = Date.now()) {
    if (stoppedAt(sessionID) >= at) return
    try {
      fs.mkdirSync(sessionDir(sessionID), { recursive: true })
      const file = path.join(sessionDir(sessionID), STOP_FILE)
      fs.writeFileSync(`${file}.${process.pid}.part`, String(at))
      fs.renameSync(`${file}.${process.pid}.part`, file)
    } catch {}
  }

  /** Latest Stop of the session or of its root session. */
  const stopOf = (sessionID, root) => Math.max(stoppedAt(sessionID), root && root !== sessionID ? stoppedAt(root) : 0)

  /** A wait: true job started before a Stop of its session or root session never wakes it. */
  const disarmed = (job) => Boolean(job.wait) && stopOf(job.sessionID, job.root) >= job.started

  /** The root of a (subagent) session's parent chain, or undefined while the server cannot say. */
  async function rootOf(sessionID) {
    const seen = new Set()
    for (let id = sessionID; !seen.has(id); ) {
      seen.add(id)
      let parent = parents.get(id)
      if (parent === undefined) {
        try {
          const session = await ctx.session.get({ sessionID: id })
          if (!session?.id) return undefined
          parent = session.parentID ?? null
        } catch {
          return undefined
        }
        parents.set(id, parent)
      }
      if (!parent) return id
      id = parent
    }
    return undefined // a cycle: no root to trust
  }

  const alive = (pid) => {
    try {
      process.kill(pid, 0)
      return true
    } catch (err) {
      return err.code === "EPERM"
    }
  }

  /** Returns undefined while the job is still running. */
  function outcome(job) {
    const exit = readExit(job.dir)
    if (exit) return exit
    if (alive(job.pid)) return undefined
    // Process is gone without leaving an exit code (machine went down mid-run).
    // Give the wrapper a couple of ticks to write the file before giving up.
    job.vanished = (job.vanished ?? 0) + 1
    if (job.vanished < 3) return undefined
    return { code: undefined, at: Date.now() }
  }

  function listJobs(sessionID) {
    let ids = []
    try {
      ids = fs.readdirSync(sessionDir(sessionID)).sort()
    } catch {
      return []
    }
    return ids.map((id) => readMeta(jobDir(sessionID, id))).filter(Boolean)
  }

  function nextJobDir(sessionID) {
    const base = sessionDir(sessionID)
    fs.mkdirSync(base, { recursive: true })
    let n = fs.readdirSync(base).filter((name) => name.startsWith("bg_")).length + 1
    for (;;) {
      const dir = path.join(base, `bg_${n}`)
      try {
        fs.mkdirSync(dir)
        return { id: `bg_${n}`, dir }
      } catch (err) {
        if (err.code !== "EEXIST") throw err
        n++
      }
    }
  }

  function spawnJob({ command, description, wait, sessionID, root, cwd }) {
    const { id, dir } = nextJobDir(sessionID)
    const log = path.join(dir, "output.log")
    const commandFile = path.join(dir, "command.sh")
    const exitFile = path.join(dir, "exit")
    fs.writeFileSync(commandFile, command.endsWith("\n") ? command : command + "\n")

    // The command runs under a tiny sh wrapper that records the exit code, so the
    // result survives even when opencode is no longer around to observe it.
    const wrapper = '"$1" "$2" </dev/null; c=$?; printf %s "$c" >"$3.part" && mv "$3.part" "$3"'
    const out = fs.openSync(log, "a")
    let child
    try {
      child = spawn("/bin/sh", ["-c", wrapper, "opencode-background", SHELL, commandFile, exitFile], {
        cwd,
        env: { ...process.env, OPENCODE_BACKGROUND_ID: id },
        detached: true, // own session + process group: survives signals aimed at opencode
        stdio: ["ignore", out, out],
      })
    } finally {
      fs.closeSync(out)
    }
    child.unref()

    const job = {
      id,
      dir,
      log,
      command,
      description,
      wait,
      sessionID,
      root, // undefined while unknown; notify() resolves it before any wake-up
      directory,
      cwd,
      pid: child.pid,
      started: Date.now(),
    }
    fs.writeFileSync(path.join(dir, "meta.json"), JSON.stringify(job, undefined, 2))
    if (wait) watch(job)
    return job
  }

  // --- watching and waking ---------------------------------------------------

  function watch(job) {
    const key = `${job.sessionID}/${job.id}`
    known.set(key, job)
    watching.set(key, job)
    ensureTimer()
  }

  function ensureTimer() {
    if (timer) return
    timer = setInterval(poll, POLL_MS)
    timer.unref?.()
  }

  async function poll() {
    if (polling) return // a slow round must not overlap the next one
    polling = true
    try {
      const retry = []
      for (const [key, job] of watching) {
        // Stopped: stop watching. The result stays on disk and is reported on the next prompt.
        if (disarmed(job)) {
          watching.delete(key)
          continue
        }
        const result = outcome(job)
        if (!result) continue
        watching.delete(key)
        if (!(await notify(job, result))) retry.push(job)
      }
      for (const job of retry) watching.set(`${job.sessionID}/${job.id}`, job)
      // A wake-up held back for a session whose end of execution was missed.
      for (const sessionID of [...parked.keys()]) if (!busy.has(sessionID)) await flush(sessionID)
      await enforceStops()
    } finally {
      polling = false
    }
    if (!watching.size && !wakeTurns.size && !parked.size && timer) {
      clearInterval(timer)
      timer = undefined
    }
  }

  /** A Stop during a running wake-up turn ends that turn, unless a newer prompt took it over. */
  async function enforceStops() {
    for (const [sessionID, turn] of wakeTurns) {
      if (stopOf(sessionID, turn.root) < turn.at) continue
      wakeTurns.delete(sessionID)
      if (busy.has(sessionID)) await interrupt(sessionID)
    }
  }

  async function interrupt(sessionID) {
    selfInterrupts.set(sessionID, Date.now())
    try {
      // resume: a prompt steered in meanwhile still runs instead of waiting for the next wake.
      const reply = await ctx.session.interrupt({ sessionID, resume: true })
      if (reply?.interrupted === false) selfInterrupts.delete(sessionID)
    } catch {
      selfInterrupts.delete(sessionID)
    }
  }

  /** Marks the job as notified; returns false if someone else got there first. */
  function claim(dir) {
    try {
      fs.writeFileSync(path.join(dir, "notified"), "", { flag: "wx" })
      return true
    } catch {
      return false
    }
  }

  /** The job no longer counts as delivered, so the next stop report lists it. */
  function unclaim(dir) {
    remove(dir, "notified")
    remove(dir, WAKE_FILE)
  }

  /** Jobs adopted from an older plugin version carry no root session yet. */
  async function resolveRoot(job) {
    if (job.root) return
    const root = await rootOf(job.sessionID)
    if (!root) return
    job.root = root
    try {
      const file = path.join(job.dir, "meta.json")
      const meta = JSON.parse(fs.readFileSync(file, "utf8"))
      fs.writeFileSync(file, JSON.stringify({ ...meta, root: job.root }, undefined, 2))
    } catch {}
  }

  /** Returns false when the job should be retried on a later round. */
  async function notify(job, result) {
    await resolveRoot(job)
    // Without its root the job cannot tell whether a Stop of the root covers it. Retry for a
    // while (the server may still be starting); after that only its own session's Stop counts.
    if (!job.root && (job.rootTries = (job.rootTries ?? 0) + 1) < ROOT_RETRIES) return false
    if (disarmed(job)) return true
    const entry = { job, text: renderNotification(job, result) }
    // OpenCode keeps a queued message through an interrupt and runs it on the next wake, so a
    // wake-up is never queued behind running work: it waits here until that work has ended.
    // It is claimed only when it is handed over, so after a crash the next start adopts it again.
    if (busy.has(job.sessionID)) {
      parked.set(job.sessionID, [...(parked.get(job.sessionID) ?? []), entry])
      ensureTimer()
    } else await send(job.sessionID, [entry])
    return true
  }

  /** Hands the session's held-back wake-ups to OpenCode, minus those a Stop disarmed meanwhile. */
  async function flush(sessionID) {
    const entries = parked.get(sessionID)
    if (!entries) return
    parked.delete(sessionID)
    await send(sessionID, entries)
  }

  async function send(sessionID, entries) {
    // A Stop between parking and sending still wins: those go to the stop report.
    const live = entries.filter(({ job }) => !disarmed(job))
    if (!live.length) return
    let report = takeReport(sessionID)
    for (const { job, text } of live) {
      if (!claim(job.dir)) continue // killed or delivered meanwhile
      // Recorded before the request: OpenCode may build the next model request before it answers.
      touch(job.dir, WAKE_FILE, String(Date.now()))
      try {
        const item = await ctx.session.synthetic({
          sessionID,
          text: report ? `${text}\n\n${report.text}` : text,
          description: `Background task ${job.id} finished`,
          metadata: { source: SOURCE, id: job.id },
          delivery: "queue",
        })
        if (item?.id) inboxJobs.set(item.id, job)
        report?.done()
        report = undefined
      } catch {
        // Session gone or server shutting down -- the result stays readable on disk.
        remove(job.dir, WAKE_FILE)
      }
    }
  }

  // --- refusing stale wake-ups ---------------------------------------------------

  /**
   * Wake-ups in a model request's messages. One the model has not seen yet is decided once, by
   * the `context` hook right before the request: still armed, it is accepted for good; disarmed
   * by a Stop since it was handed over (OpenCode had it queued), it is refused for good and left
   * out of this and every later request. Without `decide` (compaction) it is only left out.
   * `stale`: a wake-up refused now with no other input after it, so it alone started the turn.
   */
  function screen(sessionID, messages, decide) {
    let newer = false
    let stale = false
    let accepted
    for (let i = messages.length - 1; i >= 0; i--) {
      const meta = messages[i]?.metadata
      const job = meta?.source === SOURCE && typeof meta.id === "string" ? readMeta(jobDir(sessionID, meta.id)) : undefined
      if (!job) {
        if (messages[i]?.role === "user" && meta?.source !== REPORT_SOURCE) newer = true
        continue
      }
      const undecided = has(job.dir, WAKE_FILE) && !has(job.dir, ACCEPTED_FILE) && !has(job.dir, REFUSED_FILE)
      if (undecided && decide) {
        if (disarmed(job)) {
          touch(job.dir, REFUSED_FILE)
          unclaim(job.dir)
          stale ||= !newer
        } else {
          touch(job.dir, ACCEPTED_FILE)
          if (!newer) accepted = job
        }
      }
      if (has(job.dir, REFUSED_FILE) || (undecided && !decide && disarmed(job))) messages.splice(i, 1)
    }
    return { stale: stale && !accepted, accepted }
  }

  /** Right before every model request: a refused wake-up never reaches the model. */
  async function review(request) {
    const { sessionID, messages } = request
    busy.add(sessionID) // a model request runs: the session's events may still be on their way
    const { stale, accepted } = screen(sessionID, messages, true)
    if (accepted) {
      wakeTurns.set(sessionID, { at: Date.now(), root: accepted.root })
      ensureTimer()
    }
    if (!stale) return
    // Only a refused wake-up started this turn: end it before the model is asked anything.
    const ended = new Promise((resolve) => {
      interruptWaiters.set(sessionID, [...(interruptWaiters.get(sessionID) ?? []), resolve])
      setTimeout(resolve, REFUSE_WAIT_MS).unref?.()
    })
    await interrupt(sessionID)
    await ended
  }

  // --- stop report -------------------------------------------------------------

  /** The model has not seen the job's result: no wake-up yet, or one OpenCode still holds. */
  const unseen = (job) => !has(job.dir, "notified") || (has(job.dir, WAKE_FILE) && !has(job.dir, ACCEPTED_FILE))

  /** Disarmed wait: true jobs of this session (or its subagents) the agent was not told about. */
  function pendingReports(sessionID) {
    const byDir = new Map()
    for (const job of listJobs(sessionID)) byDir.set(job.dir, job)
    for (const job of known.values()) if (job.root === sessionID) byDir.set(job.dir, job)
    return [...byDir.values()].filter((job) => disarmed(job) && unseen(job) && !has(job.dir, REPORTED_FILE))
  }

  /** The stop report for the session's next prompt; call done() once it was delivered. */
  function takeReport(sessionID) {
    const jobs = pendingReports(sessionID)
    if (!jobs.length) return undefined
    return {
      text: renderStopReport(jobs),
      done: () => {
        for (const job of jobs) touch(job.dir, REPORTED_FILE)
      },
    }
  }

  function renderStopReport(jobs) {
    const lines = [
      `<background-tasks-stopped>`,
      `Automated note: the user stopped this session earlier. These background tasks`,
      `were started with wait: true before that stop and will NOT wake you:`,
    ]
    for (const job of jobs) {
      const s = state(job)
      lines.push(
        `- ${job.id} (${job.description || job.command}): ${s.label} after ${duration(job.started, s.at)}, log: ${job.log}`,
      )
    }
    const running = jobs.filter((job) => !state(job).done)
    lines.push(``, `Read a result with background_output(id: "...", full: true).`)
    if (running.length) {
      const job = running[0]
      lines.push(
        `If you still need to wait for a running one, start a new wait: true task that`,
        `waits for it and then end your turn, e.g. for ${job.id}:`,
        `  while kill -0 ${job.pid} 2>/dev/null; do sleep 5; done; tail -n 40 '${job.log}'`,
      )
    }
    lines.push(`</background-tasks-stopped>`)
    return lines.join("\n")
  }

  function renderNotification(job, result) {
    const status =
      result.code === undefined
        ? "vanished without an exit code (host restart?)"
        : result.code === 0
          ? "exited 0 (success)"
          : `exited ${result.code} (failure)`
    return [
      `<background-task id="${job.id}" status="${status}">`,
      `Automated notification: a background task you started has finished. It ran`,
      `independently of your turn.`,
      ``,
      `command: ${job.command}`,
      `duration: ${duration(job.started, result.at)}`,
      `status: ${status}`,
      `full log: ${job.log}`,
      ``,
      tail(job.log),
      `</background-task>`,
      ``,
      `Pick up whatever was waiting on this result. Read the full log with`,
      `background_output(id: "${job.id}") if the tail above is not enough. If nothing`,
      `is left to do, answer with a one line summary.`,
    ].join("\n")
  }

  // --- output rendering ------------------------------------------------------

  function state(job) {
    const exit = readExit(job.dir)
    if (exit) {
      const killed = has(job.dir, "killed")
      const label =
        killed
          ? "killed"
          : exit.code === 0
            ? "exited 0 (success)"
            : `exited ${exit.code} (failure)`
      return { done: true, label, at: exit.at }
    }
    if (alive(job.pid)) return { done: false, label: "running", at: Date.now() }
    return { done: true, label: "vanished without an exit code", at: Date.now() }
  }

  function summary(job) {
    const s = state(job)
    const stopped = disarmed(job) && unseen(job) ? "  [stopped: does not wake you]" : ""
    return `${job.id}  ${s.label}  ${duration(job.started, s.at)}  ${job.description || job.command}${stopped}`
  }

  function readSince(job, full) {
    const cursorFile = path.join(job.dir, "cursor")
    let size = 0
    try {
      size = fs.statSync(job.log).size
    } catch {
      return { text: "", size: 0 }
    }
    let from = 0
    if (!full) {
      const saved = Number(safeRead(cursorFile))
      if (Number.isFinite(saved) && saved <= size) from = saved
    }
    const fd = fs.openSync(job.log, "r")
    try {
      const buf = Buffer.alloc(size - from)
      fs.readSync(fd, buf, 0, buf.length, from)
      fs.writeFileSync(cursorFile, String(size))
      return { text: buf.toString("utf8"), size }
    } finally {
      fs.closeSync(fd)
    }
  }

  function tail(log) {
    let text = ""
    try {
      const size = fs.statSync(log).size
      const from = Math.max(0, size - TAIL_BYTES)
      const fd = fs.openSync(log, "r")
      try {
        const buf = Buffer.alloc(size - from)
        fs.readSync(fd, buf, 0, buf.length, from)
        text = buf.toString("utf8")
      } finally {
        fs.closeSync(fd)
      }
      if (from > 0) text = text.slice(text.indexOf("\n") + 1)
    } catch {
      return "(no output)"
    }
    const lines = text.split("\n")
    const clipped = lines.length > TAIL_LINES
    const body = (clipped ? lines.slice(-TAIL_LINES) : lines).join("\n").trimEnd()
    if (!body) return "(no output)"
    return [clipped ? "output (tail):" : "output:", "```", body, "```"].join("\n")
  }

  // --- startup: adopt jobs from a previous opencode run ----------------------

  function adopt() {
    let sessions = []
    try {
      sessions = fs.readdirSync(STATE_ROOT)
    } catch {
      return
    }
    const now = Date.now()
    for (const sessionID of sessions) {
      for (const job of listJobs(sessionID)) {
        const age = now - (job.started ?? 0)
        if (age > PRUNE_MAX_AGE_MS) {
          fs.rmSync(job.dir, { recursive: true, force: true })
          continue
        }
        if (!job.wait || job.directory !== directory) continue
        if (age > ADOPT_MAX_AGE_MS) continue
        if (has(job.dir, "notified")) continue
        known.set(`${job.sessionID}/${job.id}`, job)
        if (disarmed(job)) continue
        watch(job)
        // After startup, once the server answers; notify() resolves it again before any wake-up.
        if (!job.root) setTimeout(() => resolveRoot(job).catch(() => {}), 5000).unref?.()
      }
    }
  }
  adopt()

  // --- OpenCode events and hooks ---------------------------------------------

  async function onEvent(event) {
    const sessionID = event?.data?.sessionID
    if (!sessionID) return
    switch (event.type) {
      case "session.execution.started":
      case "session.step.started":
        busy.add(sessionID)
        return
      case "session.execution.interrupted": {
        busy.delete(sessionID)
        wakeTurns.delete(sessionID)
        const own = selfInterrupts.get(sessionID)
        selfInterrupts.delete(sessionID)
        // An interrupted execution is a Stop (T3's Stop, the TUI, the interrupt API) unless the
        // plugin ended it itself or the server shut down. It disarms the held-back wake-ups too.
        const reason = event.data.reason
        if (reason === "user" && !(own && Date.now() - own < SELF_INTERRUPT_MS)) markStopped(sessionID)
        for (const resolve of interruptWaiters.get(sessionID) ?? []) resolve()
        interruptWaiters.delete(sessionID)
        if (reason === "shutdown") parked.delete(sessionID) // unclaimed: the next start adopts them again
        else await flush(sessionID) // disarmed ones go to the stop report
        return
      }
      case "session.execution.succeeded":
      case "session.execution.failed":
        busy.delete(sessionID)
        wakeTurns.delete(sessionID)
        await flush(sessionID)
        return
      case "session.inbox.cancelled": {
        // A wake-up cancelled before it ran: report it once it no longer wakes the session.
        const job = inboxJobs.get(event.data.inboxID)
        inboxJobs.delete(event.data.inboxID)
        if (job && disarmed(job) && !has(job.dir, ACCEPTED_FILE)) unclaim(job.dir)
        return
      }
      case "session.inbox.delivered":
        inboxJobs.delete(event.data.inboxID)
        return
    }
  }

  ;(async () => {
    const events = ctx.event.subscribe({
      types: [
        "session.execution.started",
        "session.execution.succeeded",
        "session.execution.failed",
        "session.execution.interrupted",
        "session.step.started",
        "session.inbox.cancelled",
        "session.inbox.delivered",
      ],
      signal: subscription.signal,
    })
    for await (const event of events) await onEvent(event).catch(() => {})
  })().catch(() => {})

  // A model turn that is about to end: held-back wake-ups run in the same execution.
  // OpenCode skips this hook after an interruption, so a Stop never releases them.
  await ctx.session.hook("experimental.idle", (event) => flush(event.sessionID))

  // Every model request: refuse wake-ups a Stop overtook while OpenCode had them queued.
  await ctx.session.hook("context", (event) => review(event))
  // A compaction right after OpenCode delivered a refused wake-up must not summarize it either.
  await ctx.session.hook("compaction", (event) => {
    screen(event.sessionID, event.messages, false)
  })

  // A user prompt: it supersedes any wake-up turn, and it carries the stop report. The report
  // is a synthetic message without a description (hidden in timelines, seen by the model),
  // admitted ahead of the prompt so both are delivered together.
  await ctx.session.hook("prompt", async (event) => {
    const sessionID = event.sessionID
    wakeTurns.delete(sessionID)
    const report = takeReport(sessionID)
    if (!report) return
    try {
      await ctx.session.synthetic({
        sessionID,
        text: report.text,
        metadata: { source: REPORT_SOURCE },
        delivery: "steer",
        resume: false,
      })
      report.done()
    } catch {}
  })

  // --- tools -----------------------------------------------------------------

  await ctx.tool.transform((tools) => {
    tools.add({
      name: "background",
      description: BACKGROUND_DESCRIPTION,
      options: { codemode: false },
      input: input({
        command: { type: "string", description: "Shell command to run in the background" },
        description: {
          type: "string",
          description: "Short label for this task, 3-8 words, e.g. 'run the full test suite'",
        },
        wait: {
          type: "boolean",
          description:
            "true: wake me with the result when it exits (normal choice). false: pure fire and forget, no notification.",
        },
      }),
      async execute(args, context) {
        const command = String(args?.command ?? "").trim()
        if (!command) throw new Error("command is required")
        const wait = args?.wait !== false && args?.wait !== "false"
        const description = String(args?.description ?? "").trim() || command
        await ctx.permission.assert({
          sessionID: context.sessionID,
          agent: context.agent,
          action: "shell",
          resources: [command],
          save: [command],
          metadata: { command, description, background: true },
          source: { type: "tool", messageID: context.messageID, id: context.id },
        })
        const cwd = directory
        const root = wait ? await rootOf(context.sessionID) : context.sessionID
        const job = spawnJob({ command, description, wait, sessionID: context.sessionID, root, cwd })
        return {
          content: [
            `Started background task ${job.id} (pid ${job.pid}). It is still running: this call returned immediately and did not wait for it.`,
            `command: ${command}`,
            `cwd: ${cwd}`,
            `log: ${job.log}`,
            ``,
            ...(wait
              ? [
                  `wait: on -- when it exits, its result arrives here as a new message that resumes this session automatically, even after your turn has ended.`,
                  `What to do now: if you have other work that does not need this result, do it. Otherwise end your turn now -- reply with a one-line status and stop.`,
                  `Do not wait for it yourself: no background_output checks, no sleep, no loop or other blocking command.`,
                ]
              : [`wait: off -- fire and forget. The process keeps running even if opencode exits and nothing will notify you.`]),
            `Inspect it with background_output(id: "${job.id}") only if you need to; stop it with background_kill(id: "${job.id}").`,
          ].join("\n"),
          metadata: { id: job.id, pid: job.pid, log: job.log, wait, description },
        }
      },
    })

    tools.add({
      name: "background_output",
      description: OUTPUT_DESCRIPTION,
      options: { codemode: false },
      input: input({
        id: {
          type: "string",
          description: "Task id, e.g. 'bg_1'. Empty string lists all background tasks of this session.",
        },
        full: {
          type: "boolean",
          description: "true: whole output from the start. false: only output since your last check.",
        },
      }),
      async execute(args, context) {
        const id = String(args?.id ?? "").trim()
        const full = args?.full === true || args?.full === "true"
        const jobs = listJobs(context.sessionID)

        if (!id || id === "all") {
          if (!jobs.length) return { content: "No background tasks in this session." }
          return { content: ["Background tasks in this session:", ...jobs.map(summary)].join("\n") }
        }

        const job = jobs.find((j) => j.id === id)
        if (!job) {
          throw new Error(
            `No background task ${id} in this session. Known: ${jobs.map((j) => j.id).join(", ") || "none"}`,
          )
        }
        const s = state(job)
        const key = `${job.sessionID}/${job.id}`
        const stopped = disarmed(job)
        if (job.wait && !s.done && !stopped && !full) {
          const last = lastCheck.get(key)
          if (last !== undefined && Date.now() - last < POLL_GUARD_MS) {
            return {
              content: [
                `${job.id}: still running after ${duration(job.started, s.at)}. Checked again ${duration(last)} after your last check.`,
                `Its result arrives as a new message that resumes this session when it exits, so there is nothing to poll.`,
                `End your turn now -- reply with a one-line status and stop -- unless you have other work that does not need this result.`,
                `(New output is kept for a later check; full: true shows all of it.)`,
              ].join("\n"),
              metadata: { id: job.id, running: true, status: s.label },
            }
          }
        }
        lastCheck.set(key, Date.now())
        const { text } = readSince(job, full)
        const body = text.trimEnd()
        return {
          content: [
            `${job.id}: ${s.label} after ${duration(job.started, s.at)}`,
            `command: ${job.command}`,
            `log: ${job.log}`,
            ...(job.wait && stopped && !s.done ? [`Stopped by the user: this task keeps running but no longer wakes you.`] : []),
            ``,
            body
              ? [full ? "output (from the start):" : "output (new since your last check):", "```", body, "```"].join("\n")
              : full
                ? "(no output)"
                : "(no new output since your last check)",
          ].join("\n"),
          metadata: { id: job.id, running: !s.done, status: s.label },
        }
      },
    })

    tools.add({
      name: "background_kill",
      description: KILL_DESCRIPTION,
      options: { codemode: false },
      input: input({ id: { type: "string", description: "Task id to kill, e.g. 'bg_1'" } }),
      async execute(args, context) {
        const id = String(args?.id ?? "").trim()
        const job = listJobs(context.sessionID).find((j) => j.id === id)
        if (!job) throw new Error(`No background task ${id} in this session.`)
        if (readExit(job.dir)) return { content: `${job.id} had already finished: ${state(job).label}`, metadata: { id: job.id } }

        touch(job.dir, "killed")
        claim(job.dir) // a task we killed on purpose must not wake us up later
        watching.delete(`${job.sessionID}/${job.id}`)
        lastCheck.delete(`${job.sessionID}/${job.id}`)
        kill(job.pid, "SIGTERM")
        for (let i = 0; i < 20 && alive(job.pid); i++) await sleep(100)
        if (alive(job.pid)) kill(job.pid, "SIGKILL")
        if (!readExit(job.dir)) fs.writeFileSync(path.join(job.dir, "exit"), "143")
        return {
          content: [`Killed background task ${job.id} (${job.command}).`, `log: ${job.log}`].join("\n"),
          metadata: { id: job.id },
        }
      },
    })
  })

  return () => {
    subscription.abort()
    if (timer) clearInterval(timer)
    timer = undefined
  }
}

// --- helpers -----------------------------------------------------------------

function kill(pid, signal) {
  try {
    process.kill(-pid, signal) // whole process group, so children die too
  } catch {
    try {
      process.kill(pid, signal)
    } catch {}
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

function safeRead(file) {
  try {
    return fs.readFileSync(file, "utf8")
  } catch {
    return ""
  }
}

function duration(from, to = Date.now()) {
  const s = Math.max(0, Math.round((to - from) / 1000))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m${s % 60}s`
  return `${Math.floor(m / 60)}h${m % 60}m`
}
