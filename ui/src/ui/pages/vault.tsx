import { useEffect, useRef, useState } from 'preact/hooks';
import { QrSquare, Icon } from '../icons';
import { Btn, Confirm, Field, Head, NotYet, Page, Sec, Seg, Switch, Table, Tabs, Tag } from '../kit';
import { copy, na, useApp } from '../ctx';
import type { Page as P } from '../../model/route';

/* The key vault: secrets, access (leases), devices with pairing and revocation, history. */

function VaultTabs({ cur }: { cur: P }) {
  const { go } = useApp();
  return <Tabs items={[['vault', 'Secrets'], ['access', 'Access'], ['devices', 'Devices'], ['history', 'History']]} value={cur} onChange={t => go({ page: t })} />;
}

/** A secret's value shown in place for half a minute, with copy; it lives only in this element's state. */
function RevealValue({ name }: { name: string }) {
  const { be, toast } = useApp();
  const [value, setValue] = useState<string | null>(null);
  const [left, setLeft] = useState(0);
  // Each Show and each Hide starts a new turn; an answer that arrives for an older turn is dropped.
  const turn = useRef(0);
  const hide = () => { turn.current++; setValue(null); };
  useEffect(() => {
    if (value === null) return;
    const t = setInterval(() => setLeft(l => { if (l <= 1) { setValue(null); return 0; } return l - 1; }), 1000);
    return () => clearInterval(t);
  }, [value]);
  if (value === null) return <Btn sm onClick={() => {
    const mine = ++turn.current;
    void be.revealSecret(name).then(v => { if (turn.current === mine) { setValue(v); setLeft(30); } }, (e: Error) => toast(e.message));
  }}>Show</Btn>;
  return (
    <span class="confirm">
      <code class="m sel reveal">{value}</code>
      <span class="d m">{left}s</span>
      <Btn sm icon="copy" title="Copy" onClick={() => { copy(value); toast('Copied'); }} />
      <Btn sm onClick={hide}>Hide</Btn>
    </span>
  );
}

/** A new value for a secret, typed in place; the field is emptied when it closes. */
function ReplaceValue({ name }: { name: string }) {
  const { be } = useApp();
  const [open, setOpen] = useState(false);
  const [v, setV] = useState('');
  const close = () => { setOpen(false); setV(''); };
  if (!open) return <Btn sm onClick={() => setOpen(true)}>Replace</Btn>;
  return (
    <span class="confirm">
      <Field type="password" value={v} onInput={setV} label={`New value of ${name}`} placeholder="New value" />
      <Btn sm onClick={close}>Cancel</Btn>
      <Btn sm kind="pri" disabled={!v} onClick={() => { be.replaceSecret(name, v); close(); }}>Save</Btn>
    </span>
  );
}

export function Secrets() {
  const { s, be } = useApp();
  const [adding, setAdding] = useState(false);
  const [n, setN] = useState(''), [note, setNote] = useState(''), [val, setVal] = useState('');
  const taken = s.secrets.some(x => x.name === n.trim());
  const close = () => { setAdding(false); setN(''); setNote(''); setVal(''); };
  const add = () => { be.addSecret(n, note, val); close(); };
  return (
    <Page>
      <Head title="Key vault" acts={!na(s, 'vault') && <Btn sm icon="plus" onClick={() => (adding ? close() : setAdding(true))}>Add</Btn>} />
      <VaultTabs cur="vault" />
      {na(s, 'vault') && <NotYet />}
      {adding && (
        <div class="blk form">
          <div class="fr"><Field value={n} onInput={setN} label="Name" placeholder="name" mono /><Field value={note} onInput={setNote} label="Note" placeholder="Note" /></div>
          <Field type="password" value={val} onInput={setVal} label="Value" placeholder="Value" wide />
          <div class="bf">{taken ? <span class="badc">Exists already</span> : <span />}<Btn sm onClick={close}>Cancel</Btn><Btn sm kind="pri" disabled={!n.trim() || !val || taken} onClick={add}>Save</Btn></div>
        </div>
      )}
      {s.problems.secrets && <div class="line badc">{s.problems.secrets}</div>}
      {!na(s, 'vault') && <Sec>
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'Note', cls: 'x' }, { h: 'In use', cls: 'x' }, { h: 'Last', cls: 'x' }, { h: 'Auto' }, { h: '', cls: 'e' }]}
          rows={s.secrets.map(x => [x.name, x.note, x.usedBy.join(', ') || '–', x.last,
            <Switch on={x.auto} onChange={v => be.setAutoApprove(x.name, v)} label={`Auto-approve ${x.name}`} />,
            <span class="acts"><RevealValue key={`show-${x.name}`} name={x.name} /> <ReplaceValue key={`replace-${x.name}`} name={x.name} /> <Confirm key={`delete-${x.name}`} label="Delete" onYes={() => be.deleteSecret(x.name)} /></span>])} />
      </Sec>}
    </Page>
  );
}

export function Access() {
  const { s, be } = useApp();
  return (
    <Page>
      <Head title="Key vault" />
      <VaultTabs cur="access" />
      {s.problems.requests && <div class="line badc">{s.problems.requests}</div>}
      {na(s, 'vault') ? <NotYet /> : <Sec>
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'VM' }, { h: 'Reason', cls: 'x' }, { h: 'Left', cls: 'm' }, { h: '', cls: 'e' }]}
          rows={s.leases.map(l => [l.secret, l.vmName, l.reason || '–', l.left, na(s, 'take-back') ? '' : <Btn sm onClick={() => be.takeBack(l.id)}>Take back</Btn>])} />
      </Sec>}
    </Page>
  );
}

