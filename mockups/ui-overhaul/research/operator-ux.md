# Solo-operator UX research: The Construct (Companion popup + control panel)

## 1. Top 8 jobs-to-be-done, most frequent first

| # | Job | What they need right then |
|---|---|---|
| 1 | "Is my VM up, and is the agent still working or done?" | Online state plus per-agent activity (running, idle, waiting on you, last notify). Today there's only the tray color and version strings, with no activity signal at all. |
| 2 | Open the thing the agent just served (`construct expose`) | The forward's link, its state (queued/open/error), and the remap (`vm:5173→18800`). This should be one click from the tray. |
| 3 | Get into the VM: Remote-SSH, T3 web UI, browser console | One primary "Open" button whose target is remembered (VS Code / T3 / console) |
| 4 | Start or resume a stopped VM, then connect | The single power action, plus a warning that it costs a UAC prompt |
| 5 | Act on "behind host · reprovision" or "Construct update available" | Why it matters (commit pair, what changed), and whether a reprovision is safe now (agent busy?) |
| 6 | Toggle the mic for `/voice` | An armed/live indicator and a toggle, without opening the panel |
| 7 | Check today's token spend | Today's total cost, with a per-agent split on demand |
| 8 | Switch VM / manage projects for the next reprovision | Instance list with each VM's health and behind-state, and which profiles are selected for the next rebuild |

Weekly or rare: update agents, resize RAM/vCPU, config sync conflicts, child VMs, remote config repos, custom reinstall, remove instance, "Make this PC a host."

## 2. Pain points in the current layout

- **Destructive actions have the same weight as routine ones.** The popup (`launcher.html`, 420×650) shows Reprovision, Reinstall and Redownload side by side, just like the panel's Lifecycle `action-grid`. Reinstall deletes the VM, and it sits one misclick from the tray. The console's "type yes" guard is the only brake.
- **The panel is one long page of equal-weight modules.** In `panel.html` the order is System → Forwards → Child VMs → Voice → Coding agents → Projects → Config sync → Remote config repos → Token usage. Forwards, the most-used interactive card, has the same visual rank as Remote config repos. Token usage, which a daily operator glances at, sits last.
- **There's no agent-activity view.** The product exists to run unattended agents, yet the panel shows agent versions, not agent state. `construct notify` toasts go to the Windows notification centre, and the panel keeps no inbox or history of them.
- **Settings mixes live controls with reprovision-time config.** "Access & services *applied on reprovision*" sits next to "Save & restart to apply" (RAM) and the mic "saved preference." Users can't tell what takes effect now and what needs a reprovision. Custom reinstall and Remove instance live under that same scroll.
- **Behind-state only makes sense once you've learned the model.** "behind host · reprovision" shows a commit pair and focuses a yellow button. It doesn't say what the user loses by waiting, and it doesn't say whether an agent is mid-task.
- **Instance switching hides the state of the other VMs.** The dropdown shows names only. The "(2 to reprovision)" count exists only in the VS Code status bar, and the Companion never shows it.
- **Features are spread across three surfaces.** Forwards and mic live in the tray right-click menu, the popup and the panel. Each shows a different subset, so there's no single place that's authoritative at a glance.
- **Hidden-when-empty cards make the layout jump.** Forwards and Child VMs appear only once they have content, so the page reflows at exactly the moment an agent exposes a port.

## 3. Three layout concepts

### A. "Pulse": glanceable status first
- **Hierarchy:** a VM health line, then live links, then the one next action. Everything else sits behind tabs.
- **First screen (popup):** a header row per VM with a color dot, name, uptime and today's spend (for example $4.12). Under the active VM:
  - a "Live" strip with forward chips (`:5173 open ▷`)
  - a mic pill
  - one context-aware primary button: Open in VS Code, Start & connect, or Resume
- **Demoted:** versions, Ubuntu release, RAM/disk (shown only above 90%), and the lifecycle trio, which moves into a "⋯ Maintenance" drawer.
- **Signature interaction:** the **Next-action slot**. It shows the single most useful action for the current state, such as "Reprovision (2 commits behind, agent idle)" or "Resume (saved by idle policy)." When an agent is busy it waits, showing "Reprovision when Claude finishes ⏲."

### B. "Watchtower": agent activity at the center
- **Hierarchy:** agents, then their outputs (notifications, forwards, cost), then the machine.
- **First screen:** one card per agent (Claude Code, Codex, OpenCode, T3). Each shows its state (working / idle / last turn N min ago), repo or project, today's tokens and cost, and any forwards it opened. Below the cards is a **notify inbox** that keeps `construct notify` messages with their level and time. Clicking a message jumps to its agent.
- **Demoted:** VM hardware, projects and settings move to a "Machine" tab. Updating agents becomes a per-card badge.
- **Signature interaction:** a **"Wake me when done" bell** on each agent card. It arms a toast for when that agent goes idle. No agent-side `notify` call is needed.

### C. "Operator line": keyboard first, timeline plus palette
- **Hierarchy:** a timeline of events (started, provisioned @commit, expose :5173, notify "tests failed", idle-saved), with a command bar on top.
- **First screen:** the command bar plus the last ~8 events for the active VM. Each event has an inline action (▷ open, ✕ close forward, retry).
- **Demoted:** every module becomes a palette verb: `mic on`, `switch dev-2`, `ram 16`, `usage month`, `export config`.
- **Signature interaction:** a **global hotkey** (for example Win+Alt+C) opens the palette over any app. Typing `5173` opens that forward, and `rep` stages a reprovision. Destructive verbs (`reinstall`, `remove`) require typing the instance name.

## 4. What belongs at each depth

**Glanceable in under 1 second (tray icon plus popup, first view):**
- online/off/saved/error, for every VM and not just the active one
- whether an agent is working or idle
- whether any forward is open, with its link
- mic armed/live
- behind-host or update-available as a badge, not a banner
- today's cost
- disk above 90%

**One click deep (panel tabs or drawers):**
- Reprovision
- agent versions and update-all
- usage daily/monthly/total and export
- project profile selection and editing
- config sync status and "Sync now"
- the notify history
- idle policy
- VM resources (restart to apply)
- child VMs
- T3 and VS Code serve-web toggles

Each of these should be tagged **live** or **next reprovision** so the "when does this apply" question goes away.

**Rare or dangerous (behind a separate "Danger zone" and never in the popup):**
- Reinstall, Redownload
- Custom reinstall (clean wipe)
- Remove instance
- Make this PC a host
- conflict resolution for remote config repos

Give each one:
- an impact preview, reusing what the panel already computes: dirty or unpushed repos, the backup it will use, the targets from the removal plan
- a typed-name confirmation, with the consequence written out ("VM deleted, repos without remote lost")

The main change is to take Reinstall and Redownload out of the 420×650 popup completely.

Sources I read: `/root/repos/construct/extension/media/launcher.html` (this is the popup, per `WebViewDocument.Surface`), `/root/repos/construct/extension/media/panel.html`, `/root/repos/construct/docs/control-panel.md`, `/root/repos/construct/docs/companion.md`, `/root/repos/construct/companion/src/Construct.Companion.Core/Desktop/TrayModel.cs`.