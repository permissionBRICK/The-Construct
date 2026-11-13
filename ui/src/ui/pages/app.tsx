import { useEffect, useState } from 'preact/hooks';
import type { FirstRunRequest, HostProbe, Scheme, ThemeMode } from '../../backend/types';
import { updateDue, updateParts } from '../../model/update';
import { SCHEME_ORDER, SCHEMES } from '../../model/tokens';
import { Icon } from '../icons';
import { Btn, Confirm, Copyable, Field, Head, KV, Page, Row, Sec, Seg, Switch, Table, Tag } from '../kit';
import { copy, na, useApp } from '../ctx';
import { EverythingBlock } from '../blocks';

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
      {due && !s.everything?.running && (
        <div class="blk row upd">
          <div class="bh grow"><b class="m">{s.version} → {to}</b></div>
          <Btn kind="pri" icon="up" onClick={() => be.updateEverything()}>Update</Btn>
        </div>
      )}
      {s.everything && <EverythingBlock e={s.everything} />}
      <Sec>
        <Table cols={[{ h: 'Part' }, { h: 'Version', cls: 'm' }, { h: 'After', cls: 'e m' }]}
          rows={parts.map(p => [p.name, p.version, p.to ? <span class="warnc">{p.to}</span> : p.note === 'admin' ? <Tag>by its admin</Tag> : p.note === 'channel' ? <Tag>other channel</Tag> : '–'])} />
      </Sec>
      <Sec title="Preferences">
        <Row l="Channel">{na(s, 'channel') ? <span class="d">Not available yet</span> : s.prefs.channel === 'stable' || s.prefs.channel === 'nightly'
          ? <Seg label="Channel" opts={['stable', 'nightly'] as const} labels={{ stable: 'Stable', nightly: 'Nightly' }} value={s.prefs.channel} onChange={channel => be.setPrefs({ channel })} />
          : <code class="m">{s.prefs.channel}</code>}</Row>
        <Row l="Automatic updates"><Switch on={s.prefs.autoUpdate} onChange={autoUpdate => be.setPrefs({ autoUpdate })} label="Automatic updates" /></Row>
        <Row l="Theme"><Seg<ThemeMode> label="Theme" opts={['auto', 'light', 'dark']} labels={{ auto: 'Auto', light: 'Light', dark: 'Dark' }} value={s.prefs.theme} onChange={theme => be.setPrefs({ theme })} /></Row>
        <Row l="Colours">
          <span class="swatches" role="radiogroup" aria-label="Colours">
            {SCHEME_ORDER.map(k => <SchemeSwatch k={k} on={s.prefs.scheme === k} onPick={() => be.setPrefs({ scheme: k })} />)}
          </span>
        </Row>
        {!na(s, 'os-prefs') && (!s.device.can || s.device.can.startWithOs) && <Row l="Start with Windows"><Switch on={s.prefs.startWithOs} onChange={startWithOs => be.setPrefs({ startWithOs })} label="Start with Windows" /></Row>}
        {!na(s, 'notifications') && <Row l="Notifications"><Switch on={s.prefs.notifications} onChange={notifications => be.setPrefs({ notifications })} label="Notifications" /></Row>}
      </Sec>
      <Sec title="This device">
        <KV k="Name">{s.device.name}</KV>
        <KV k="Version">{s.version}</KV>
        {s.device.sshKey && <SshKey text={s.device.sshKey} />}
        <Row l="Recovery code"><Reveal /></Row>
        <Row l="Log"><Btn sm icon="log" onClick={() => be.openLog()} disabled={na(s, 'log')}>Open</Btn></Row>
      </Sec>
    </Page>
  );
}

