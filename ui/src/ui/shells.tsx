import { useState } from 'preact/hooks';
import type { ComponentChildren } from 'preact';
import { activeJob, vmDot } from '../model/vm';
import { updateDue } from '../model/update';
import { section, VM_PAGES, type Page, type Route } from '../model/route';
import { Icon, type IconName } from './icons';
import { Btn, Dot, Pip } from './kit';
import { na, useApp } from './ctx';
import { JobBlock, PowerBtn, ReinstallConfirm, Tiles, VaultCard, VaultCards } from './blocks';
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
  if (!vm) return <div class="stack"><VaultCards /><NoVm /></div>;
  const job = activeJob(s, vm.id);
  const host = s.hosts.find(h => h.id === vm.hostId);
  return (
    <div class="stack">
      <VaultCards />
      <div class="hd"><VmMenu /><span class="host">{host?.name}</span><PowerBtn vm={vm} /></div>
      <Btn kind="pri" icon="open" onClick={() => be.open(vm.id, 't3')} disabled={na(s, 'open')}>T3 Code</Btn>
      <div class="two">
        <Btn icon="code" onClick={() => be.open(vm.id, 'vscode')} disabled={na(s, 'open')}>VS Code</Btn>
        <Btn icon="term" onClick={() => be.open(vm.id, 'ssh')} disabled={na(s, 'open')}>SSH</Btn>
      </div>
      {job ? <JobBlock job={job} vm={vm} /> : wipe ? <ReinstallConfirm vm={vm} onCancel={() => setWipe(false)} /> : <Tiles vm={vm} onReinstall={() => setWipe(true)} />}
      {footer && (
        <nav class="sft">
          <button type="button" onClick={() => shell.openWindow({ page: 'vault' })}><Icon n="key" />Key vault</button>
          <span class="grow" />
          <button type="button" onClick={() => shell.openWindow({ page: 'home' })}><Icon n="gear" />Control panel</button>
        </nav>
      )}
    </div>
  );
}

function NoVm() {
  const { surf, shell, go } = useApp();
  return (
    <div class="blk">
      <Btn kind="pri" icon="plus" onClick={() => (surf === 'pop' ? shell.openWindow({ page: 'create' }) : go({ page: 'create' }))}>New VM</Btn>
    </div>
  );
}

export function PopOut() {
  const { s } = useApp();
  if (s.firstRun) return <div class="pop"><PageView /></div>;
  return <div class="pop"><Stack footer /></div>;
}

/** The vault card alone, popped from the tray while Construct is closed. Its buttons close it. */
export function Alone() {
  const { s, shell } = useApp();
  const r = s.vaultRequests[0];
  return <div class="alone">{r ? <VaultCard key={r.id} r={r} onDone={() => shell.close()} /> : null}</div>;
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
