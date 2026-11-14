# Construct client UI

One web app for every client surface: the desktop agent's tray pop-out and window, a browser or phone
served by a host, and the VS Code webview. Preact and TypeScript, built with Vite into a static bundle.

Two implementations of the same `Backend` interface serve it. `ApiBackend` talks to the device agent
(`agent/`) and, through the agent's proxy, to every engine the user works with; the bundle uses it when
the agent opens it with its token in the fragment (`/#agent-token=...`, see `agent/README.md`). The
token is taken out of the address at once, kept for the tab in `sessionStorage` and sent only as a
header. Without a token the bundle runs on `FakeBackend`, an in-memory backend with invented data, as
the preview and the screenshot tests do.

## Layout

| Path | Content |
|---|---|
| `src/backend/types.ts` | The `Backend` interface: a `State` snapshot, `subscribe`, and every action the screens use |
| `src/backend/fake.ts`, `sample.ts` | `FakeBackend`: simulated jobs, failures with the repair agent, vault requests, updates, pairing |
| `src/backend/api/` | `ApiBackend` (`backend.ts`), the mapping from API responses to `State` (`map.ts`), the agent's event stream (`events.ts`) |
| `src/backend/generated/` | TypeScript types generated from `agent/openapi/agent.v1.json` and `service/openapi/constructd.v1.json`; not edited by hand |
| `src/model/` | Logic without UI: VM state, the Reprovision/Update action, the update plan, when settings apply, routes, design tokens |
| `src/ui/` | Components: `kit.tsx` (small parts), `blocks.tsx` (stack blocks), `shells.tsx` (pop-out, window, phone, lone vault card), `pages/` |
| `src/styles/app.css` | Plain CSS on the tokens |
| `preview.html` | A frame showing the app on the fake backend at each surface's size, with demo triggers |

## Commands

```sh
npm install
npm run dev            # development server
npm test               # unit tests (vitest)
npm run build          # static bundle in dist/
npm run test:screens   # screenshot tests (Playwright, headless Chromium), after a build
npm run gen:api        # regenerate src/backend/generated after an OpenAPI document changed
npm run test:e2e       # after a build: the real engine (fake mode) and two device agents serve the bundle
```

`npm test` fails when the generated types differ from what the two OpenAPI documents give. The
end-to-end test (`tests/e2e/`) builds `tests/e2e/engine-host`, a console host that runs the engine as
the agent's tests do, with a scripted SSH guest so provision and reinstall jobs run to their end (it answers
`construct backup` through the engine's backup routes), and the agent; it drives the first run with the host's
fingerprint, creating a VM with its settings (the engine provisions it; the page sends no provision), a setting
changed and the dot on Reprovision, a reprovision, a failed reprovision retried through the engine's retry route
(the scripted guest fails once when the test leaves `fail-next` in the engine's directory), the host's `repair.auto`
switched off, a failed provision step (`fail-step`) repaired by the agent started from the UI (the engine's fake-mode
guest session asks once and fixes on the reply; the engine runs the job again), a secret added and revealed, a VM's vault request allowed from its card, a profile edited and saved, a reinstall, a restore of the
backup it left through the agent's restore route, and pairing a second device in headless Chromium. Nothing in
the product enrols the account's backup key on a VM yet; the scripted guest adds it from the store once the test
names the account, so the device can open the backup.

## Live changes

`ApiBackend` follows the agent's merged event stream. An engine's event names what changed, never the state:
a job event re-reads that job, a backup event that VM's backups, a VM, update or `engine.reset` event the
engine, a vault approval or entry-request event the pending vault requests. The agent's own `update` event
re-reads this device's update and the "update everything" run, `connections` this device's links to the VMs.
A job the page saw end stays ended whatever an older read says. What has no event is read once a minute:
usage, guest reports (coding agents), host administration, profiles, secrets, and the vault requests of an
engine that was not reachable.

## Settings, jobs, updates

A VM's Services and Identity settings and its profiles are its desired state on its engine
(`GET`/`PUT /vms/{name}/desired`): a save reads the document, changes the named fields and stores it whole, so
fields this page does not show stay as they are. Nothing runs on the VM; the engine's `provision-status` then
reports the change and Reprovision shows the dot. Memory and CPU keep their own routes and the inline "restart
now or later". Creating a VM is one request with the settings; the engine provisions it in the same job. A failed
provision or reinstall is retried through `POST /jobs/{id}/retry`; a restore needs its backup's key, which only a
device opens, so it starts again through the agent's restore route. Update runs the agent's "update everything"
and shows each part's state; "only this VM" is the engine's VM update route.

## Repair agent

A failed provision, reinstall or restore shows the VM's repair session from its engine (`GET /vms/{name}/repair`,
read once per failed job, then on each `repair.started`, `repair.output` and `repair.ended` event, the lines after the
last one read). Only a session whose `jobId` is that job shows under it, and only the engine and VM of that job get
its reply, Stop (`DELETE`) or start (`POST /vms/{name}/repair`; a `409 repair-running` follows the session the host
started). The reply box is there only while the session waits for an answer. "Fixed" shows only when the session
ended with `fixed`; the job the engine retried then shows it. The pop-out opens the window on the VM (`#/home/<vm>`).
An admin switches `repair.auto` on the host's Configuration tab.

