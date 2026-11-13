import { render } from 'preact';
import { App } from './ui/App';
import { FakeBackend, SCENES, type Scene } from './backend/fake';
import { ApiBackend } from './backend/api/backend';
import type { Backend, Scheme } from './backend/types';
import { formatRoute, type Route } from './model/route';
import type { Surface } from './ui/ctx';
import './styles/app.css';

/* The standalone app. Served by the device agent, it opens with the agent's token in the fragment
   (`#agent-token=...`) and runs on the real API; otherwise on the fake backend. URL parameters:
     surf=pop|win|phone|alone   which surface (default: phone below 700 px, else window)
     theme=light|dark           overrides the theme preference
     scheme=green|blue|copper|mono
     scene=<name>               start in one state of the fake backend (see SCENES)
     timers=0                   nothing moves by itself (screenshots)
   The route is the hash: #/projects, #/host/orchard/users. */

const TOKEN_KEY = 'construct.agent-token';

/** The agent's token: taken out of the fragment at once, so it is in no URL the page keeps, and kept for this
    tab only, so the pop-out's switch to the window keeps working. */
function agentToken(): string | null {
  const m = /(?:^#|&)agent-token=([^&]+)/.exec(location.hash);
  if (m) {
    try { sessionStorage.setItem(TOKEN_KEY, decodeURIComponent(m[1])); } catch { /* storage off: this load only */ }
    history.replaceState(null, '', location.pathname + location.search + '#/');
    return decodeURIComponent(m[1]);
  }
  try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; }
}

const q = new URLSearchParams(location.search);
const surfParam = q.get('surf');
const surf: Surface = surfParam === 'pop' || surfParam === 'win' || surfParam === 'phone' || surfParam === 'alone' ? surfParam : innerWidth < 700 ? 'phone' : 'win';
const token = agentToken();
let be: Backend;
if (token) {
  const api = new ApiBackend({ base: location.origin, token });
  void api.start();
  be = api;
} else {
  const scene = SCENES.includes(q.get('scene') as Scene) ? (q.get('scene') as Scene) : undefined;
  be = new FakeBackend({ scene, timers: q.get('timers') !== '0' });
}
const scheme = q.get('scheme');
if (scheme === 'green' || scheme === 'blue' || scheme === 'copper' || scheme === 'mono') be.setPrefs({ scheme: scheme as Scheme });
const theme = q.get('theme');
if (theme === 'light' || theme === 'dark') be.setPrefs({ theme });

// Standalone, the pop-out opens the window by navigating; the desktop shell provides its own.
const shell = {
  openWindow(r: Route) {
    const u = new URL(location.href);
    u.searchParams.set('surf', 'win');
    u.hash = formatRoute(r);
    location.href = u.toString();
  },
  close() { /* nothing to close in a browser tab */ },
};

document.documentElement.dataset.surf = surf;
render(<App be={be} surf={surf} shell={shell} hashRouting />, document.getElementById('app')!);