/** This device's SSH public key, which its engines authorize on the user's VMs; wrapped, with copy. */
function SshKey({ text }: { text: string }) {
  const { toast } = useApp();
  return (
    <div class="line key">
      <span>SSH key</span>
      <code class="grow sel brk">{text}</code>
      <Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn>
    </div>
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

/** The recovery code shown in place for a minute, with copy. Where the backend cannot show the code again
    (`code-reveal`), the button makes a new one after a confirm in place; the old one stops working. */
function Reveal() {
  const { s, be, toast } = useApp();
  const [code, setCode] = useState<string[] | null>(null);
  const [left, setLeft] = useState(0);
  useEffect(() => {
    if (!code) return;
    const t = setInterval(() => setLeft(l => { if (l <= 1) { setCode(null); return 0; } return l - 1; }), 1000);
    return () => clearInterval(t);
  }, [code]);
  const show = () => {
    void Promise.resolve(be.showRecoveryCode()).then(c => { setCode(c); setLeft(60); }, (e: Error) => toast(e.message));
  };
  if (!code) return na(s, 'code-reveal') ? <Confirm label="New code" danger={false} onYes={show} /> : <Btn sm onClick={show}>Show</Btn>;
  return (
    <span class="confirm">
      <code class="m">{code.join(' ')}</code>
      <span class="d m">{left}s</span>
      <Btn sm icon="copy" title="Copy" onClick={() => { copy(code.join(' ')); toast('Copied'); }} />
      <Btn sm onClick={() => setCode(null)}>Hide</Btn>
    </span>
  );
}

/** A host's certificate fingerprint as the agent read it, to compare with the one its admin sees, before the
    host is added. */
function PinConfirm({ probe, onYes, onNo }: { probe: HostProbe; onYes: () => void; onNo: () => void }) {
  return (
    <div class="blk pin" role="group" aria-label="Host fingerprint">
      <div class="bh"><Icon n="shield" /><b class="m">{probe.url}</b></div>
      <code class="m sel fp">{probe.pin.match(/.{1,4}/g)?.join(' ')}</code>
      <div class="two"><Btn onClick={onNo}>Cancel</Btn><Btn kind="pri" onClick={onYes}>It matches</Btn></div>
    </div>
  );
}

/** Probing a host and holding what it presented until the user confirms it; a new address starts over. */
function usePin() {
  const { be } = useApp();
  const [probe, setProbe] = useState<HostProbe | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const check = (address: string, then: (p: HostProbe) => void) => {
    setBusy(true); setErr('');
    void be.probeHost(address).then(p => { setBusy(false); then(p); }, (e: Error) => { setBusy(false); setErr(e.message); });
  };
  const reset = () => { setProbe(null); setErr(''); };
  return { probe, setProbe, busy, err, check, reset };
}

export function CreateVm() {
  const { s, be, go, selectVm, surf } = useApp();
  const [where, setWhere] = useState<'local' | 'remote'>('local');
  const [name, setName] = useState('');
  const [addr, setAddr] = useState('');
  const [signIn, setSignIn] = useState<'windows' | 'token'>(na(s, 'windows-sign-in') ? 'token' : 'windows');
  const [token, setToken] = useState('');
  const pin = usePin();
  const taken = s.vms.some(v => v.name === name.trim());
  const valid = /^[a-z][a-z0-9-]{1,30}$/.test(name.trim()) && !taken && (where === 'local' || (addr.trim() && (signIn === 'windows' || token.trim())));
  const send = (confirmed?: string) => {
    const id = be.createVm({ where, name: name.trim(), hostAddress: addr.trim(), signIn, token: token.trim(), pin: confirmed });
    if (id) selectVm(id);
    go({ page: 'home' });
  };
  // A remote host is added only with the fingerprint the user confirmed; a known one needs no confirm.
  const create = () => {
    if (!valid || pin.busy) return;
    if (where === 'local') { send(); return; }
    pin.check(addr.trim(), p => { if (p.known && !token.trim()) send(); else pin.setProbe(p); });
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
            <label class="lab">Host address<Field value={addr} onInput={v => { setAddr(v); pin.reset(); }} label="Host address" placeholder="https://host:8443" mono wide /></label>
            {!na(s, 'windows-sign-in') && <Seg label="Sign-in" opts={['windows', 'token'] as const} labels={{ windows: 'Windows sign-in', token: 'Token' }} value={signIn} onChange={setSignIn} />}
            {signIn === 'token' && <Field type="password" value={token} onInput={setToken} label="Token" placeholder="Token" mono wide />}
          </>
        )}
        {pin.err && <span class="badc">{pin.err}</span>}
        {where === 'remote' && pin.probe
          ? <PinConfirm probe={pin.probe} onNo={pin.reset} onYes={() => { const p = pin.probe!; pin.reset(); send(p.pin); }} />
          : <Btn kind="pri" icon="plus" disabled={!valid || pin.busy} onClick={create}>Create</Btn>}
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
        <Btn icon="save" onClick={() => be.exportRecoveryCode('file')} disabled={na(s, 'export-code')}>Save</Btn>
        <Btn icon="print" onClick={() => be.exportRecoveryCode('print')} disabled={na(s, 'export-code')}>Print</Btn>
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

const FR_STEPS: [string, string][] = [['start', 'Sign in'], ['code', 'Recovery code'], ['done', 'Done']];

/** First run of a device without an identity (11.6): a new account with its recovery code, pairing with a
    device that has one, or recovery with the code. Creating the first VM follows on the normal screens. */
export function FirstRun() {
  const { s, be, toast } = useApp();
  const f = s.firstRun;
  const [how, setHow] = useState<FirstRunRequest['how']>('setup');
  const [address, setAddress] = useState(f?.localAddress ?? '');
  const [deviceName, setDeviceName] = useState('');
  const [token, setToken] = useState('');
  const [account, setAccount] = useState('');
  const [code, setCode] = useState('');
  const [stored, setStored] = useState(false);
  const pin = usePin();
  if (!f) return null;
  const at = f.step === 'code' ? 1 : 0;
  const top = (
    <>
      <span class="brand"><Icon n="cube" />The Construct</span>
      <ol class="steps">{FR_STEPS.map((x, i) => <li class={i < at ? 'ok' : i === at ? 'on' : ''}>{x[1]}</li>)}</ol>
    </>
  );
  if (f.step === 'code' && f.code) return (
    <div class="solo">{top}<h3>Recovery code</h3>
      <div class="codebox">{f.code.map(c => <span>{c}</span>)}</div>
      {f.account && <KV k="Account"><span class="m sel">{f.account}</span></KV>}
      <div class="three">
        <Btn icon="copy" onClick={() => { copy(f.code!.join('-')); toast('Copied'); }}>Copy</Btn>
        <Btn icon="save" onClick={() => be.exportRecoveryCode('file')} disabled={na(s, 'export-code')}>Save</Btn>
        <Btn icon="print" onClick={() => be.exportRecoveryCode('print')} disabled={na(s, 'export-code')}>Print</Btn>
      </div>
      <label class="bf check"><input type="checkbox" checked={stored} onChange={e => setStored(e.currentTarget.checked)} /><span>I stored it</span></label>
      <Btn kind="pri" disabled={!stored} onClick={() => be.firstRunNext()}>Continue</Btn>
    </div>
  );
  if (f.step === 'pairing' && f.pairing) return (
    <div class="solo">{top}<h3>Pair with your other device</h3>
      <div class="wz"><div class="cap">Code</div><Copyable text={f.pairing.code} /></div>
      <div class="words row">{f.pairing.words.map(w => <span>{w}</span>)}</div>
      <div class="wft"><span class="d"><Icon n="repro" />Waiting for the other device</span><Btn onClick={() => be.firstRunNext()}>Cancel</Btn></div>
    </div>
  );
  const valid = address.trim() && deviceName.trim() && (how !== 'setup' || token.trim()) && (how !== 'recover' || (account.trim() && code.trim()));
  const start = (confirmed?: string) => be.firstRunStart({ how, address, deviceName, token, account, code, pin: confirmed });
  // The engine on this machine is trusted as found; another host only with the fingerprint the user confirmed.
  const go = () => {
    if (!valid || f.busy || pin.busy) return;
    if (f.localAddress && address.trim() === f.localAddress) { start(); return; }
    pin.check(address.trim(), p => { if (p.known && how !== 'setup') start(); else pin.setProbe(p); });
  };
  return (
    <div class="solo">{top}<h3>This device</h3>
      <div class="blk form">
        <Seg label="How" opts={['setup', 'pair', 'recover'] as const} labels={{ setup: 'New account', pair: 'Pair with a device', recover: 'Recovery code' }} value={how} onChange={setHow} />
        <label class="lab">Host address<Field value={address} onInput={v => { setAddress(v); pin.reset(); }} label="Host address" placeholder="https://host:8443" mono wide /></label>
        <label class="lab">Device name<Field value={deviceName} onInput={setDeviceName} label="Device name" placeholder="Device name" wide /></label>
        {how === 'setup' && <label class="lab">Token<Field type="password" value={token} onInput={setToken} label="Token" placeholder="Token" mono wide /></label>}
        {how === 'recover' && (
          <>
            <label class="lab">Account<Field value={account} onInput={setAccount} label="Account" placeholder="Account" mono wide /></label>
            <label class="lab">Recovery code<Field type="password" value={code} onInput={setCode} label="Recovery code" placeholder="XXXX-XXXX-…" mono wide onEnter={go} /></label>
          </>
        )}
        {(f.problem || pin.err) && <span class="badc">{pin.err || f.problem}</span>}
        {pin.probe
          ? <PinConfirm probe={pin.probe} onNo={pin.reset} onYes={() => { const p = pin.probe!; pin.reset(); start(p.pin); }} />
          : <Btn kind="pri" icon="right" disabled={!valid || f.busy || pin.busy} onClick={go}>{f.busy ? 'Working' : 'Continue'}</Btn>}
      </div>
    </div>
  );
}
