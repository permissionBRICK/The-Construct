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
the agent's tests do, with a scripted SSH guest so a provision job runs to its end, and the agent; it
drives the first run, creating a VM, a reprovision and pairing a second device in headless Chromium.

## What the API does not offer yet

`State.na` lists the parts `ApiBackend` cannot serve; each screen shows such a part as not available
and its action sends nothing. They are the key vault (secrets, requests, leases, history), the repair
agent's chat, forwards, sharing and the microphone, projects, coding agents, the VM services and
identity settings, the disk, checkpoints and start-with-host settings, the VM password, reinstall and
restore, opening T3 Code, VS Code, SSH, the log and consoles (the shell's job), rotating a VM token
(its one-time value has no place yet), retrying a host job, this device's own update and channel,
start with the OS and notifications, Windows sign-in, and saving or printing the recovery code.

## Parameters of `index.html`

| Parameter | Values |
|---|---|
| `surf` | `pop`, `win`, `phone`, `alone` (the vault card by itself); default by width |
| `theme` | `light`, `dark`; default follows the preference, then the system |
| `scheme` | `green` (default), `blue`, `copper`, `mono` |
| `scene` | A start state of the fake backend, listed as `SCENES` in `src/backend/fake.ts` |
| `timers=0` | Nothing moves by itself |

The route is the hash, for example `#/projects` or `#/host/orchard/users`.
