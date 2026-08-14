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

const BACKGROUND_DESCRIPTION = `Run a shell command in the background, detached from this tool call.

The command is spawned in its own process group and keeps running after the tool
returns, after your turn ends, and after opencode exits. Nothing kills it on a
timeout. Use it for anything slow or open ended: test suites, builds, installs,
dev servers, watchers, log tails, long migrations.

wait: true (the normal choice)
  You are notified in this session as soon as the command exits, even if your
  turn has already ended -- the result wakes you back up. Start the job, keep
  working or end your turn; do NOT sit in a polling loop waiting for it.

wait: false
  True fire and forget. Nobody wakes you. The process outlives opencode
  entirely. Use it for things you never need the result of (a dev server you
  just want running, a detached deploy). You can still inspect it later with
  background_output.

Use the normal shell/bash tool instead when the command is quick and you need
its result right now.`

const OUTPUT_DESCRIPTION = `Check a background task started with the background tool: its status and its output.

By default you get only output that appeared since your last check on that task,
so you can call this repeatedly on a running job (a dev server, a watcher)
without re-reading everything.

Pass an empty id to list every background task of this session with its status.
Every task also has a plain log file on disk (path is reported), so you can grep
or tail it with the normal shell tool.`

const KILL_DESCRIPTION = `Stop a background task started with the background tool.

Terminates the whole process group (SIGTERM, then SIGKILL if it does not exit).
A killed task never sends a wake-up notification -- you already know it is gone.`

/** Plain-JSON-Schema args: opencode marks every declared arg as required. */
export const BackgroundTasks = async ({ client, directory }) => {
  /** key `sessionID/id` -> job, jobs we still owe a notification for */
  const watching = new Map()
  /** sessionID -> [text], notifications parked until the session goes idle */
  const parked = new Map()
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
    let n = fs.readdirSync(base).length + 1
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

  function spawnJob({ command, description, wait, sessionID, cwd }) {
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
    watching.set(`${job.sessionID}/${job.id}`, job)
    if (!timer) {
      timer = setInterval(poll, POLL_MS)
      timer.unref?.()
    }
  }

  async function poll() {
    for (const [key, job] of watching) {
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

  async function notify(job, result) {
    if (!claim(job.dir)) return
    const text = renderNotification(job, result)
    if (await isIdle(job.sessionID)) return send(job.sessionID, text)
    parked.set(job.sessionID, [...(parked.get(job.sessionID) ?? []), text])
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

  async function send(sessionID, text) {
    try {
      await client.session.promptAsync({
        path: { id: sessionID },
        query: { directory },
        body: { parts: [{ type: "text", text }] },
      })
    } catch {
      // Session gone or server shutting down -- the result stays readable on disk.
    }
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
    return `${job.id}  ${s.label}  ${duration(job.started, s.at)}  ${job.description || job.command}`
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
        watch(job)
      }
    }
  }
  adopt()

  // --- tools -----------------------------------------------------------------

  return {
    event: async ({ event }) => {
      if (event.type !== "session.idle") return
      const sessionID = event.properties.sessionID
      const texts = parked.get(sessionID)
      if (!texts?.length) return
      parked.delete(sessionID)
      await send(sessionID, texts.join("\n\n"))
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
          const job = spawnJob({ command, description, wait, sessionID: ctx.sessionID, cwd })
          ctx.metadata({ title: `${description} (${job.id})`, metadata: { id: job.id, pid: job.pid } })
          return {
            title: `${description} (${job.id})`,
            output: [
              `Started background task ${job.id} (pid ${job.pid}).`,
              `command: ${command}`,
              `cwd: ${cwd}`,
              `log: ${job.log}`,
              ``,
              wait
                ? `wait: on -- you will be notified in this session when it exits, even after your turn has ended. Do not poll in a loop; carry on or end your turn, the result will wake you.`
                : `wait: off -- fire and forget. The process keeps running even if opencode exits and nothing will notify you.`,
              `Check on it any time with background_output(id: "${job.id}"), stop it with background_kill(id: "${job.id}").`,
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
          const { text } = readSince(job, full)
          const body = text.trimEnd()
          return {
            title: `${job.description || job.command} (${job.id})`,
            output: [
              `${job.id}: ${s.label} after ${duration(job.started, s.at)}`,
              `command: ${job.command}`,
              `log: ${job.log}`,
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
