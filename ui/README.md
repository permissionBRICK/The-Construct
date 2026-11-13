# Construct client UI

One web app for every client surface: the desktop agent's tray pop-out and window, a browser or phone
served by a host, and the VS Code webview. Preact and TypeScript, built with Vite into a static bundle.

This version runs on `FakeBackend`, an in-memory backend with invented data. The real API connects
through the same `Backend` interface later.

## Layout

| Path | Content |
|---|---|
| `src/backend/types.ts` | The `Backend` interface: a `State` snapshot, `subscribe`, and every action the screens use |
| `src/backend/fake.ts`, `sample.ts` | `FakeBackend`: simulated jobs, failures with the repair agent, vault requests, updates, pairing |
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
```

## Parameters of `index.html`

| Parameter | Values |
|---|---|
| `surf` | `pop`, `win`, `phone`, `alone` (the vault card by itself); default by width |
| `theme` | `light`, `dark`; default follows the preference, then the system |
| `scheme` | `green` (default), `blue`, `copper`, `mono` |
| `scene` | A start state of the fake backend, listed as `SCENES` in `src/backend/fake.ts` |
| `timers=0` | Nothing moves by itself |

The route is the hash, for example `#/projects` or `#/host/orchard/users`.
