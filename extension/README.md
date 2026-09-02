# The Construct — control panel

A VS Code extension that turns the agent VM into a one-screen operator console:
lifecycle (reprovision / reinstall / redownload / export), update checks for
Construct itself and the coding agents, project profiles, token usage & cost,
**microphone passthrough** so voice input works over Remote-SSH, the ports
`construct expose` opened on this PC, and — when there is more than one VM — an
instance picker that retargets all of it, local or remote.

## How it runs

It is a **UI extension** (`extensionKind: "ui"`), so it executes on your local
machine even while the window is attached to the VM over Remote-SSH. That single
vantage point lets it drive both sides:

- the **host** — the PowerShell lifecycle scripts in `%LOCALAPPDATA%\The-Construct`,
  the local microphone, and the local ports it opens for `construct expose`;
- the **VM** — status, versions and usage gathered over SSH, using the active
  instance's alias/key/port (`agent-vm` by default).

Which VM that is comes from the **instance registry**
(`%LOCALAPPDATA%\The-Construct\instances.json`, read by `src/instances.js` and its
PowerShell twin `lib/AgentVm.Instances.ps1`). A missing registry means exactly one
implicit instance, `agent-vm`, with today's literals — so a single-VM install sees no
picker, no status-bar item and no change of any kind.

The activity-bar icon opens a compact **launcher** in the sidebar — live status,
three quick lifecycle actions (Reprovision / Redownload / Reinstall), and an **Open
Control Panel** button. The full panel (settings, usage, projects, all lifecycle)
opens on demand as a wide **editor tab** via that button or the `The Construct: Open
Control Panel` command, where the two-column layout has room to breathe. The tab is
restored across window reloads.

## Install

No build step — it is plain JavaScript. The installer packages this folder into a
`.vsix` (`Build-ControlPanelVsix`, no vsce/Node) and installs it with
`code --install-extension`; VS Code loads it on next launch. (A bare folder copied into
`.vscode\extensions` isn't loaded by current VS Code — it must be a registered install.)
To develop locally, open this folder in VS Code and press F5.

## Layout

| Path | Role |
| --- | --- |
| `package.json` | manifest: activity-bar container, webview view, commands, settings |
| `extension.js` | activation, launcher + panel wiring, serializer, host/VM bridges, message router, instance switching |
| `media/launcher.html` · `launcher.js` | the sidebar launcher (status + quick actions) |
| `media/panel.html` · `panel.js` | the full control panel (editor tab), incl. the project edit modal, the Forwards and Idle-policy cards |
| `media/panel.css` · `themes/` · `theme-previews/` | the base stylesheet plus one stylesheet + thumbnail per UI design |
| `media/icon.svg` | activity-bar icon |
| `src/instances.js` | the instance registry: load/validate/derive, the active-instance resolution, the mic handover chain. PowerShell twin: `lib/AgentVm.Instances.ps1` |
| `src/ssh.js` · `probe.js` | SSH runner (per-instance host/alias/key/port) + the live status/version probe |
| `src/drivers/` · `vmpower.js` | backend dispatch (`hyperv-local`, `hyperv-remote`, a degrading fallback) + the panel's power/state entry point |
| `src/remotehost.js` | the `constructd` API client: three credential providers, cert pinning, SecretStorage tokens |
| `src/host.js` · `lifecycle.js` | host scripts-dir + settings + project profiles; lifecycle launchers and their capability/version gates |
| `src/updates.js` · `remote.js` | update checks; Remote-SSH open + add/open project |
| `src/projects.js` · `configsync.js` · `importui.js` · `zip.js` | Projects import/select/edit; the git config-sync engine; the rename-on-collision import core; the dependency-free zip writer |
| `src/usage.js` | ccusage over SSH → per-agent tokens + estimated cost |
| `src/audio.js` (`makeHostMicProvider`) | mic-passthrough orchestrator + native host mic capture (ffmpeg/sox `rec` → raw 16 kHz mono S16LE PCM on stdout) — a webview can't reach the mic (VS Code's webview iframe Permissions-Policy omits `microphone`) |
| `src/forwarder.js` · `forwarder-ui.js` | the client port forwarder for `construct expose` — a pure core with injected transport, plus the one adapter that touches processes, sockets and the vscode API |
| `src/notify.js` · `repatch.js` · `themes.js` | VM→host desktop notifications; startup re-verification of the Claude Code patches; the UI-design registry |
| `vm/*.sh` | the rec/arecord shim, the audio enable/disable scripts and the read-only patch probe pushed to the VM |
| `test/*.test.js` · `ui-smoke.js` | plain-node unit suites + the Playwright webview smoke test |

The webview talks to the extension over `postMessage`; the message contract lives at
the top of `extension.js` (and in `ARCHITECTURE.md`). Run the tests with
`node test/<name>.test.js` (and `NODE_PATH=<playwright> node test/ui-smoke.js`).
