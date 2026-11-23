# The VS Code extension: how it is built

The extension is a thin client (plan `docs/plans/construct-platform-design.md` S3, 11.4). It owns
no state and runs no job; every action is the device agent's, through the agent's page.

| File | Role |
|---|---|
| `extension.js` | VS Code wiring: the view (`pop`) and the panel (`win`), commands, the bridge, Remote-SSH, the register offer |
| `src/agent.js` | The agent's directory and token file, the health check, start, install, one loopback request helper |
| `src/page.js` | The webview documents (a status page, or the frame onto the agent) and the check of the page's messages |
| `src/workspace.js` | The register-workspace decision: SSH alias, checkout, `origin`, profiles, the new profile |
| `media/icon.svg` | The activity-bar icon |
| `src/projects.js`, `vm/` | Not the extension's: the VM's profile validator and the guest scripts (see README) |

## The frame and the token

A surface's document is either a status page or a frame onto `http://127.0.0.1:<port>`. Its CSP
allows frames from that origin only, the frame is sandboxed (`allow-scripts allow-same-origin
allow-forms`: no top navigation, no popups) and starts without `src`. When the document reports
ready, the extension posts the address with `#agent-token=...`; the script navigates the frame
there and keeps nothing. The page takes the token out of its URL at once. The token is never in
the document's text, an attribute, `setState`, a command argument or a log.

A route (`#/hosts`) is sent after the page's first load, as a same-document navigation. Every ten
seconds an open surface checks the token file: a restarted agent is reloaded, a stopped one gets a
Start button.

## The bridge

The agent's page may post to its parent. The document passes a message on only when it comes from
the frame and the agent's origin; the extension then accepts `openWindow {route}` (open the panel
at a route), `openVsCode {engine, vm}` and `close` (ignored), each checked by `page.pageMessage`.

## Where it runs

`extensionKind: ["ui"]`: on the user's PC, also in a Remote-SSH window. Where VS Code runs it in a
remote extension host anyway, or inside a Construct VM (`/etc/construct/config.env`), it reads no
agent file and shows a note.
