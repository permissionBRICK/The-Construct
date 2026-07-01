# The Construct — control panel

A VS Code extension that turns the agent VM into a one-screen operator console:
lifecycle (reprovision / reinstall / redownload / export), update checks for
Construct itself and the coding agents, project profiles, token usage & cost, and
**microphone passthrough** so voice input works over Remote-SSH.

## How it runs

It is a **UI extension** (`extensionKind: "ui"`), so it executes on your local
machine even while the window is attached to the VM over Remote-SSH. That single
vantage point lets it drive both sides:

- the **host** — the PowerShell lifecycle scripts in `%LOCALAPPDATA%\The-Construct`,
  and the local microphone;
- the **VM** — status, versions and usage gathered over SSH (`agent-vm`).

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
| `package.json` | manifest: activity-bar container, webview view, commands |
| `extension.js` | activation, launcher + panel wiring, serializer, host/VM bridges, message router |
| `media/launcher.html` · `launcher.js` | the sidebar launcher (status + quick actions) |
| `media/panel.html` · `panel.js` | the full control panel (editor tab), incl. the project edit modal |
| `media/panel.css` | Matrix theme shared by both surfaces |
| `media/audio.html` · `audio-capture.js` · `audio-worklet.js` | hidden mic-capture webview (getUserMedia → 16 kHz mono PCM) |
| `media/icon.svg` | activity-bar icon |
| `src/ssh.js` · `probe.js` | SSH runner + the live status/version probe |
| `src/host.js` · `lifecycle.js` | host scripts-dir + settings + project profiles; lifecycle launchers |
| `src/updates.js` · `remote.js` · `vmpower.js` | update checks; Remote-SSH open + add/open project; Hyper-V power |
| `src/projects.js` · `usage.js` · `audio.js` | Projects import/select/edit; ccusage usage+cost; mic-passthrough orchestrator |
| `vm/*.sh` | the rec/arecord shim + audio enable/disable scripts pushed to the VM |
| `test/*.test.js` · `ui-smoke.js` | plain-node unit suites + the Playwright webview smoke test |

The webview talks to the extension over `postMessage`; the message contract lives at
the top of `extension.js` (and in `ARCHITECTURE.md`). Run the tests with
`node test/<name>.test.js` (and `NODE_PATH=<playwright> node test/ui-smoke.js`).
