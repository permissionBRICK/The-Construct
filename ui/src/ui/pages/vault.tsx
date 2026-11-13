import { useState } from 'preact/hooks';
import { QrSquare, Icon } from '../icons';
import { Btn, Confirm, Field, Head, Page, Sec, Switch, Table, Tabs, Tag } from '../kit';
import { useApp } from '../ctx';
import type { Page as P } from '../../model/route';

/* The key vault: secrets, access (leases), devices with pairing and revocation, history. */

function VaultTabs({ cur }: { cur: P }) {
  const { go } = useApp();
  return <Tabs items={[['vault', 'Secrets'], ['access', 'Access'], ['devices', 'Devices'], ['history', 'History']]} value={cur} onChange={t => go({ page: t })} />;
}

export function Secrets() {
  const { s, be } = useApp();
  const [adding, setAdding] = useState(false);
  const [n, setN] = useState(''), [note, setNote] = useState(''), [val, setVal] = useState('');
  const taken = s.secrets.some(x => x.name === n.trim());
  const add = () => { be.addSecret(n, note, val); setAdding(false); setN(''); setNote(''); setVal(''); };
  return (
    <Page>
      <Head title="Key vault" acts={<Btn sm icon="plus" onClick={() => setAdding(!adding)}>Add</Btn>} />
      <VaultTabs cur="vault" />
      {adding && (
        <div class="blk form">
          <div class="fr"><Field value={n} onInput={setN} label="Name" placeholder="name" mono /><Field value={note} onInput={setNote} label="Note" placeholder="Note" /></div>
          <Field type="password" value={val} onInput={setVal} label="Value" placeholder="Value" wide />
          <div class="bf">{taken ? <span class="badc">Exists already</span> : <span />}<Btn sm onClick={() => setAdding(false)}>Cancel</Btn><Btn sm kind="pri" disabled={!n.trim() || !val || taken} onClick={add}>Save</Btn></div>
        </div>
      )}
      <Sec>
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'Note', cls: 'x' }, { h: 'In use', cls: 'x' }, { h: 'Last', cls: 'x' }, { h: 'Auto' }, { h: '', cls: 'e' }]}
          rows={s.secrets.map(x => [x.name, x.note, x.usedBy.join(', ') || '–', x.last,
            <Switch on={x.auto} onChange={v => be.setAutoApprove(x.name, v)} label={`Auto-approve ${x.name}`} />,
            <Confirm label="Delete" onYes={() => be.deleteSecret(x.name)} />])} />
      </Sec>
    </Page>
  );
}

export function Access() {
  const { s, be } = useApp();
  return (
    <Page>
      <Head title="Key vault" />
      <VaultTabs cur="access" />
      <Sec>
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'VM' }, { h: 'Reason', cls: 'x' }, { h: 'Left', cls: 'm' }, { h: '', cls: 'e' }]}
          rows={s.leases.map(l => [l.secret, l.vmName, l.reason, l.left, <Btn sm onClick={() => be.takeBack(l.id)}>Take back</Btn>])} />
      </Sec>
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
            d.me ? <Tag>this</Tag> : d.revoked ? <Tag>revoked</Tag> : <Confirm label="Revoke" onYes={() => { be.revokeDevice(d.id); go({ page: 'revoke', arg: d.id }); }} />])} />
      </Sec>
      <Sec title="Hosts with a copy">
        <Table cols={[{ h: 'Host' }, { h: 'State', cls: 'e' }]} rows={s.hosts.map(h => [h.name, 'in sync'])} />
      </Sec>
    </Page>
  );
}

const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;

export function Pair() {
  const { s, be, go } = useApp();
  const p = s.pairing;
  const back = () => { be.cancelPairing(); go({ page: 'devices' }); };
  return (
    <Page>
      <Head title="Pair a device" back={back} />
      {!p && <div class="line"><Btn kind="pri" icon="plus" onClick={() => be.startPairing()}>Start</Btn></div>}
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
  const { s, be, go } = useApp();
  const r = s.revocation;
  const back = () => { be.closeRevocation(); go({ page: 'devices' }); };
  if (!r) return <Page><Head title="Revoked" back={back} /></Page>;
  return (
    <Page>
      <Head title={r.deviceName} back={back} />
      <div class="blk bad"><div class="bh"><Icon n="check" /><b>Revoked</b><span>{r.hosts.filter(h => h.confirmed).length} of {r.hosts.length} hosts confirmed</span></div></div>
      <Sec title="Hosts">
        <Table cols={[{ h: 'Host' }, { h: 'State', cls: 'e' }]}
          rows={r.hosts.map(h => [h.name, h.confirmed ? <span class="okc">confirmed</span> : <span class="badc">serves it until it syncs</span>])} />
      </Sec>
      <Sec title="Rotate">
        <Table cols={[{ h: 'Secret', cls: 'm' }, { h: 'Last used', cls: 'x' }, { h: '', cls: 'e' }]}
          rows={r.rotate.map(x => [x.secret, x.last, <Btn sm kind={x.done ? '' : 'pri'} icon={x.done ? 'check' : undefined} onClick={() => be.markRotated(x.secret)}>{x.done ? 'Rotated' : 'Mark rotated'}</Btn>])} />
      </Sec>
    </Page>
  );
}

export function History() {
  const { s } = useApp();
  return (
    <Page>
      <Head title="Key vault" />
      <VaultTabs cur="history" />
      <Sec>
        <Table cols={[{ h: 'Time', cls: 'm' }, { h: 'VM' }, { h: 'Secret', cls: 'm' }, { h: 'Event', cls: 'x' }]} rows={s.history.map(h => [h.at, h.vmName, h.secret, h.event])} />
      </Sec>
    </Page>
  );
}
