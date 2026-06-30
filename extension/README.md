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

No build step — it is plain JavaScript. Provisioning copies this folder into
`%USERPROFILE%\.vscode\extensions\` on the host; VS Code loads it on next launch.
To develop locally, open this folder in VS Code and press F5.

## Layout

| Path | Role |
| --- | --- |
| `package.json` | manifest: activity-bar container, webview view, commands |
| `extension.js` | activation, launcher + panel wiring, serializer, host/VM bridges |
| `media/launcher.html` · `launcher.js` | the sidebar launcher (status + quick actions) |
| `media/panel.html` · `panel.js` | the full control panel (editor tab) |
| `media/panel.css` | Matrix theme shared by both surfaces |
| `media/icon.svg` | activity-bar icon |

The webview talks to the extension over `postMessage`; the message contract lives at
the top of `extension.js`.
