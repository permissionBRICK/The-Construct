import { useEffect, useState } from 'preact/hooks';
import type { Scheme, ThemeMode } from '../../backend/types';
import { updateDue, updateParts } from '../../model/update';
import { SCHEME_ORDER, SCHEMES } from '../../model/tokens';
import { Icon } from '../icons';
import { Btn, Field, Head, KV, Page, Row, Sec, Seg, Switch, Table, Tag } from '../kit';
import { copy, useApp } from '../ctx';

/* Construct: the one Update, the parts it covers, preferences, this device. Then creating a VM and the
   one-time upgrade dialog. */

export function Construct() {
  const { s, be, surf, go } = useApp();
  const parts = updateParts(s);
  const due = updateDue(s);
  const to = parts.find(p => p.to)?.to;
  return (
    <Page>
      <Head title="Construct" back={surf === 'phone' ? () => go({ page: 'home' }) : undefined} />
      {due && (
        <div class="blk row upd">
          <div class="bh grow"><b class="m">{s.version} → {to}</b></div>
          <Btn kind="pri" icon="up" onClick={() => be.updateEverything()}>Update</Btn>
        </div>
      )}
      <Sec>
        <Table cols={[{ h: 'Part' }, { h: 'Version', cls: 'm' }, { h: 'After', cls: 'e m' }]}
          rows={parts.map(p => [p.name, p.version, p.to ? <span class="warnc">{p.to}</span> : p.note === 'admin' ? <Tag>by its admin</Tag> : p.note === 'channel' ? <Tag>other channel</Tag> : '–'])} />
      </Sec>
      <Sec title="Preferences">
        <Row l="Channel"><Seg label="Channel" opts={['stable', 'nightly'] as const} labels={{ stable: 'Stable', nightly: 'Nightly' }} value={s.prefs.channel} onChange={channel => be.setPrefs({ channel })} /></Row>
        <Row l="Automatic updates"><Switch on={s.prefs.autoUpdate} onChange={autoUpdate => be.setPrefs({ autoUpdate })} label="Automatic updates" /></Row>
        <Row l="Theme"><Seg<ThemeMode> label="Theme" opts={['auto', 'light', 'dark']} labels={{ auto: 'Auto', light: 'Light', dark: 'Dark' }} value={s.prefs.theme} onChange={theme => be.setPrefs({ theme })} /></Row>
        <Row l="Colours">
          <span class="swatches" role="radiogroup" aria-label="Colours">
            {SCHEME_ORDER.map(k => <SchemeSwatch k={k} on={s.prefs.scheme === k} onPick={() => be.setPrefs({ scheme: k })} />)}
          </span>
        </Row>
        <Row l="Start with Windows"><Switch on={s.prefs.startWithOs} onChange={startWithOs => be.setPrefs({ startWithOs })} label="Start with Windows" /></Row>
        <Row l="Notifications"><Switch on={s.prefs.notifications} onChange={notifications => be.setPrefs({ notifications })} label="Notifications" /></Row>
      </Sec>
      <Sec title="This device">
        <KV k="Name">{s.device.name}</KV>
        <Row l="Recovery code"><Reveal /></Row>
        <Row l="Log"><Btn sm icon="log" onClick={() => be.openLog()}>Open</Btn></Row>
      </Sec>
    </Page>
  );
}

function SchemeSwatch({ k, on, onPick }: { k: Scheme; on: boolean; onPick: () => void }) {
  const t = SCHEMES[k].dark;
  return (
    <button type="button" role="radio" aria-checked={on} class={`swatch ${on ? 'on' : ''}`} title={SCHEMES[k].label} aria-label={SCHEMES[k].label} onClick={onPick}>
      <i style={{ background: t.side }}><b style={{ background: t.sidepri }} /></i>
    </button>
  );
}

/** The recovery code shown in place for a minute, with copy. */
function Reveal() {
  const { be, toast } = useApp();
  const [code, setCode] = useState<string[] | null>(null);
  const [left, setLeft] = useState(0);
  useEffect(() => {
    if (!code) return;
    const t = setInterval(() => setLeft(l => { if (l <= 1) { setCode(null); return 0; } return l - 1; }), 1000);
    return () => clearInterval(t);
  }, [code]);
  if (!code) return <Btn sm onClick={() => { setCode(be.showRecoveryCode()); setLeft(60); }}>Show</Btn>;
  return (
    <span class="confirm">
      <code class="m">{code.join(' ')}</code>
      <span class="d m">{left}s</span>
      <Btn sm icon="copy" title="Copy" onClick={() => { copy(code.join(' ')); toast('Copied'); }} />
      <Btn sm onClick={() => setCode(null)}>Hide</Btn>
    </span>
  );
}

