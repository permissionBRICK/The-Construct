import { useEffect, useRef, useState } from 'preact/hooks';
import type { ComponentChildren } from 'preact';
import { activeJob, running, vmDot, warnedJob } from '../model/vm';
import { shortVersion, updateDue } from '../model/update';
import { section, VM_PAGES, type Page, type Route } from '../model/route';
import { Icon, type IconName } from './icons';
import { Btn, Dot, Pip } from './kit';
import { na, useApp } from './ctx';
import { EverythingBlock, FilesCard, JobBlock, NEEDS_RUNNING, PowerBtn, ReinstallConfirm, SignInBlock, SignInCard, SignInCards, Tiles, VaultCard, VaultCards, WarnedBlock } from './blocks';
import { lostSignIns } from '../model/signin';
import { filesByVm, nextAloneRequest } from '../model/vault';
import { PageView } from './pages';

/* The three surfaces from the same parts: the tray pop-out (the stack), the window (sidebar, VM tabs,
   pages) and the phone (the stack, the pages, a bottom tab bar). Plus the lone vault card from the tray. */

export const NAV: [Page, string, IconName][] = [
  ['home', 'Overview', 'home'], ['projects', 'Projects', 'folder'], ['connections', 'Connections', 'link'], ['backups', 'Backups', 'box'], ['settings', 'Settings', 'set'],
];
const NAV2: [Page, string, IconName][] = [['vault', 'Key vault', 'key'], ['hosts', 'Hosts', 'host']];

function VmMenu() {
  const { s, vm, selectVm, surf, shell, go } = useApp();
  if (!vm) return null;
  const close = (e: Event) => { (e.currentTarget as HTMLElement).closest('details')!.open = false; };
  return (
    <details class="who">
      <summary><Dot s={vmDot(vm, activeJob(s, vm.id))} />{vm.name}<Icon n="down" /></summary>
      <div class="vml">
        {s.vms.filter(x => x.id !== vm.id).map(x => {
          const host = s.hosts.find(h => h.id === x.hostId);
          return <button type="button" onClick={e => { close(e); selectVm(x.id); }}><Dot s={vmDot(x, activeJob(s, x.id))} />{x.name}<span class="d">{host?.name}</span></button>;
        })}
        <button type="button" class="new" onClick={e => { close(e); if (surf === 'pop') shell.openWindow({ page: 'create' }); else go({ page: 'create' }); }}><Icon n="plus" />New VM</button>
      </div>
    </details>
  );
}

