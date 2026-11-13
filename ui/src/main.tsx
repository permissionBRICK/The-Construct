import { render } from 'preact';
import { App } from './ui/App';
import { FakeBackend, SCENES, type Scene } from './backend/fake';
import type { Scheme } from './backend/types';
import { formatRoute, type Route } from './model/route';
import type { Surface } from './ui/ctx';
import './styles/app.css';

/* The standalone app on the fake backend. URL parameters:
     surf=pop|win|phone|alone   which surface (default: phone below 700 px, else window)
     theme=light|dark           overrides the theme preference
     scheme=green|blue|copper|mono
     scene=<name>               start in one state of the fake backend (see SCENES)
     timers=0                   nothing moves by itself (screenshots)
   The route is the hash: #/projects, #/host/orchard/users. */

const q = new URLSearchParams(location.search);
const surfParam = q.get('surf');
const surf: Surface = surfParam === 'pop' || surfParam === 'win' || surfParam === 'phone' || surfParam === 'alone' ? surfParam : innerWidth < 700 ? 'phone' : 'win';
const scene = SCENES.includes(q.get('scene') as Scene) ? (q.get('scene') as Scene) : undefined;
const be = new FakeBackend({ scene, timers: q.get('timers') !== '0' });
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
