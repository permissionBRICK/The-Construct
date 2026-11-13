import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import type { Backend, State } from '../backend/types';
import { formatRoute, parseRoute, type Route } from '../model/route';
import { cssVars, type Mode } from '../model/tokens';
import { AppCtx, type Ctx, type Shell, type Surface } from './ctx';
import { Alone, Phone, PopOut, Window } from './shells';

export interface AppProps {
  be: Backend;
  surf: Surface;
  shell: Shell;
  /** Keep the route in location.hash (the standalone app). Off when several apps share one page. */
  hashRouting?: boolean;
  initialRoute?: Route;
  /** Overrides the stored theme preference (preview frame, tests). */
  mode?: Mode;
}

function useBackend(be: Backend): State {
  const [s, setS] = useState(() => be.snapshot());
  useEffect(() => {
    setS(be.snapshot());
    return be.subscribe(() => setS(be.snapshot()));
  }, [be]);
  return s;
}

function useSystemDark(): boolean {
  const q = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : null;
  const [dark, setDark] = useState(!!q?.matches);
  useEffect(() => {
    if (!q) return;
    const f = () => setDark(q.matches);
    q.addEventListener('change', f);
    return () => q.removeEventListener('change', f);
  }, []);
  return dark;
}

const VM_KEY = 'construct.vm';
const ls = {
  get(k: string) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k: string, v: string) { try { localStorage.setItem(k, v); } catch { /* storage off */ } },
};

export function App({ be, surf, shell, hashRouting, initialRoute, mode }: AppProps) {
  const s = useBackend(be);
  const [route, setRoute] = useState<Route>(() => (hashRouting ? parseRoute(location.hash) : initialRoute ?? { page: 'home' }));
  const [vmId, setVmId] = useState<string | null>(() => ls.get(VM_KEY));
  const [toastText, setToast] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  const sysDark = useSystemDark();

  useEffect(() => {
    if (!hashRouting) return;
    const f = () => setRoute(parseRoute(location.hash));
    addEventListener('hashchange', f);
    return () => removeEventListener('hashchange', f);
  }, [hashRouting]);

  const toast = (t: string) => {
    setToast(t);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setToast(null), 2000);
  };
  useEffect(() => { if (s.notice) toast(s.notice.text); }, [s.notice?.id]);

  const vm = s.vms.find(v => v.id === vmId) ?? s.vms[0] ?? null;
  const go = (r: Route) => {
    setRoute(r);
    if (hashRouting) { const h = formatRoute(r); if (location.hash !== h) history.pushState(null, '', h); }
  };
  const ctx: Ctx = { be, s, surf, route, go, vm, selectVm: id => { setVmId(id); ls.set(VM_KEY, id); }, shell, toast };

  const m: Mode = mode ?? (s.prefs.theme === 'auto' ? (sysDark ? 'dark' : 'light') : s.prefs.theme);
  const style = useMemo(() => cssVars(s.prefs.scheme, m), [s.prefs.scheme, m]);
  useEffect(() => { if (hashRouting) document.documentElement.style.colorScheme = m; }, [m]);

  const Surface = { pop: PopOut, win: Window, phone: Phone, alone: Alone }[surf];
  return (
    <AppCtx.Provider value={ctx}>
      <div class={`app s-${surf} m-${m}`} style={style} data-surf={surf} data-scheme={s.prefs.scheme} data-mode={m}>
        <Surface />
        {toastText && <div class="toast" role="status">{toastText}</div>}
      </div>
    </AppCtx.Provider>
  );
}
