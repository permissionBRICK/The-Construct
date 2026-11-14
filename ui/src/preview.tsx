import { render } from 'preact';
import { useEffect, useMemo, useState } from 'preact/hooks';
import { App } from './ui/App';
import { FakeBackend, SCENES, type Demo, type Scene } from './backend/fake';
import type { Scheme, ThemeMode } from './backend/types';
import type { Route } from './model/route';
import type { Surface } from './ui/ctx';
import { SCHEME_ORDER, SCHEMES } from './model/tokens';
import './styles/app.css';
import './styles/preview.css';

/* A frame for looking at the app on the fake backend: each surface at its real size, the theme and colour
   switches, a start state, and buttons that make the fake backend do what an agent or a release would. */

const SURFS: [Surface, string][] = [['pop', 'Pop-out'], ['win', 'Window'], ['phone', 'Phone'], ['alone', 'Vault alone']];
const DEMOS: [Demo, string][] = [['lease', 'Vault request'], ['enter', 'Agent asks for a secret'], ['release', 'Release out'], ['change', 'Change waits'], ['fail-next', 'Next job fails']];

function Preview() {
  const q = new URLSearchParams(location.search);
  const [surf, setSurf] = useState<Surface>((SURFS.find(x => x[0] === q.get('surf'))?.[0]) ?? (innerWidth < 700 ? 'phone' : 'pop'));
  const [scene, setScene] = useState<Scene>((SCENES.find(x => x === q.get('scene'))) ?? 'calm');
  const [route, setRoute] = useState<{ r: Route; n: number }>({ r: { page: 'home' }, n: 0 });
  const be = useMemo(() => new FakeBackend({ scene }), [scene]);
  const [, bump] = useState(0);
  useEffect(() => be.subscribe(() => bump(x => x + 1)), [be]);
  useEffect(() => () => be.dispose(), [be]);
  const prefs = be.snapshot().prefs;
  useEffect(() => {
    const u = new URL(location.href);
    u.searchParams.set('surf', surf); u.searchParams.set('scene', scene);
    history.replaceState(null, '', u);
  }, [surf, scene]);

  const shell = {
    openWindow(r: Route) { setRoute({ r, n: route.n + 1 }); setSurf('win'); },
    close() { setSurf('pop'); },
  };
  const app = <App key={`${surf}-${route.n}-${scene}`} be={be} surf={surf} shell={shell} initialRoute={route.r} />;
  const seg = <T extends string>(items: [T, string][], cur: T, set: (v: T) => void) => (
    <div class="pv-seg">{items.map(([v, l]) => <button aria-pressed={v === cur} onClick={() => set(v)}>{l}</button>)}</div>
  );
  return (
    <div class={`pv pv-${prefs.theme}`}>
      <header class="pv-top">
        <b>Construct UI</b>
        {seg(SURFS, surf, setSurf)}
        {seg<ThemeMode>([['auto', 'Auto'], ['light', 'Light'], ['dark', 'Dark']], prefs.theme, theme => be.setPrefs({ theme }))}
        {seg<Scheme>(SCHEME_ORDER.map(k => [k, SCHEMES[k].label]), prefs.scheme, scheme => be.setPrefs({ scheme }))}
        <label>Start <select value={scene} onChange={e => setScene(e.currentTarget.value as Scene)}>{SCENES.map(s => <option value={s}>{s}</option>)}</select></label>
        <span class="pv-sp" />
        {DEMOS.map(([d, l]) => <button class="pv-btn" onClick={() => be.demo(d)}>{l}</button>)}
      </header>
      <main class="pv-stage">
        {surf === 'win' ? <div class="pv-win"><div class="pv-bar">Construct</div><div class="pv-in">{app}</div></div>
          : surf === 'phone' ? <div class="pv-phone"><div class="pv-in">{app}</div></div>
          : <div class="pv-desk"><div class={`pv-pop ${surf === 'alone' ? 'alone' : ''}`}>{app}</div><div class="pv-task"><i /><i /><i /><span class="pv-tray" /><span>16:42</span></div></div>}
      </main>
    </div>
  );
}

render(<Preview />, document.getElementById('preview')!);
