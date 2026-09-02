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

## Which VM the panel drives (instances)

**With one VM there is nothing here to do.** The panel drives the default instance,
`agent-vm`, no picker or status-bar item appears, and every screen below is the screen it
has always been. The rest of this section only applies once a second VM exists — see
[Installation § Several VMs](installation.md#several-vms-and-vms-on-another-host) for the
registry that defines them.

With **two or more** instances in `%LOCALAPPDATA%\The-Construct\instances.json`:

- A **dropdown** appears at the top of the panel and a **status-bar item** (`$(vm) <name>`)
  on the left of the status bar. Either one switches; so does **The Construct: Switch
  Instance** from the command palette, which lists each instance with its endpoint and
  backend and ticks the current one.
- The choice is **per window** and persists across reloads, so two windows can drive two
  VMs at once. Setting **`construct.instance`** in VS Code settings *pins* every window to
  one instance instead — the panel says so rather than letting a switch silently not take
  effect.
- A window attached to a known instance's VM over Remote-SSH **adopts** that instance on
  activation, so the panel describes the machine you are actually working on rather than
  whichever one was selected last.

Switching retargets everything that holds a per-VM connection or cache in one sequence: the
in-flight status probe, the usage table, the git/config-sync state and the cached idle
policy are dropped (serving the previous VM's numbers under the new name would be a lie),
the notification watcher reconnects to the new
VM's spool, the [forwarder](#forwards-construct-expose) hands back its claim and re-opens
the new VM's tunnels, and **microphone passthrough is torn down and re-armed against the
destination** — evaluated from *that* VM's saved preference, so a VM whose mic preference is
on gets its tunnel even if the instance you left never had one. Rapid A→B→C switching is
serialized through one chain, so B can neither leave a tunnel behind nor win over C.

If the registry itself has a problem — an entry that does not load, two entries claiming one
identity — the panel toasts it once and writes the detail to its log; it never guesses.

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
| Installed but stopped (the backend reports it off) | **▶ Start & connect** | Elevated `Start-VM` (one UAC prompt) for a local VM, a power call to the host service for a remote one; waits for SSH, then opens it |
| Suspended by the [idle policy](#idle-policy-remote-vms) (`saved`) | **▶ Resume & connect** | The same action, named for what actually happens: the VM's RAM is on disk and starting it resumes it transparently |
| Reachable | **⏻ Shutdown** | `systemctl poweroff` over SSH (confirms first) |

Connecting needs the `ms-vscode-remote.remote-ssh` extension and the VM's SSH host alias —
`agent-vm` for the default VM, the instance's own name for any other (both set up by the
installer/provisioner). When you first connect, the dashboard opens alongside automatically.

For a VM that isn't on this PC's Hyper-V, the System module also names the **backend** and
the **host service** that owns it. Both rows stay hidden for `hyperv-local`, so a
single-VM install's card is exactly the card it always was.

## Forwards (`construct expose`)

When an agent on the VM runs [`construct expose <port>`](expose.md), the extension is what
actually opens that port on **your** PC: it spawns a long-lived `ssh -N -L` process of its
own to the active instance, one per forward. The **Forwards** card is where you see them.
It stays hidden entirely until
there is something to show (no forwards *and* a local instance), so an install that never
runs `expose` sees the panel it always saw.

Each row shows the VM port (as `vm:5173`, or `vm:5173→18800` when the port had to be
remapped because the one asked for was busy on your PC), whether the forward is a **client**
forward (opened here) or a **host** forward (published by the host service on its LAN
address), its label, and its state — `queued`, `open` or `error`. **▷** opens the link and
is disabled until there is one; **✕** closes the forward.

- **A stopped VM does not lose the forward, but the link does go down.** The card keeps
  rendering when the VM stops answering (the request is what persists, and it is not on the
  VM's side of anything the extension forgets), but the `ssh` process dies with the
  connection, so the local port stops listening. The extension notices and retries on the
  **same** local port with a backoff — the number the guest was already told — so the link
  comes back by itself once the VM does. After enough failed attempts the row goes `error`
  and says so, while the retries continue at the slowest cadence.
- **Several windows, one server — on a local VM.** A local VM's forward requests live in a
  spool on the VM itself, and exactly one window claims it: the others show the same list
  read-only, say "served by another window", and have **✕** disabled (a non-owner deleting
  the owner's spool documents would tear down a forward the owner still believes it is
  serving). The links work in every window — it is the same PC. Closing the serving window
  releases the claim and the next window picks the queued forwards straight back up.
- **On a remote VM there is no spool and no claim.** The host service holds the forward
  state, so every window lists every forward and **✕** is always enabled — the ownership row
  never says "served by another window". The tunnels still do not multiply **on this PC**: a
  window that finds a forward already acked will only take it over if the exact local port
  the guest was promised is still free here. Busy means another window on this machine is
  already serving it, so it is left alone — re-opening it on a different port would break a
  link somebody already holds. (The check is per machine, so a *second PC* signed in to the
  same host will open its own tunnel for the same forward — which is what you want: the link
  has to work on that machine too.)
- By default the tunnel listens on **loopback only** and the link is
  `http://localhost:<port>/`. Setting **`construct.forwards.hostLabel`** both puts that name
  in the link and makes the forward listen on all of that PC's interfaces — otherwise it
  would advertise an address only this PC could open. Changing the setting restarts the live
  tunnels, on the same ports.
- **`construct.forwards.enabled`** switches the whole thing off; requests then simply stay
  queued on the VM, which is the same answer as "no VS Code attached".

## Idle policy (remote VMs)

A VM on a [host service](remote-host.md) consumes that host's RAM whether or not anyone is
using it, so the service enforces a per-VM idle policy — and the **Idle policy** card is
where you set yours. It is hidden for a local VM: there is no always-on service on your own
PC, so there would be nothing to enforce it.

Pick a timeout in minutes and what should happen — **save** (the default: Hyper-V writes the
VM's RAM to disk and frees it; any start resumes it transparently), **shutdown**, or **off**
(never idle out) — and press **apply**. If your admin set a cap, the box carries it as its
maximum and the hint says so, so the number you see is the number that takes effect rather
than one that silently changes after the round trip. A background refresh never overwrites a
half-typed value.

A VM counts as idle only when **both** signals agree for the whole window: no live
connections through any of its forwards, *and* no in-guest activity reported by the VM's
heartbeat. **An agent running a long unattended job keeps the VM alive with nobody
connected** — that is the entire point of unattended agents.

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

For a **non-default instance** the panel checks three things before it launches anything,
and refuses with the reason rather than running an action that would hit the wrong machine:

- **The backend must support it.** Reinstall, Redownload and applying automatic checkpoints
  go through the host's PowerShell scripts, so they are gated on the driver's
  `hostLifecycle` capability; checkpoints additionally need the `checkpoints` capability,
  which `hyperv-remote` does not have (the message says the backend has no checkpoints,
  not something vaguer). Reprovision and Export config are pure SSH to a running VM and
  work on every backend.
- **A remote instance needs a host service** recorded in its registry entry — without one
  there is nothing to ask for a new VM, and an `Auto-Install.ps1` run with no `-ServiceUrl`
  would fall back to the *local* path.
- **The installed scripts must accept the targeting parameters.** If this PC's Construct is
  older than the instance work (no `-VmName` / `-InstanceName` / `-ConfigBranch`), the
  action would silently run against the default VM or split one VM's config across two
  branches — so the panel blocks it and tells you to update the scripts.

The default instance is never blocked by any of this: it needs no targeting in the first
place.

### Remote hosts

Two command-palette entries handle VMs on somebody else's Hyper-V (full story:
[Remote host](remote-host.md)):

- **The Construct: Add Remote Host** — service URL → the certificate fingerprint shown once
  for you to confirm → authentication (Windows/Kerberos first, then a token or domain
  credentials) → `whoami`. The host is remembered in the extension's `globalState`; a token
  goes into VS Code **SecretStorage**. Nothing is written to `instances.json` until a VM
  exists on that host, because the registry describes *VMs*. The confirmed fingerprint is
  written to the same pin file the PowerShell client reads, so a host trusted here is
  already trusted in a console (the token is deliberately *not* shared — each store keeps
  its own).
- **The Construct: New VM on Remote Host** — pick a known host, answer name / CPU / RAM /
  disk, and the command launches `Auto-Install.ps1`'s remote path in a host console through
  the same launcher every other lifecycle action uses. The console does the create *and* the
  provisioning, because provisioning configures your PC too.

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
- **Switching instances re-arms it.** The tunnel terminates on one VM, so switching the
  window to another instance tears it down and evaluates *that* VM's own saved preference —
  you don't have to flip the switch again on the VM you moved to, and the VM you left is not
  holding a tunnel this window no longer drives.
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

The notification is filed under **The Construct**, which the extension registers for your
user on first use (no admin, no installer) — you'll find it in *Settings ▸ System ▸
Notifications* if you ever want to mute or un-mute it. Until Windows has seen a toast from
that identity it sometimes refuses to recognise it; in that case the toast still appears,
just labelled *Windows PowerShell*, and the Construct output channel says why.

If Windows genuinely can't show it — notifications switched off for your user or by group
policy, a locked-down PowerShell, or a non-Windows host — it falls back to a VS Code
notification so the message is never lost, and the exact reason is written to the Construct
output channel. Nothing flashes on screen either way: the toast is raised by a PowerShell
that runs with no console at all.

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
name (`<VM name> - (<timestamp>)`, e.g. `Agent-VM - (…)`) — and because a checkpoint *you* created could be named
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
.\Set-AgentVmCheckpoints.ps1 -Enabled false -VmName build-vm        # another local VM
```

It defaults to `-VmName Agent-VM -Backend hyperv-local`, and refuses to run when the
backend reports no checkpoint capability rather than reconfiguring some *local* VM that
happens to share the name.

### OpenCode background watcher

The **Patch OpenCode with background watcher** toggle is a separate, off-by-default
feature. It installs a Construct-managed OpenCode plugin that provides three tools:
`background` starts a detached process, `background_output` reads its captured output,
and `background_kill` stops it. A `background` call with `wait: true` can wake the
originating OpenCode session when the process finishes, which also supplies the stable
signals used by Construct's optional T3 monitoring patch.

This feature contains only that dependency-free watcher plugin. It does **not** install
the source archive's Cortecs request hook, provider settings, model-fallback changes, or
repository symlinks. Enabling or disabling it is applied by the next reprovision; saving a
changed setting shows a prompt with a **Reprovision now** action. Turning
it off removes only a plugin bearing Construct's ownership marker, so an unrelated local
`background.js` is never overwritten or deleted. The preference is also carried through
reprovision and rebuilds as `OPENCODE_BACKGROUND_WATCHER`.

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

**HTTPS.** Construct serves the web GUI over TLS as well: the VM runs nginx on port
`5178` (`T3CODE_HTTPS_PORT`) with a certificate issued by a **local CA** it creates once,
and reverse-proxies to the unchanged `t3 serve` on `5177` — which stays plain HTTP for
local tooling. The https origin is the one the panel shows and the ▷ button opens, because
browsers only expose the microphone (`getUserMedia`) on a **secure origin**: over plain
`http://<vm>.mshome.net:5177` T3's in-browser voice input is not available at all. The
Windows Desktop app is unaffected — it loads its UI from a scheme registered as secure.

Provisioning copies the CA certificate to
`%LOCALAPPDATA%\The-Construct\artifacts\t3code\construct-t3-ca.crt` and trusts it for you:
elevated it lands in the **machine** Root store silently, and when the run is *not*
elevated it goes into your **user** Root store — where Windows shows **one** confirmation
dialog you have to accept. A CA that is already trusted is never imported twice. Firefox
keeps its own trust store, so set `security.enterprise_roots.enabled` to `true` in
`about:config` to make it use the Windows one. On the VM the CA lives in
`/etc/construct/tls/ca.crt` (private keys stay in that `0700` directory; a readable copy
for the host handoff sits at `/etc/construct/t3code-ca.crt`), and it is part of the
[config backup](backup-restore.md) — so a reinstall does not ask you to trust a new CA.
Set `T3CODE_HTTPS=false` (see [provisioning](provisioning.md)) to remove the proxy; the CA
is kept either way.

The **channel** dropdown next to the toggle selects between **stable** (npm `@latest` —
the released version) and **nightly** (npm `@nightly` — the latest CI build, which may
include newer features but can break). Switching the channel on an already-enabled T3 Code
reinstalls it at the new tag and restarts the service live, without requiring a reprovision.
The choice is persisted in `config.env` (`T3CODE_CHANNEL`), rides reprovision/reinstall, and
survives a backup/restore cycle. On the agent card, a nightly install is annotated so it's
visually distinguishable from stable.

### Patched T3 Code server + Desktop build

The **Build patched T3 Code + Desktop** toggle (off by default) makes Construct resolve the
selected npm channel to its exact upstream Git tag, apply one guarded source patch, and build
both the VM server/web client and an unsigned Windows x64 Desktop installer from that same
checkout. The build toolchain (Node/pnpm, Rust/MinGW, Wine, and Electron Builder) stays in the
VM. The finished installer is copied to
`%LOCALAPPDATA%\The-Construct\artifacts\t3code\` and silently installed or updated on
Windows as part of provisioning. There is no installation prompt and provisioning does not
launch the app afterward. Construct keys the shared build by the resolved upstream T3 version,
the installed Construct revision, and the guarded patch recipe. Routine reprovisions reuse the
running VM server and Desktop artifact without rebuilding, reinstalling, or restarting T3; a T3
update or Construct update invalidates that cache. An already-current Desktop installation is
left alone. Activating a genuinely new build restarts `t3code-serve`, so an open T3 provider
session may ask you to send a new message afterward; unchanged reprovisions do not interrupt it.

The shared patch adds:

- **Voice input:** a mic button immediately left of Send in both draft/new and existing chat
  composers, plus **Ctrl+T** tap/hold behavior. While recording, a live ring around the button
  expands with the microphone signal level. The selected environment streams raw 16 kHz
  mono PCM through Construct's existing host microphone reverse tunnel into Claude's speech
  endpoint. Partial transcripts replace only their own live span at the cursor captured when
  recording started; existing prefix/suffix text is never replaced, and typing while recording
  stops the stream before another partial can write. Enable **Microphone passthrough** as well
  and keep the Construct VS Code extension running so it owns the host capture/tunnel.

- **Claude usage-limit recovery:** when Claude rejects a turn for an account limit —
  including SDK results wrapped as `subtype: success` with `is_error: true` — T3 **parks** the
  thread and automatically dispatches a continuation once the limit resets, plus a
  one-minute margin. The park is sent through T3's native snooze lifecycle, so existing
  clients move it into the **Snoozed** shelf and show their normal wake countdown/banner;
  the continuation wakes it without requiring a Windows client update. Parked threads are
  persisted (`$T3CODE_HOME/userdata/t3park-pending.json`, default
  `~/.t3/userdata/`), so they survive a service restart; the resume re-uses the thread's
  own model/runtime settings and is skipped if you already continued the thread manually.

- **OpenCode background monitoring:** a completed `background` tool call armed with
  `wait: true` is projected as a T3 `local_bash` task, so the thread pill stays
  `monitoring` after the foreground turn settles. The plugin's later
  `<background-task ...>` wake-up prompt, or a completed `background_kill`, closes that
  task and clears the monitoring state.

This is a **reprovision-only** toggle: saving a change persists it and shows a prompt with
a **Reprovision now** action. The reprovision selects the patched-source or stock install and
restarts `t3code-serve`. The preference also rides reinstall. For compatibility with existing machines,
its internal config key remains `T3CODE_LIMIT_RESUME`; the UI and behavior now cover the whole
source-build feature set. If a newer stable/nightly source tag changes a guarded patch anchor,
the new build is refused and the prior working T3 installation is left in place. The established
usage-limit/OpenCode bundle transforms are applied to the freshly built server before both the
VM install and Desktop packaging. The auto-resume dispatch authenticates with its own long-lived
T3 API token (`/etc/construct/t3park-token`, minted on enable).

In the Construct-built Desktop app, the normal update control launches `Update-T3Code.ps1`,
which reruns Construct reprovisioning with the saved settings. It does not install an upstream
T3 binary over the patched build. The next installer is again built in the VM and silently
applied on the host without launching the app. The Construct panel's T3 update action and
stable/nightly changes follow that same reprovision path while the shared source build is
enabled, so neither end is replaced by a stock npm update.

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
