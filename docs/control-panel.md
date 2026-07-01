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

The **Coding agents** module lists Claude Code, Codex, and Opencode with their live
versions. Update checks are best-effort and cached: a header banner appears when Construct
itself is behind its git ref (**Update Construct** re-downloads in place), and a per-agent
badge marks an available update (**update all** force-updates them over SSH, then re-probes).

## Lifecycle

The **Lifecycle** actions each launch a host console window (never the VM's terminal):

- **Reprovision** — re-run setup, keep all data.
- **Export config** — save auth, credentials, and profiles.
- **Redownload** / **Reinstall** — rebuild the VM (fresh ISO, or current ISO). These delete
  and recreate the VM, so they launch elevated and always stop in the console for the
  dirty-repo warning and the "type yes" delete confirmation. **Settings → Custom
  reinstall** offers a one-time save-and-restore / restore-existing / clean-wipe choice.

## Projects

The **Projects** module manages the project profiles in `<scripts>\projects\*.json`:

- **+ add project** — paste a git URL; it's cloned into `/root/repos` on the VM (safely,
  never through the shell) and opened in a new Remote-SSH window.
- **import from VM** — scans the repos already checked out on the VM and writes a minimal
  profile for each one not already covered (it never overwrites an existing profile).
- Click a chip to **edit** its profile in a modal — repos, SDKs (`node`/`python`/…), MCP
  servers (raw JSON, validated before save), host packages, and provision commands. The
  inline **▷** on a chip opens that project's folder on the VM in a new window.
- **select profiles** — tick which profiles are active. The selection is recorded (in
  `.construct-settings.json`) for the **next** Reprovision / Reinstall; it does not
  re-provision a running VM.

## Token usage & cost

The **Token usage & cost** module runs [ccusage](https://github.com/ryoppippi/ccusage) over
SSH and shows a per-agent breakdown — a share bar, exact token counts, and an **estimated**
cost — plus a total. (Token counts are exact; cost is an estimate from ccusage's model
pricing.) It's a slower round-trip, so it fills in a moment after the rest of the status.
**export json** saves the full raw usage document to a file you pick. The first run can be
slow if ccusage installs itself on the VM.

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

The panel is honest about the patch and the recorder: the "chat mic button" line reflects
whether the guard patch actually applied, and if no recorder or no capture device is found
you get a one-time warning (never silent-but-broken). On a Claude Code build the patch
doesn't recognise, it says so rather than claiming the button is unlocked. `/voice` in the
terminal works regardless of the button.

**Windows: picking the right microphone.** ffmpeg's DirectShow capture needs an exact
device name — the panel auto-detects the first one, but if that's the wrong input, list your
devices with `ffmpeg -list_devices true -f dshow -i dummy` and set **`construct.micDevice`**
(in VS Code settings) to the device name you want (e.g. `Microphone (Realtek(R) Audio)`).

## Settings

**Settings** pre-fills the installer's prompts (git identity, VM resources, services like
serve-web / tunnel / SMB) so your next Reprovision / Reinstall runs with your saved choices.
The agent password is never stored — it's entered in the elevated console at reinstall time.
See [Project profiles & configuration](projects.md) and [Provisioning](provisioning.md) for
what each setting maps to.