/** The stack: one VM, open it, Reprovision and Reinstall. */
export function Stack({ footer }: { footer: boolean }) {
  const { s, be, vm, shell } = useApp();
  const [wipe, setWipe] = useState(false);
  if (!vm) return <div class="stack"><VaultCards /><SignInCards />{s.loading ? <LoadingVm /> : <NoVm />}</div>;
  const job = activeJob(s, vm.id);
  const warned = job ? null : warnedJob(s, vm.id);
  const host = s.hosts.find(h => h.id === vm.hostId);
  return (
    <div class="stack">
      <VaultCards />
      <SignInCards />
      <div class="hd"><VmMenu /><span class="host">{host?.name}</span><PowerBtn vm={vm} /></div>
      {footer ? (
        // The pop-out: the control panel is what it is opened for most (the coloured button); the open actions share
        // one row below it.
        <>
          <Btn class="big" kind="pri" icon="gear" onClick={() => shell.openWindow({ page: 'home' })}>Control panel</Btn>
          <div class="three">
            <Btn icon="open" onClick={() => be.open(vm.id, 't3')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>T3 Code</Btn>
            <Btn icon="code" onClick={() => be.open(vm.id, 'vscode')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>VS Code</Btn>
            <Btn icon="term" onClick={() => be.open(vm.id, 'ssh')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>SSH</Btn>
          </div>
        </>
      ) : (
        <>
          <Btn kind="pri" icon="open" onClick={() => be.open(vm.id, 't3')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>T3 Code</Btn>
          <div class="two">
            <Btn icon="code" onClick={() => be.open(vm.id, 'vscode')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>VS Code</Btn>
            <Btn icon="term" onClick={() => be.open(vm.id, 'ssh')} disabled={na(s, 'open') || !running(vm)} title={running(vm) ? undefined : NEEDS_RUNNING}>SSH</Btn>
          </div>
        </>
      )}
      {job ? <JobBlock job={job} vm={vm} /> : wipe ? <ReinstallConfirm vm={vm} onCancel={() => setWipe(false)} /> : <Tiles vm={vm} onReinstall={() => setWipe(true)} />}
      {warned && <WarnedBlock job={warned} vm={vm} />}
      {s.everything && (s.everything.running || s.everything.parts.some(p => p.state === 'failed')) && <EverythingBlock e={s.everything} />}
      {footer && (
        <nav class="sft">
          <button type="button" onClick={() => shell.openWindow({ page: 'vault' })}><Icon n="key" />Key vault</button>
          <span class="v" title={s.version}>{shortVersion(s.version, s.prefs.channel)}</span>
        </nav>
      )}
    </div>
  );
}

function LoadingVm() {
  return (
    <div class="blk" role="status" aria-live="polite">
      <span class="d">Loading…</span>
    </div>
  );
}

function NoVm() {
  const { s, surf, shell, go } = useApp();
  const unreachable = s.hosts.filter(h => h.problem);
  const allDown = s.hosts.length > 0 && unreachable.length === s.hosts.length;
  return (
    <>
      {unreachable.length > 0 && (
        <div class="blk warn" role="status">
          <div class="bh"><b>Host unreachable</b></div>
          {unreachable.map(h => <div class="msg">{h.name}: {h.problem}</div>)}
          {allDown && (
            <Btn kind="pri" icon="gear" onClick={() => (surf === 'pop' ? shell.openWindow({ page: 'hosts' }) : go({ page: 'hosts' }))}>Hosts</Btn>
          )}
        </div>
      )}
      {!allDown && (
        <div class="blk">
          <Btn kind="pri" icon="plus" onClick={() => (surf === 'pop' ? shell.openWindow({ page: 'create' }) : go({ page: 'create' }))}>New VM</Btn>
        </div>
      )}
    </>
  );
}

function closeOpenMenus(root: ParentNode, keep?: Node | null): boolean {
  let closed = false;
  for (const m of root.querySelectorAll('details[open] > .vml')) {
    const d = m.parentElement as HTMLDetailsElement;
    if (!keep || !d.contains(keep)) { d.open = false; closed = true; }
  }
  return closed;
}

/** The pop-out and the lone card tell the desktop shell how tall their content is, as it changes; the shell sizes the
    window to it instead of a fixed height (finding 7). A menu open below its button counts too, and open menus close
    whenever the surface hides, loses focus, or gets a click outside them. */
function useFit() {
  const { s, shell } = useApp();
  const ref = useRef<HTMLDivElement>(null);
  const measureRef = useRef<(() => void) | null>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    let last = 0;
    let ro: ResizeObserver | null = null;
    const measure = (force = false) => {
      if (!shell.fit) return;
      const top = el.getBoundingClientRect().top;
      let h = el.getBoundingClientRect().height;
      for (const m of el.querySelectorAll('details[open] > .vml, .tippop')) {
        // An open menu or discovery tooltip is placed over the page, so the page's own size does not change with it: watched as well.
        ro?.observe(m);
        h = Math.max(h, m.getBoundingClientRect().bottom - top + 12);
      }
      h = Math.ceil(h);
      if (h > 0 && (force || h !== last)) { last = h; shell.fit(h); }
    };
    measureRef.current = () => measure();
    const onToggle = () => measure();
    if (shell.fit && typeof ResizeObserver !== 'undefined') {
      ro = new ResizeObserver(() => measure());
      ro.observe(el);
      el.addEventListener('toggle', onToggle, true);
      measure();
    }
    const isEditable = (node: EventTarget | null) =>
      node instanceof HTMLElement && (node.matches('input, textarea, select') || node.isContentEditable);
    // Focusing the container briefly before blurring resets Chromium's sequential focus navigation starting point
    // back to the top of the surface, so pressing Tab after dropping an auto-focus still lands on the first control.
    const resetFocus = () => {
      el.tabIndex = -1;
      el.focus({ preventScroll: true });
      el.blur();
      el.removeAttribute('tabindex');
    };
    const blurFocused = (onlyNonEditable: boolean) => {
      const active = document.activeElement;
      if (active instanceof HTMLElement && active !== el && el.contains(active) && (!onlyNonEditable || !isEditable(active))) {
        resetFocus();
      }
    };
    // Suppress host-injected initial focus (e.g. WebView2 focusing the first control on first show) until the user
    // interacts by keyboard or pointer, so the VM dropdown never opens with a focus outline by default.
    let suppressAutoFocus = true;
    blurFocused(true);
    const allowFocus = () => { suppressAutoFocus = false; };
    const onFocusIn = (e: FocusEvent) => {
      if (suppressAutoFocus && e.target instanceof HTMLElement && e.target !== el && !isEditable(e.target)) resetFocus();
    };
    const closeMenus = () => { if (closeOpenMenus(el)) measure(); };
    const onHide = () => {
      closeMenus();
      blurFocused(false);
      suppressAutoFocus = true;
    };
    const onShow = () => {
      suppressAutoFocus = true;
      blurFocused(true);
      measure(true);
      if (typeof requestAnimationFrame === 'function') {
        requestAnimationFrame(() => {
          if (suppressAutoFocus) blurFocused(true);
          measure();
        });
      }
    };
    const onVisibility = () => { if (document.hidden) onHide(); else onShow(); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeMenus(); };
    // Capture-phase pointerup also dismisses when clicking disabled controls (which suppress click);
    // the toggle event updates the fitted height afterwards so click delivery is unaffected.
    const onOutside = (e: Event) => { closeOpenMenus(el, e.target as Node | null); };
    addEventListener('blur', onHide);
    addEventListener('pagehide', onHide);
    addEventListener('keydown', onKey);
    document.addEventListener('keydown', allowFocus, true);
    document.addEventListener('pointerdown', allowFocus, true);
    el.addEventListener('focusin', onFocusIn, true);
    document.addEventListener('visibilitychange', onVisibility);
    document.addEventListener('pointerup', onOutside, true);
    document.addEventListener('click', onOutside);
    const offHide = shell.onHide?.(onHide);
    const offShow = shell.onShow?.(onShow);
    return () => {
      measureRef.current = null;
      ro?.disconnect();
      el.removeEventListener('toggle', onToggle, true);
      removeEventListener('blur', onHide);
      removeEventListener('pagehide', onHide);
      removeEventListener('keydown', onKey);
      document.removeEventListener('keydown', allowFocus, true);
      document.removeEventListener('pointerdown', allowFocus, true);
      el.removeEventListener('focusin', onFocusIn, true);
      document.removeEventListener('visibilitychange', onVisibility);
      document.removeEventListener('pointerup', onOutside, true);
      document.removeEventListener('click', onOutside);
      offHide?.();
      offShow?.();
    };
  }, [shell]);
  useEffect(() => { measureRef.current?.(); }, [s.autoUpdateTip, s.release, s.prefs.autoUpdate, s.everything?.running]);
  return ref;
}

export function PopOut() {
  const { s } = useApp();
  const fit = useFit();
  return <div class="pop" ref={fit}>{s.upgrade || s.firstRun ? <PageView /> : <Stack footer />}</div>;
}

/** A card alone, popped from the tray while Construct is closed: the vault's (a request, else a VM's files that still
    hold a secret), else a lost sign-in's (S36). The vault card's buttons and the cross close it; a sign-in started
    from the card runs here and closes it once nothing is left to show. */
export function Alone() {
  const { s, shell } = useApp();
  // A card done gives way to the next one waiting; the surface closes once none is left.
  // Requests currently displayed inline in another app (e.g. T3 Code Desktop) are skipped on the lone tray card.
  const [gone, setGone] = useState<string[]>([]);
  const r = nextAloneRequest(s.vaultRequests, gone);
  const files = filesByVm(s.vaultFiles.filter(f => !gone.includes('f:' + f.id)))[0];
  const lost = lostSignIns(s.vms).find(l => !gone.includes('n:' + l.vm.id + '/' + l.id.id));
  // A file answer that has not landed keeps the surface open: if it fails, the file comes back for another try.
  const [landing, setLanding] = useState(0);
  const open = !!r || !!files || !!lost || !!s.signIn || landing > 0;
  const shown = useRef(false);
  useEffect(() => {
    if (open) shown.current = true;
    else if (shown.current) shell.close();
  }, [open]);
  const fileDone = (id: string, landed: Promise<boolean>) => {
    setLanding(n => n + 1);
    void landed.then(ok => { if (ok) setGone(g => [...g, 'f:' + id]); }, () => {}).finally(() => setLanding(n => n - 1));
  };
  const fit = useFit();
  return (
    <div class="alone" ref={fit}>
      {r ? <VaultCard key={r.id} r={r} onDone={() => setGone(g => [...g, 'v:' + r.id])} />
        : files ? <FilesCard key={files[0].hostId + '/' + files[0].vmName} files={files} onDone={fileDone} />
        : lost ? <SignInCard key={lost.vm.id + '/' + lost.id.id} vm={lost.vm} agent={lost.id.id} onDone={() => setGone(g => [...g, 'n:' + lost.vm.id + '/' + lost.id.id])} />
        : s.signIn ? <SignInBlock si={s.signIn} /> : null}
    </div>
  );
}

function Side() {
  const { s, route, go, vm } = useApp();
  const at = section(route.page);
  const nb = (id: Page, label: string, icon: IconName, extra?: ComponentChildren) => (
    <button type="button" class={at === id ? 'on' : ''} aria-current={at === id ? 'page' : undefined} onClick={() => go({ page: id })}><Icon n={icon} />{label}{extra}</button>
  );
  return (
    <aside class="side">
      <div class="lg"><i><Icon n="cube" /></i>Construct</div>
      {vm && <div class="gl">{vm.name}</div>}
      {vm && NAV.map(n => nb(...n))}
      <div class="gl">Shared</div>
      {NAV2.map(n => nb(...n))}
      <span class="grow" />
      {nb('app', 'Construct', 'gear', <><span class="v">{s.version}</span>{updateDue(s) && <Pip />}</>)}
    </aside>
  );
}

function VmTabs() {
  const { s, vm, selectVm, route, go } = useApp();
  return (
    <header class="top">
      {s.vms.map(x => {
        const host = s.hosts.find(h => h.id === x.hostId);
        return (
          <button type="button" class={`vtab ${x.id === vm?.id ? 'on' : ''}`} onClick={() => { selectVm(x.id); if (!VM_PAGES.includes(route.page)) go({ page: 'home' }); }}>
            <Dot s={vmDot(x, activeJob(s, x.id))} />{x.name}<small>{host?.name}</small>
          </button>
        );
      })}
      <button type="button" class="vtab add" title="New VM" aria-label="New VM" onClick={() => go({ page: 'create' })}><Icon n="plus" /></button>
    </header>
  );
}

export function Window() {
  const { s } = useApp();
  if (s.upgrade || s.firstRun) return <div class="dlgwrap"><PageView /></div>;
  return (
    <div class="shell">
      <Side />
      <VmTabs />
      <main class="main"><PageView /></main>
    </div>
  );
}

export function Phone() {
  const { s, route, go, vm } = useApp();
  if (s.upgrade || s.firstRun) return <div class="phn"><PageView /></div>;
  const at = section(route.page);
  const g = at === 'vault' || at === 'hosts' || at === 'app' ? at : 'home';
  const t = (id: Page, label: string, icon: IconName, pip?: boolean) => (
    <button type="button" class={g === id ? 'on' : ''} onClick={() => go({ page: id })}><Icon n={icon} /><span>{label}</span>{pip && <Pip />}</button>
  );
  return (
    <div class="phn">
      <div class="body">
        {route.page === 'home' ? (
          <>
            <Stack footer={false} />
            {vm && (
              <div class="plist">
                {NAV.slice(1).map(([id, label, icon]) => (
                  <button type="button" class="r" onClick={() => go({ page: id })}><span class="rl"><Icon n={icon} />{label}</span><Icon n="right" /></button>
                ))}
              </div>
            )}
          </>
        ) : <PageView />}
      </div>
      <nav class="tbar">
        {t('home', vm?.name ?? 'VM', 'monitor')}
        {t('vault', 'Vault', 'key')}
        {t('hosts', 'Hosts', 'host')}
        {t('app', 'Construct', 'gear', updateDue(s))}
      </nav>
    </div>
  );
}

export type { Route };