## The one-time upgrade

When `GET /api/upgrade` reports an old install not converted, its dialog is the only screen. It shows the step
the agent reports as the first not done (engine, recovery code, close programs, convert) and runs only that one;
the conversion is sent only after `GET /api/upgrade/writers` is empty. The recovery code from `POST /api/device/setup`
stays in the dialog's state only, until the user confirms it was kept (`POST /api/device/recovery-code/kept` with the
code's issue); the agent counts the step done only then, and refuses the confirm of a code replaced since, which shows the
current one instead. A reload before that asks for a new code (`POST /api/device/recovery-code/again`; the earlier one
stops working) and shows it; the first run does the same when `GET /api/state` reports `recoveryUnconfirmed`. The convert
step names the shared hosts the agent reports as still on the old stack (`oldHosts`). When the agent reports `converted`
with notes, they show once with one button (`POST /api/upgrade/notes/read`); then, or without notes, the normal screens load.

## Secrets, profiles, hosts

A secret's value reaches the page only from the agent's `reveal` route, into the element that shows it for
half a minute, or from a field the user types in; neither goes into the state, a log or the URL, and both are
emptied when they close. A vault card names the VM and host as the engine that holds the request names them
and shows the agent's description and reason marked as the agent's; its answer goes to that engine. A profile
is saved through the engine with the revision the editor read: the engine merges a newer revision or opens a
conflict that an agent in a VM resolves, shown only as a state of that profile. A host other than the one on
this machine is added only after the user confirmed the certificate fingerprint the agent probed.

## What the API does not offer yet

`State.na` lists the parts `ApiBackend` cannot serve; each screen shows such a part as not available
and its action sends nothing. They are sharing VS Code web, updating a VM's coding
agents, the T3 proxy URL and the chat history kept in backups (neither is in the desired state), the disk,
checkpoints and start-with-host settings, the VM password, the log and consoles, rotating a VM token (its
one-time value has no place yet), the notifications switch, and saving or printing the recovery code. Windows
sign-in is offered only where the agent reports a Windows device.

## Desktop shell

In the Windows shell the page runs in WebView2 with `?surf=pop|win|alone`. Closing the pop-out or the lone vault
card (its buttons, or Escape) posts `{type: 'close'}`, and opening the window posts `{type: 'openWindow', route}`,
both through `window.chrome.webview.postMessage` and only when that object exists (`src/ui/shell.ts`). In a
browser the pop-out opens the window by navigating, and close does nothing.

## Parameters of `index.html`

| Parameter | Values |
|---|---|
| `surf` | `pop`, `win`, `phone`, `alone` (the vault card by itself); default by width |
| `theme` | `light`, `dark`; default follows the preference, then the system |
| `scheme` | `green` (default), `blue`, `copper`, `mono` |
| `scene` | A start state of the fake backend, listed as `SCENES` in `src/backend/fake.ts` |
| `timers=0` | Nothing moves by itself |

The route is the hash, for example `#/projects` or `#/host/orchard/users`.