export function CreateVm() {
  const { s, be, go, selectVm, surf } = useApp();
  const [where, setWhere] = useState<'local' | 'remote'>('local');
  const [name, setName] = useState('');
  const [addr, setAddr] = useState('');
  const [signIn, setSignIn] = useState<'windows' | 'token'>('windows');
  const [token, setToken] = useState('');
  const taken = s.vms.some(v => v.name === name.trim());
  const valid = /^[a-z][a-z0-9-]{1,30}$/.test(name.trim()) && !taken && (where === 'local' || (addr.trim() && (signIn === 'windows' || token.trim())));
  const create = () => {
    if (!valid) return;
    be.createVm({ where, name: name.trim(), hostAddress: addr.trim(), signIn, token: token.trim() });
    selectVm(name.trim());
    go({ page: 'home' });
  };
  return (
    <Page>
      <Head title="New VM" back={surf === 'phone' || s.vms.length ? () => go({ page: 'home' }) : undefined} />
      <div class="blk form narrow">
        <Seg label="Where" opts={['local', 'remote'] as const} labels={{ local: 'This PC', remote: 'Remote host' }} value={where} onChange={setWhere} />
        <label class="lab">Name<Field value={name} onInput={v => setName(v.toLowerCase())} label="Name" placeholder="name" mono wide onEnter={create} /></label>
        {taken && <span class="badc">Taken</span>}
        {where === 'remote' && (
          <>
            <label class="lab">Host address<Field value={addr} onInput={setAddr} label="Host address" placeholder="https://host:8443" mono wide /></label>
            <Seg label="Sign-in" opts={['windows', 'token'] as const} labels={{ windows: 'Windows sign-in', token: 'Token' }} value={signIn} onChange={setSignIn} />
            {signIn === 'token' && <Field type="password" value={token} onInput={setToken} label="Token" placeholder="Token" mono wide />}
          </>
        )}
        <Btn kind="pri" icon="plus" disabled={!valid} onClick={create}>Create</Btn>
      </div>
    </Page>
  );
}

const STEPS: [string, string][] = [['intro', 'New Construct'], ['code', 'Recovery code'], ['convert', 'Moving over'], ['done', 'Done']];

/** The one-time upgrade dialog (first screen of the window): what is new, the recovery code, moving over, done. */
export function Upgrade() {
  const { s, be, toast } = useApp();
  const u = s.upgrade;
  const [stored, setStored] = useState(false);
  if (!u) return null;
  const at = STEPS.findIndex(x => x[0] === u.step);
  const top = (
    <>
      <span class="brand"><Icon n="cube" />The Construct</span>
      <ol class="steps">{STEPS.map((x, i) => <li class={i < at ? 'ok' : i === at ? 'on' : ''}>{x[1]}</li>)}</ol>
    </>
  );
  const wl = (icon: preact.ComponentChildren, label: string, right?: preact.ComponentChildren, cls = '') => <div class={`wl ${cls}`}>{icon}<span>{label}</span>{right}</div>;
  if (u.step === 'intro') return (
    <div class="solo">{top}<h3>This is the new Construct</h3>
      <div class="wz"><div class="cap">New</div>
        {wl(<Icon n="monitor" />, 'One window for all your VMs')}
        {wl(<Icon n="key" />, 'The vault comes back with a recovery code')}
        {wl(<Icon n="phone" />, 'Approve vault requests from your phone')}
        {wl(<Icon n="repro" />, 'Update covers this PC, your hosts and your VMs')}
      </div>
      <div class="wz"><div class="cap">Comes along</div>
        <KV k="VMs">{s.vms.map(v => v.name).join(', ')}</KV><KV k="Projects">{s.projects.length}</KV><KV k="Secrets">{s.secrets.length}</KV>
      </div>
      <div class="wft"><span class="d"><Icon n="shield" />One admin prompt</span><Btn kind="pri" icon="right" onClick={() => be.upgradeNext()}>Continue</Btn></div>
    </div>
  );
  if (u.step === 'code') return (
    <div class="solo">{top}<h3>Recovery code</h3>
      <div class="codebox">{u.code.map(c => <span>{c}</span>)}</div>
      <div class="three">
        <Btn icon="copy" onClick={() => { copy(u.code.join(' ')); toast('Copied'); }}>Copy</Btn>
        <Btn icon="save" onClick={() => be.exportRecoveryCode('file')}>Save</Btn>
        <Btn icon="print" onClick={() => be.exportRecoveryCode('print')}>Print</Btn>
      </div>
      <label class="bf check"><input type="checkbox" checked={stored} onChange={e => setStored(e.currentTarget.checked)} /><span>I stored it</span></label>
      <Btn kind="pri" disabled={!stored} onClick={() => be.upgradeNext()}>Continue</Btn>
    </div>
  );
  if (u.step === 'convert') return (
    <div class="solo">{top}<h3>Moving over</h3>
      <div class="wz">
        {u.items.map(it => wl(it.state === 'done' ? <Icon n="check" /> : it.state === 'running' ? <Icon n="repro" /> : <i class="ring" />, it.name,
          <>{it.count && <span class="rv">{it.count}</span>}{it.state === 'running' && <div class="bar"><i style={{ width: `${it.pct}%` }} /></div>}</>, it.state === 'done' ? 'ok' : it.state === 'waiting' ? 'wait' : ''))}
      </div>
      <div class="wft"><span class="d"><Icon n="power" />VMs keep running</span><Btn kind="pri" icon="right" disabled>Continue</Btn></div>
    </div>
  );
  return (
    <div class="solo">{top}<h3>Ready</h3>
      <div class="wz">{s.vms.map(v => <div class="wl"><i class={`dot ${v.power === 'on' ? 'on' : ''}`} /><b>{v.name}</b><span class="d">{s.hosts.find(h => h.id === v.hostId)?.name}</span><span class="rv">{v.power === 'on' ? 'Running' : 'Off'}</span></div>)}</div>
      <div class="wft"><span class="d"><Icon n="check" />{s.vms.length} VMs, {s.projects.length} projects, {s.secrets.length} secrets</span><Btn kind="pri" icon="open" onClick={() => be.upgradeNext()}>Open Construct</Btn></div>
    </div>
  );
}
