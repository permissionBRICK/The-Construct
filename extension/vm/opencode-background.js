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
// With wait: true the plugin watches the job and, once it exits, prompts the
// session again -- which wakes the agent even if its turn ended long ago.
//
// Stop disarms: when the user stops the session (an aborted turn, or the
// `stopped` marker that Construct's T3 patch writes for a Stop after the turn
// had already settled), every wait: true task started before that moment
// stops waking it. The task keeps running and its result stays on disk; the
// next prompt to the session tells the agent which tasks were disarmed.
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
// Per-task marker: the agent has been told that this task no longer wakes it.
const REPORTED_FILE = "reported"
// A second background_output on a still-armed running task within this window
// is answered with a reminder instead of output.
const POLL_GUARD_MS = 60 * 1000
const MAX_PARENT_DEPTH = 8

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

/** Plain-JSON-Schema args: opencode marks every declared arg as required. */
export const BackgroundTasks = async ({ client, directory }) => {
  /** key `sessionID/id` -> job, jobs we still owe a notification for */
  const watching = new Map()
  /** key `sessionID/id` -> job, every wait: true job of this process (for stop reports) */
  const known = new Map()
  /** sessionID -> [{ job, text }], notifications parked until the session goes idle */
  const parked = new Map()
  /** key `sessionID/id` -> time background_output last returned output for it */
  const lastCheck = new Map()
  /** sessionID -> parent sessionID, or null for a root session */
  const parents = new Map()
  let timer

  // --- job state on disk -----------------------------------------------------

  const sessionDir = (sessionID) => path.join(STATE_ROOT, sessionID)
  const jobDir = (sessionID, id) => path.join(sessionDir(sessionID), id)

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

  /** A wait: true job started before a Stop of its session or root session never wakes it. */
  function disarmed(job) {
    if (!job.wait) return false
    const root = job.root && job.root !== job.sessionID ? stoppedAt(job.root) : 0
    return Math.max(stoppedAt(job.sessionID), root) >= job.started
  }

  /** The root of a (subagent) session's parent chain, or undefined while the server cannot say. */
  async function rootOf(sessionID) {
    let id = sessionID
    for (let depth = 0; depth < MAX_PARENT_DEPTH; depth++) {
      let parent = parents.get(id)
      if (parent === undefined) {
        try {
          const res = await client.session.get({ path: { id }, query: { directory } })
          if (res?.error || !(res?.data ?? res)?.id) return undefined
          parent = (res?.data ?? res).parentID ?? null
        } catch {
          return undefined
        }
        parents.set(id, parent)
      }
      if (!parent) return id
      id = parent
    }
    return id
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
    if (!timer) {
      timer = setInterval(poll, POLL_MS)
      timer.unref?.()
    }
  }

  async function poll() {
    for (const [key, job] of watching) {
      // Stopped: stop watching. The result stays on disk and is reported on the next prompt.
      if (disarmed(job)) {
        watching.delete(key)
        continue
      }
      const result = outcome(job)
      if (!result) continue
      watching.delete(key)
      await notify(job, result)
    }
    if (!watching.size && timer) {
      clearInterval(timer)
      timer = undefined
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

  function unclaim(dir) {
    fs.rmSync(path.join(dir, "notified"), { force: true })
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

  async function notify(job, result) {
    await resolveRoot(job)
    if (disarmed(job)) return
    if (!claim(job.dir)) return
    const entry = { job, text: renderNotification(job, result) }
    if (await isIdle(job.sessionID)) return send(job.sessionID, [entry])
    parked.set(job.sessionID, [...(parked.get(job.sessionID) ?? []), entry])
  }

  async function isIdle(sessionID) {
    try {
      const res = await client.session.status({})
      const status = (res?.data ?? res)?.[sessionID]
      return !status || status.type === "idle"
    } catch {
      return true
    }
  }

  async function send(sessionID, entries) {
    // A Stop between parking and sending still wins: hand those back to the stop report.
    const live = entries.filter(({ job }) => {
      if (!disarmed(job)) return true
      unclaim(job.dir)
      return false
    })
    if (!live.length) return
    try {
      await client.session.promptAsync({
        path: { id: sessionID },
        query: { directory },
        body: { parts: [{ type: "text", text: live.map((entry) => entry.text).join("\n\n") }] },
      })
    } catch {
      // Session gone or server shutting down -- the result stays readable on disk.
      return
    }
    // A Stop that landed while the wake-up was in flight: end the turn it just started.
    if (live.some(({ job }) => disarmed(job))) {
      try {
        await client.session.abort({ path: { id: sessionID }, query: { directory } })
      } catch {}
    }
  }

  // --- stop report -------------------------------------------------------------

  /** Disarmed wait: true jobs of this session (or its subagents) the agent was not told about. */
  function pendingReports(sessionID) {
    const byDir = new Map()
    for (const job of listJobs(sessionID)) byDir.set(job.dir, job)
    for (const job of known.values()) if (job.root === sessionID) byDir.set(job.dir, job)
    return [...byDir.values()].filter(
      (job) =>
        disarmed(job) &&
        !fs.existsSync(path.join(job.dir, "notified")) &&
        !fs.existsSync(path.join(job.dir, REPORTED_FILE)),
    )
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
      const killed = fs.existsSync(path.join(job.dir, "killed"))
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
    const stopped = disarmed(job) && !fs.existsSync(path.join(job.dir, "notified")) ? "  [stopped: does not wake you]" : ""
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
        if (fs.existsSync(path.join(job.dir, "notified"))) continue
        known.set(`${job.sessionID}/${job.id}`, job)
        if (disarmed(job)) continue
        watch(job)
        // After startup, once the server answers; notify() resolves it again before any wake-up.
        if (!job.root) setTimeout(() => resolveRoot(job).catch(() => {}), 5000).unref?.()
      }
    }
  }
  adopt()

  // --- tools -----------------------------------------------------------------

  return {
    event: async ({ event }) => {
      const sessionID = event?.properties?.sessionID
      if (!sessionID) return
      // An aborted turn is a Stop (T3's Stop, the TUI's Esc, the abort API). It is
      // published before the session goes idle, so parked wake-ups are disarmed in time.
      if (event.type === "session.error") {
        if (event.properties.error?.name === "MessageAbortedError") markStopped(sessionID)
        return
      }
      if (event.type !== "session.idle") return
      const entries = parked.get(sessionID)
      if (!entries?.length) return
      parked.delete(sessionID)
      await send(sessionID, entries)
    },

    // The next prompt after a Stop (the user's, or another task's wake-up) tells the
    // agent which tasks no longer wake it. Synthetic: the model sees it, T3 hides it.
    "chat.message": async (input, output) => {
      const sessionID = input?.sessionID
      if (!sessionID || !Array.isArray(output?.parts)) return
      const jobs = pendingReports(sessionID)
      if (!jobs.length) return
      output.parts.push({
        id: partID(),
        sessionID,
        messageID: output.message?.id ?? output.parts[0]?.messageID,
        type: "text",
        synthetic: true,
        text: renderStopReport(jobs),
      })
      for (const job of jobs) {
        try {
          fs.writeFileSync(path.join(job.dir, REPORTED_FILE), "")
        } catch {}
      }
    },

    dispose: async () => {
      if (timer) clearInterval(timer)
    },

    tool: {
      background: {
        description: BACKGROUND_DESCRIPTION,
        args: {
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
        },
        async execute(args, ctx) {
          const command = String(args?.command ?? "").trim()
          if (!command) throw new Error("command is required")
          const wait = args?.wait !== false && args?.wait !== "false"
          const description = String(args?.description ?? "").trim() || command
          await ctx.ask({
            permission: "bash",
            patterns: [command],
            always: [command],
            metadata: { command, description, background: true },
          })
          const cwd = ctx.directory || directory
          const root = wait ? await rootOf(ctx.sessionID) : ctx.sessionID
          const job = spawnJob({ command, description, wait, sessionID: ctx.sessionID, root, cwd })
          ctx.metadata({ title: `${description} (${job.id})`, metadata: { id: job.id, pid: job.pid } })
          return {
            title: `${description} (${job.id})`,
            output: [
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
            metadata: { id: job.id, pid: job.pid, log: job.log, wait },
          }
        },
      },

      background_output: {
        description: OUTPUT_DESCRIPTION,
        args: {
          id: {
            type: "string",
            description: "Task id, e.g. 'bg_1'. Empty string lists all background tasks of this session.",
          },
          full: {
            type: "boolean",
            description: "true: whole output from the start. false: only output since your last check.",
          },
        },
        async execute(args, ctx) {
          const id = String(args?.id ?? "").trim()
          const full = args?.full === true || args?.full === "true"
          const jobs = listJobs(ctx.sessionID)

          if (!id || id === "all") {
            if (!jobs.length) return "No background tasks in this session."
            return {
              title: `${jobs.length} background task(s)`,
              output: ["Background tasks in this session:", ...jobs.map(summary)].join("\n"),
            }
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
                title: `${job.description || job.command} (${job.id})`,
                output: [
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
            title: `${job.description || job.command} (${job.id})`,
            output: [
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
      },

      background_kill: {
        description: KILL_DESCRIPTION,
        args: { id: { type: "string", description: "Task id to kill, e.g. 'bg_1'" } },
        async execute(args, ctx) {
          const id = String(args?.id ?? "").trim()
          const job = listJobs(ctx.sessionID).find((j) => j.id === id)
          if (!job) throw new Error(`No background task ${id} in this session.`)
          if (readExit(job.dir)) return `${job.id} had already finished: ${state(job).label}`

          fs.writeFileSync(path.join(job.dir, "killed"), "")
          claim(job.dir) // a task we killed on purpose must not wake us up later
          watching.delete(`${job.sessionID}/${job.id}`)
          lastCheck.delete(`${job.sessionID}/${job.id}`)
          kill(job.pid, "SIGTERM")
          for (let i = 0; i < 20 && alive(job.pid); i++) await sleep(100)
          if (alive(job.pid)) kill(job.pid, "SIGKILL")
          if (!readExit(job.dir)) fs.writeFileSync(path.join(job.dir, "exit"), "143")
          return {
            title: `killed ${job.id}`,
            output: [`Killed background task ${job.id} (${job.command}).`, `log: ${job.log}`].join("\n"),
          }
        },
      },
    },
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

// OpenCode's ascending part id layout: "prt_", 12 hex digits of (ms * 4096 + counter),
// 14 base62 characters. One millisecond ahead so the note sorts after the prompt's parts.
const BASE62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
function partID() {
  const time = (BigInt(Date.now() + 1) * 0x1000n).toString(16).padStart(12, "0").slice(-12)
  let rest = ""
  for (let i = 0; i < 14; i++) rest += BASE62[Math.floor(Math.random() * 62)]
  return `prt_${time}${rest}`
}

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
