# Control panel

The Construct ships a VS Code extension — **The Construct** in the activity bar — that
turns the agent VM into a one-screen operator console. It runs on your **local machine**
(a UI extension), so it can reach both sides at once: the Windows **host** (the PowerShell
lifecycle scripts and your microphone) and the **VM** (status, versions, usage over SSH).

The installer packages it into a `.vsix` and installs it with `code --install-extension`
(and sets up VS Code + the Remote-SSH extension for you), so it's registered and loads on
the next VS Code start. "Update Construct" re-downloads and reinstalls the panel. (No build
step — it's plain JavaScript; to hack on it, open `extension/` and press F5.)

## Two surfaces

- **Sidebar launcher** — click the Construct icon in the activity bar for a compact view:
  live status, the three quick lifecycle actions, the power buttons, and an **Open Control
  Panel** button.
- **Full panel** — that button (or the `The Construct: Open Control Panel` command) opens
  the wide **editor tab** with everything: system status, agents, projects, usage, voice,
  and settings. It's restored across window reloads.

Both read the same live state, pushed from the extension as the VM is probed over SSH.

## Choosing a look (UI designs)

The panels ship in **VS Code Native** by default — nothing is asked on first launch. Three
designs are available:

- **VS Code Native** (default) — looks like a built-in VS Code panel and follows your
  editor color theme automatically, light or dark.
- **Classic Matrix** — the original green operator console.
- **Terminal, Refined** — the same identity with a disciplined phosphor palette; glow is
  reserved for genuinely live things (the online dot, an armed mic, a busy action).

Every design is the **same panel** — identical features, buttons, and behavior; only the
stylesheet changes. Switch anytime with **The Construct: Choose UI Design** from the
command palette (a tab of preview cards), or set `construct.uiTheme` in VS Code settings;
open panels restyle in place. Closing the picker without choosing keeps the current look.

## Status & connecting

The header shows whether the VM is **online**, its hostname, and install/reprovision
timestamps; the System module lists the Hyper-V VM name, RAM/disk, and Ubuntu release. One
power control shows at a time, driven by the VM's real state:

| State | Button | What it does |
| --- | --- | --- |
| Reachable, this window not on the VM | **→ Open on VM** | Opens `/root/repos` over Remote-SSH in the current window |
| Installed but stopped (Hyper-V reports it off) | **▶ Start & connect** | Elevated `Start-VM` (one UAC prompt), waits for SSH, then opens it |
| Reachable | **⏻ Shutdown** | `systemctl poweroff` over SSH (confirms first) |

Connecting needs the `ms-vscode-remote.remote-ssh` extension and the `agent-vm` SSH host
alias (both set up by the installer). When you first connect, the dashboard opens alongside
automatically.

## Coding agents & updates

The **Coding agents** module lists Claude Code, Codex, and Opencode — plus T3 Code when its
web GUI is enabled (see [Settings](#t3-code-web-gui)) — with their live versions. Agents
with a browser UI (T3 Code) get an inline **▷** button that opens the web UI on your
machine, logged in via a freshly minted one-time pairing link. Update checks are best-effort and cached: a header banner appears when Construct
itself is behind its git ref (**Update Construct** re-downloads the extension + scripts in
place and records the new version, so the banner clears once it's done), and a per-agent
badge marks an available update (**update all** force-updates them over SSH, then re-probes).

Construct tracks two versions separately: the **installed** Construct (extension + scripts,
bumped by install / Update Construct) and the version the **VM was last provisioned with**.
When the VM is behind the installed Construct, the **Reprovision** button turns **yellow**
("update pending") — reprovision to apply the update to the VM. It clears once you reprovision.

## Lifecycle

The **Lifecycle** actions each launch a host console window (never the VM's terminal):

- **Reprovision** — re-run setup, keep all data.
- **Redownload** / **Reinstall** — rebuild the VM (fresh ISO, or current ISO). These delete
  and recreate the VM, so they launch elevated and always stop in the console for the
  dirty-repo warning and the "type yes" delete confirmation. **Settings → Custom
  reinstall** offers a one-time save-and-restore / restore-existing / clean-wipe choice.

## Projects

The **Projects** module manages project profiles, versioned on the host in a dedicated
config directory (`%LOCALAPPDATA%\The-Construct\config`, git-versioned when git is present)
and kept in sync with the VM's live store (`/opt/construct/projects`) by the
[config-sync](config-sync.md) engine. The panel's open-folder button opens the `projects/`
subdirectory of that config directory.

- **export config** — save auth, credentials, and profiles (launches a host console window).
- **+ add project** — paste a git URL; it's cloned into `/root/repos` on the VM (safely,
  never through the shell) and opened in a new Remote-SSH window.
- **auto-import** — repos checked out on the VM are discovered automatically by the
  config-sync tick and imported as profiles (never overwrites an existing profile).
  Click **sync now** in the Config sync strip to trigger discovery immediately.
- Click a chip to **edit** its profile in a modal — repos, SDKs (`node`/`python`/…), MCP
  servers (raw JSON, validated before save), host packages, and provision commands. The
  inline **▷** on a chip opens that project's folder on the VM in a new window. The
  **default** chip shows a lock icon and refuses the edit modal — it's a reserved,
  read-only seed; customize it by creating a named profile instead.
- **select profiles** — tick which profiles are active. The selection is recorded (in
  `.construct-settings.json`) for the **next** Reprovision / Reinstall; it does not
  re-provision a running VM. Profile *edits* made on the VM, though, reach the host
  automatically between reprovisions via the sync tick below — you don't need to reprovision
  just to pick up a VM-side change.

### Sync status

A status strip shows the current [config-sync](config-sync.md) state:

- **Sync now** — runs an immediate tick instead of waiting for the next automatic one.
- **Conflict banner** — appears when the host and VM diverged in a way git couldn't
  auto-merge. **Open config repo** opens the config directory in a new VS Code window, where
  the built-in merge editor handles the conflict like any other git merge; sync pauses and
  resumes automatically once you resolve and commit.
- **Install git notice** — shown instead, persistently, when git isn't present on the host.
  Config sync and versioning are **disabled** until git is installed: the panel's tick does
  nothing without it, so the config repo, merging, and remote-config features stay off. (A
  reinstall/reprovision from PowerShell still preserves profiles through the additive
  backup/restore fallback — see [Project profiles](projects.md#degraded-mode-no-git-on-the-host)
  — but that path is separate from this panel.) A one-click button runs the git installer for you.

### Remote config repos

A separate section links the Projects tab to shared, upstream config repos (a company git
host with baseline configs for several projects):

- **Add / remove a remote** — link or unlink a repo by URL. Linked remotes are fetched into
  a disposable local cache in the background.
- **Import** — a picker lists the config files found across all linked repos, grouped by
  repo, **none ticked by default**; importing pulls the selected files in, merges them with
  anything already tracked from the same repo/path, and runs them through the same
  validation as any other profile write.
- **Share** — pick profiles to hand to someone else. A selection backed by a linked remote
  is shared as the [install one-liner](installation.md#sharing-a-config-as-a-one-liner)
  (command carrier); a selection containing local-only profiles is shared as a zip bundle
  instead (a small `deploy.ps1` plus the profile files).
- **Push back** — manually push your local versions of a remote's tracked files to a branch
  on that remote for review; never automatic, since shared config affects other people's VMs.

## Token usage & cost

The **Token usage & cost** module runs [ccusage](https://github.com/ryoppippi/ccusage) over
SSH and shows a per-agent breakdown — a share bar, exact token counts, and an **estimated**
cost — plus a total. (Token counts are exact; cost is an estimate from ccusage's model
pricing.) It's a slower round-trip, so it fills in a moment after the rest of the status.

Use the **daily / monthly / total** tabs to switch the window: **daily** shows usage so far
today, **monthly** shows usage this calendar month, and **total** shows all-time lifetime
usage (all per agent). Daily is the default. (Daily and monthly naturally coincide on the 1st
of the month; total is always distinct.) An agent with no usage in the selected window is
simply left out of the table. (We deliberately don't offer a *weekly* view — `ccusage`
doesn't support a weekly report for Codex, which would drop Codex from the table.)

**export json** saves the full raw usage document — scoped to the current tab — to a file you
pick. The first run can be slow if ccusage installs itself on the VM.

## Microphone passthrough (voice input)

Claude Code's speech-to-text is disabled over Remote-SSH by default. The **Voice input**
toggle re-enables it by streaming your **local** microphone to the VM on demand:

- Enabling installs a small `rec`/`arecord` shim on the VM, applies a **reversible** patch
  that lifts only the remote speech gate in the installed Claude Code extension, and opens
  an `ssh -R` reverse tunnel from the VM back to your host mic.
- Your **local** microphone is captured by a native recorder the panel runs on your host
  (**ffmpeg**, or `sox` `rec` as a fallback). A VS Code webview can't reach the microphone,
  so this is done by the extension's host process instead. The installer sets ffmpeg up for
  you (`winget install Gyan.FFmpeg`); if it isn't found, install it and restart VS Code.
- The mic is opened **only while you're actually recording** (the VM shim connects when
  Claude records and disconnects when it stops) — it is never hot continuously.
- Disabling removes the shim and reverts the patch. Turning it off (or closing VS Code)
  releases the mic and tears down the tunnel.
- **Multiple VS Code windows work at the same time.** Each window gets its own tunnel
  port (8767–8774), so voice input works in every window attached to the VM — not just the
  first one. The shared VM-side pieces (shim + patch) are only removed when the **last**
  window's passthrough turns off.
- **One persistent toggle + auto-arm.** The main **Voice input** switch and the **Settings →
  Microphone passthrough** toggle are the *same* setting. Turning it on **persists** it, so
  passthrough arms itself **automatically on startup** (as soon as the VM is reachable) — you
  don't have to flip it each session. Startup arming is silent: if the VM is down it just
  stays off; flip the console switch to see any error.
- **Seeing the chat mic button.** The first time you enable passthrough in a session, the
  already-running Claude Code still has its pre-patch code loaded, so the chat mic button
  won't appear until the window reloads. The panel offers a **Reload window** button for
  exactly this — after the reload the button is there and passthrough re-arms automatically.
  (On later sessions where it auto-armed at startup, the button is already present.)

The panel is honest about the patch and the recorder: the "chat mic button" line reflects
whether the guard patch actually applied, and if no recorder or no capture device is found
you get a one-time warning (never silent-but-broken). On a Claude Code build the patch
doesn't recognise, it says so rather than claiming the button is unlocked. `/voice` in the
terminal works regardless of the button.

**Windows: picking the right microphone.** ffmpeg's DirectShow capture needs an exact
device name — the panel auto-detects the first one, but if that's the wrong input, list your
devices with `ffmpeg -list_devices true -f dshow -i dummy` and set **`construct.micDevice`**
(in VS Code settings) to the device name you want (e.g. `Microphone (Realtek(R) Audio)`).

## Desktop notifications from the VM

An agent working on the VM can get your attention on the Windows desktop:

```bash
construct notify "Test suite finished — 3 failures" --title "audiobook-pilot"
construct notify "Deploy failed, rolling back" --level error   # info | warning | error
```

You get a **real Windows notification** — the kind that slides in from the tray and stays
in the notification centre — not a VS Code toast buried in a window you're not looking at.
Clicking it opens the control panel. `warning` and `error` stay on screen longer; nothing
is ever shown twice, even with several VS Code windows open.

It works **without the panel or sidebar ever being opened**: the extension activates with
VS Code and opens **one long-lived SSH connection** that waits on the VM and streams
notifications as they're queued — no polling, so delivery is immediate (milliseconds) and an
idle connection costs nothing. If it drops (VM rebooted, laptop slept, Wi-Fi switched) it
reconnects on its own, backing off from 2 s to a minute, and delivers whatever queued in the
meantime on reconnect. Turn the whole thing off with **`construct.notifications`**.

Messages queued while VS Code was closed arrive at next start — apart from stale ones (older
than an hour) and anything queued before a VM reboot, which are dropped rather than replayed
at you.

If Windows can't show the notification — notifications switched off for the app, a policy
blocking PowerShell, or a non-Windows host — it falls back to a VS Code notification so the
message is never lost. Nothing flashes on screen either way: the toast is raised by a
PowerShell that runs with no console at all.

The channel is deliberately **one-way**. An agent can tell you something; it cannot ask you
anything or get an answer back — questions belong in the agent's own chat. Notifications
are also rate-limited (50 queued at most) so a looping agent can't bury you in popups, and
their text is sanitized before display.

## Keeping the Claude Code patches applied across updates

Construct applies two reversible patches to the VM's Claude Code extension —
**partial-message streaming** (so the chat panel streams over Remote-SSH instead of
freezing until each turn finishes) and the **microphone gate** above. These go on at
provision time, but VS Code **auto-updates the Claude Code extension in the background**,
and a fresh build arrives un-patched — so after such an update the features silently
regress until the next reprovision.

To avoid that, the panel re-checks the patches on every start: about **20 seconds after
it activates** (enough for a start-time auto-update to land) it probes the VM read-only
and **re-applies any patch whose feature is on but has reverted to stock**. It's silent —
recorded to the **Construct** output channel, no toasts — and best-effort (a powered-off
VM is skipped). A re-applied patch takes effect for the *current* window after a reload,
and is already in place on the next start. Tune the delay with
**`construct.repatchDelaySeconds`** (default `20`; set `0` to disable the check — patches
are still applied on provision).

## Settings

**Settings** pre-fills the installer's prompts (git identity, VM resources, services like
serve-web / tunnel / SMB) so your next Reprovision / Reinstall runs with your saved choices.
The agent password is never stored — it's entered in the elevated console at reinstall time.
See [Project profiles & configuration](projects.md) and [Provisioning](provisioning.md) for
what each setting maps to.

### Automatic checkpoints

Hyper-V's **automatic checkpoints** snapshot a VM every time it starts. That's a sensible
default for a pet VM and a pure cost for a disposable agent VM: the checkpoint pins a
differencing disk (`.avhdx`) that grows with every write, slows disk I/O, and has to be
merged back whenever it's deleted. Construct therefore **creates VMs with automatic
checkpoints off**, and the **Settings → VM resources → Automatic checkpoints** toggle
(off by default) controls it.

The saved value is applied whenever the VM is (re)created — every Reinstall / Redownload
picks it up with no extra step. Because it's a Hyper-V property, it can't be changed by a
Reprovision, so **saving the setting also offers to apply it to the VM you have right now**:
answer **Apply now** and an elevated console (one UAC prompt) runs
`Set-AgentVmCheckpoints.ps1`, which flips the policy and — when turning it *off* — removes
the automatic checkpoint Hyper-V already took, so its disk gets merged back.

Checkpoints you made yourself are never deleted *automatically*. The script only removes
checkpoints Hyper-V positively reports as automatic (its `IsAutomaticSnapshot` flag). On an
older host that doesn't report the flag, it falls back to matching Hyper-V's auto-generated
name (`Agent-VM - (<timestamp>)`) — and because a checkpoint *you* created could be named
that way too, each one is shown separately in the console and removed only if you type `yes`
for it. Answer anything else and it's kept.

Choosing **Later** is fine: the preference is saved either way and takes effect on the next
rebuild — and saving again still offers to apply it while the VM disagrees. The panel
compares your setting against the VM's *actual* Hyper-V policy, not against what the
settings file used to say, which is what makes the toggle work on a VM created before
Construct started disabling checkpoints: its policy is on, the saved preference is off, so
the first save offers to fix it. Reading that policy needs Hyper-V access (the installer
adds you to **Hyper-V Administrators**, effective at your next sign-in); until then the
panel falls back to remembering whether an apply has ever *succeeded*, so the offer keeps
appearing on each save until one does — choosing **Later** or cancelling the UAC prompt
doesn't count.

You can also run it by hand:

```powershell
.\Set-AgentVmCheckpoints.ps1 -Enabled false             # off + clean up the existing one
.\Set-AgentVmCheckpoints.ps1 -Enabled true              # back to Hyper-V's default
.\Set-AgentVmCheckpoints.ps1 -Enabled false -RemoveExisting false   # policy only
```

### T3 Code web GUI

The **T3 Code web GUI** toggle (off by default) opts the VM into
[T3 Code](https://github.com/pingdotgg/t3code) — a browser control plane that drives the
installed coding agents through their existing CLI auth. It's a **live** toggle like the mic
switch: flipping it on installs T3 Code on the VM over SSH right away, starts the
`t3code-serve` service (port `5177`), and opens the web UI in your browser via a one-time
pairing link; flipping it off stops the service. The choice is also persisted, so
provisioning honours it — a reinstall reinstalls T3 Code, and its settings, chat threads,
and auth/pairing state are part of the [config backup](backup-restore.md), so paired
browsers keep working across a reinstall. While enabled, T3 Code appears in the **Coding
agents** list with its version, an update badge, and a **▷** button that mints a fresh
pairing link and opens the web UI.

The **channel** dropdown next to the toggle selects between **stable** (npm `@latest` —
the released version) and **nightly** (npm `@nightly` — the latest CI build, which may
include newer features but can break). Switching the channel on an already-enabled T3 Code
reinstalls it at the new tag and restarts the service live, without requiring a reprovision.
The choice is persisted in `config.env` (`T3CODE_CHANNEL`), rides reprovision/reinstall, and
survives a backup/restore cycle. On the agent card, a nightly install is annotated so it's
visually distinguishable from stable.

## Troubleshooting

If a lifecycle action doesn't behave as expected:

- **Logs.** The **logs** button in the sidebar (or **The Construct: Show Logs** from the
  command palette) opens the **Construct** Output channel, which records each action with the
  exact host command it launched, the resolved script path, the arguments, and the result —
  also written to `%TEMP%\construct-panel.log`. This is the first place to look (and the
  easiest thing to copy when reporting an issue).
- **Keep consoles open.** Turn on **`construct.debug`** in VS Code settings to launch the host
  PowerShell consoles with `-NoExit` so they stay open instead of closing — any error stays on
  screen to read. Turn it back off for normal use.