export function Devices() {
  const { s, be, go } = useApp();
  return (
    <Page>
      <Head title="Key vault" acts={<Btn sm icon="plus" onClick={() => { be.startPairing(); go({ page: 'pair' }); }}>Pair</Btn>} />
      <VaultTabs cur="devices" />
      <Sec>
        <Table cols={[{ h: 'Device' }, { h: 'Kind', cls: 'x' }, { h: 'Added', cls: 'x' }, { h: 'Seen' }, { h: '', cls: 'e' }]}
          rows={s.devices.map(d => [d.name, d.kind, d.added, d.revoked ? '–' : d.seen,
            d.me ? <Tag>this</Tag> : d.revoked ? <Btn sm onClick={() => { be.openRevocation(d.id); go({ page: 'revoke', arg: d.id }); }}>Revoked</Btn>
              : <Confirm key={d.id} label="Revoke" onYes={() => { be.revokeDevice(d.id); go({ page: 'revoke', arg: d.id }); }} />])} />
      </Sec>
      {!na(s, 'vault') && s.vaultModes.length > 0 && <Sec title="Vault on each host">
        <Table cols={[{ h: 'Host' }, { h: 'Mode', cls: 'e' }]} rows={s.vaultModes.map(m => [m.hostName, m.problem ? <span class="badc">{m.problem}</span>
          : <Seg label={`Vault on ${m.hostName}`} opts={['available', 'locked'] as const} labels={{ available: 'Available', locked: 'Locked' }} value={m.mode ?? 'locked'} onChange={v => be.setVaultMode(m.hostId, v)} />])} />
      </Sec>}
    </Page>
  );
}

const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;

export function Pair() {
  const { s, be, go } = useApp();
  const p = s.pairing;
  const [code, setCode] = useState('');
  const back = () => { be.cancelPairing(); go({ page: 'devices' }); };
  return (
    <Page>
      <Head title="Pair a device" back={back} />
      {!p && <div class="line"><Btn kind="pri" icon="plus" onClick={() => be.startPairing()}>Start</Btn></div>}
      {p?.state === 'code' && (
        <div class="blk form narrow">
          <Field value={code} onInput={setCode} label="Code from the new device" placeholder="Code from the new device" mono wide onEnter={() => be.enterPairingCode(code)} />
          {p.problem && <span class="badc">{p.problem}</span>}
          <Btn kind="pri" icon="right" disabled={!code.trim()} onClick={() => be.enterPairingCode(code)}>Next</Btn>
        </div>
      )}
      {p?.state === 'showing' && (
        <>
          <div class="qrbox"><QrSquare seed={p.words.join('')} /><div class="words">{p.words.map(w => <span>{w}</span>)}</div></div>
          <div class="line narrow"><span class="grow">Scan it, or compare the words</span><b class="m fg">{mmss(p.expiresInS)}</b></div>
        </>
      )}
      {p?.state === 'confirm' && (
        <div class="blk warn narrow">
          <div class="bh"><Icon n="phone" /><b>{p.deviceName}</b></div>
          <div class="words row">{p.words.map(w => <span>{w}</span>)}</div>
          <div class="two"><Btn onClick={() => be.confirmPairing(false)}>No match</Btn><Btn kind="pri" onClick={() => be.confirmPairing(true)}>Pair</Btn></div>
        </div>
      )}
      {p?.state === 'done' && (
        <div class="blk narrow"><div class="bh"><Icon n="check" /><b>{p.deviceName} paired</b></div><Btn onClick={back}>Done</Btn></div>
      )}
    </Page>
  );
}

export function Revoke() {
  const { s, be, go, route } = useApp();
  const r = s.revocation && (!route.arg || s.revocation.deviceId === route.arg) ? s.revocation : null;
  const back = () => { be.closeRevocation(); go({ page: 'devices' }); };
  // Opened from a link or after a reload: the device's page reads what to rotate.
  useEffect(() => { if (!r && route.arg && s.devices.some(d => d.id === route.arg && d.revoked)) be.openRevocation(route.arg); }, [r, route.arg, s.devices]);
  if (!r) return <Page><Head title="Revoked" back={back} /></Page>;
  return (
    <Page>
      <Head title={r.deviceName} back={back} />
      <div class="blk bad"><div class="bh"><Icon n="check" /><b>Revoked</b><span>{r.hosts.filter(h => h.confirmed).length} of {r.hosts.length} hosts confirmed</span></div></div>
      <Sec title="Hosts">
        <Table cols={[{ h: 'Host' }, { h: 'State', cls: 'e' }]}
          rows={r.hosts.map(h => [h.name, h.confirmed ? <span class="okc">confirmed</span> : <span class="badc">serves it until it syncs</span>])} />
      </Sec>
      {!na(s, 'vault') && <Sec title="Rotate at their source">
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'Last used', cls: 'x' }, { h: '', cls: 'e' }]}
          rows={r.rotate.map(x => [x.secret, x.last, <Btn sm kind={x.done ? '' : 'pri'} icon={x.done ? 'check' : undefined} onClick={() => be.markRotated(x.secret)}>{x.done ? 'Rotated' : 'Mark rotated'}</Btn>])} />
      </Sec>}
    </Page>
  );
}

export function History() {
  const { s } = useApp();
  return (
    <Page>
      <Head title="Key vault" />
      <VaultTabs cur="history" />
      {s.problems.history && <div class="line badc">{s.problems.history}</div>}
      {na(s, 'vault') ? <NotYet /> : <Sec>
        <Table cols={[{ h: 'Time', cls: 'm' }, { h: 'By' }, { h: 'Secret', cls: 'm' }, { h: 'Event', cls: 'x' }]} rows={s.history.map(h => [h.at, h.vmName, h.secret, h.event])} />
      </Sec>}
    </Page>
  );
}
