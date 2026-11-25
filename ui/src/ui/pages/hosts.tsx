import { useEffect, useRef, useState } from 'preact/hooks';
import type { Host, HostUser, HostVm } from '../../backend/types';
import { hostUpdateTarget } from '../../model/vm';
import { DEFAULT_WINDOWS_DISK_DIR, dirOfWindowsPath, normalizeWindowsDir, sameWindowsDir, windowsDirError } from '../../model/storage';
import {
  buildConfigPayload, channelChoice, DEDICATED_HOST_CONFIG_SECTIONS, HOST_CONFIG_SECTIONS, HOST_CONFIG_SPEC_BY_KEY,
  parseConfigSection, toFieldDrafts, visibleFields,
  type FieldDrafts, type HostConfigField, type HostConfigSectionSpec,
} from '../../model/hostConfig';
import { Icon } from '../icons';
import { Btn, Confirm, Copyable, Dot, Field, Head, KV, Meter, Modal, Page, Row, Sec, Seg, Switch, Table, Tabs, Tag, UpTag } from '../kit';
import { MoveDiskRow } from '../blocks';
import { na, useApp } from '../ctx';

/* Hosts: the list, and host administration for hosts the user administers. Every feature of today's host
   administration, as simple pages with tables: overview with capacity and the one Update button, VMs, users
   with limits and tokens, token usage, media, operations, configuration with the certificate. */

export function Hosts() {
  const { s, go, surf } = useApp();
  return (
    <Page>
      <Head title="Hosts" back={surf === 'phone' ? undefined : undefined} />
      <Sec>
        <Table cols={[{ h: 'Host' }, { h: 'Kind', cls: 'x' }, { h: 'Version', cls: 'm' }, { h: 'Channel', cls: 'x' }, { h: 'Role', cls: 'x' }, { h: 'VMs', cls: 'e' }]}
          rows={s.hosts.map(h => { const to = hostUpdateTarget(s, h); return [<>{h.name}{h.problem && <> <span class="badc">{h.problem}</span></>}</>, h.kind, <>{h.version}{to && <> <UpTag v={to} /></>}</>, h.channel, h.admin ? 'admin' : 'user', h.vmCount]; })}
          onRow={i => go({ page: 'host', arg: s.hosts[i].id })} />
      </Sec>
    </Page>
  );
}

type HTab = 'overview' | 'vms' | 'users' | 'usage' | 'media' | 'ops' | 'config';
const HTABS: [HTab, string][] = [['overview', 'Overview'], ['vms', 'VMs'], ['users', 'Users'], ['usage', 'Usage'], ['media', 'Media'], ['ops', 'Operations'], ['config', 'Configuration']];

export function HostPage({ id }: { id?: string }) {
  const { s, route, go } = useApp();
  const h = s.hosts.find(x => x.id === id);
  if (!h) return <Page><Head title="Hosts" back={() => go({ page: 'hosts' })} /></Page>;
  const back = () => go({ page: 'hosts' });
  const problem = h.problem ? <div class="line badc">{h.problem}</div> : null;
  if (!h.admin) {
    return (
      <Page>
        <Head title={h.name} back={back} />
        {problem}
        <Sec>
          <KV k="Kind">{h.kind}</KV>
          <KV k="Version"><span class="m">{h.version}</span></KV>
          <KV k="Channel">{h.channel}</KV>
          <KV k="Address"><span class="m">{h.address}</span></KV>
          <KV k="Your VMs">{s.vms.filter(v => v.hostId === h.id).map(v => v.name).join(', ') || '–'}</KV>
        </Sec>
      </Page>
    );
  }
  const tab = (route.tab as HTab) || 'overview';
  const View = { overview: HOverview, vms: HVms, users: HUsers, usage: HUsage, media: HMedia, ops: HOps, config: HConfig }[tab];
  return (
    <Page>
      <Head title={h.name} back={back} acts={<UpdateHost h={h} />} />
      {problem}
      <Tabs items={HTABS} value={tab} onChange={t => go({ page: 'host', arg: h.id, tab: t })} />
      <View h={h} />
    </Page>
  );
}

/** One Update button, in place of today's check, stage, apply, resume, cancel and resolve. */
function UpdateHost({ h }: { h: Host }) {
  const { s, be } = useApp();
  if (h.updating) return <Tag warn>updating</Tag>;
  const to = hostUpdateTarget(s, h);
  if (!to) return null;
  return <Btn sm kind="amber" icon="up" onClick={() => be.updateHost(h.id)}>Update to {to}</Btn>;
}

const pct = (a: [number, number]) => Math.round((a[0] / a[1]) * 100);

function HOverview({ h }: { h: Host }) {
  const { be } = useApp();
  const ok = (v: string) => <span class={v === 'ok' ? 'okc' : 'badc'}>{v}</span>;
  return (
    <>
      <div class="two">
        <Sec title="Host">
          <KV k="Kind">{h.kind}</KV>
          <KV k="Version"><span class="m">{h.version}</span></KV>
          <KV k="Channel">{h.channel}</KV>
          <KV k="Address"><span class="m">{h.address}</span></KV>
        </Sec>
        <Sec title="Health">
          <KV k="Hypervisor">{ok(h.health.hypervisor)}</KV>
          <KV k="Database">{ok(h.health.database)}</KV>
          <KV k="Media">{ok(h.health.media)}</KV>
          <KV k="Inventory">{ok(h.health.inventory)}</KV>
        </Sec>
      </div>
      <Sec title="Capacity" right={<Tag>{h.capacity.mode}</Tag>}>
        <KV k="Memory">{h.capacity.ram[0]} / {h.capacity.ram[1]} GB <Meter pct={pct(h.capacity.ram)} /></KV>
        <KV k="CPU">{h.capacity.cpu[0]} / {h.capacity.cpu[1]} vCPUs <Meter pct={pct(h.capacity.cpu)} /></KV>
        <KV k="Disk">{h.capacity.disk[0]} / {h.capacity.disk[1]} TB <Meter pct={pct(h.capacity.disk)} /></KV>
      </Sec>
      <Sec title="Running jobs">
        <Table cols={[{ h: 'Job', cls: 'm' }, { h: 'Kind' }, { h: 'VM' }, { h: 'Owner', cls: 'x' }, { h: 'Phase', cls: 'e' }]}
          rows={h.jobs.filter(j => j.state === 'running').map(j => [j.id, j.kind, j.vm, j.owner, j.phase])} />
      </Sec>
      <Sec title="Updates" right={<Btn sm icon="repro" onClick={() => be.updateHostVms(h.id)}>Update all VMs on this host</Btn>}>
        <Table cols={[{ h: 'When' }, { h: 'From', cls: 'm' }, { h: 'To', cls: 'm' }, { h: 'By', cls: 'x' }, { h: 'Result', cls: 'e' }]}
          rows={h.updates.map(u => [u.at, u.from, u.to, u.by, u.state])} />
      </Sec>
    </>
  );
}

function HVms({ h }: { h: Host }) {
  const { s, be } = useApp();
  const [open, setOpen] = useState<string | null>(null);
  const act = (vm: string, a: Parameters<typeof be.hostVmAction>[2]) => be.hostVmAction(h.id, vm, a);
  const rows = h.vms.map(v => {
    const on = v.power === 'on';
    return [
      <><Dot s={on ? 'on' : 'off'} />{v.name}{v.role === 'child' && <> <Tag>child</Tag></>}{v.shared && <> <Tag>shared</Tag></>}</>,
      v.owner,
      <span class="m">{on ? `${v.cpuPct} %` : '–'}</span>,
      <span class="m">{v.cpus} · {v.memGb}/{v.memMaxGb} GB · {v.diskGb} GB</span>,
      <span class="m">{v.lease}</span>,
      <span class="acts">
        <Btn sm onClick={() => act(v.name, 'console')} disabled={na(s, 'console')}>Console</Btn>
        {on ? <Confirm label="Shut down" onYes={() => act(v.name, 'shutdown')} /> : <Btn sm onClick={() => act(v.name, 'start')}>Start</Btn>}
        <Btn sm icon="set" title="Settings" onClick={() => setOpen(open === v.name ? null : v.name)} />
      </span>,
    ];
  });
  const sel = h.vms.find(v => v.name === open);
  const selKids = sel ? h.vms.filter(c => c.parent === sel.name) : [];
  return (
    <>
      <Sec>
        <Table cols={[{ h: 'VM' }, { h: 'Owner', cls: 'x' }, { h: 'CPU', cls: 'x' }, { h: 'vCPU · RAM · Disk', cls: 'x' }, { h: 'Lifetime', cls: 'x' }, { h: '', cls: 'e' }]} rows={rows} />
      </Sec>
      {sel && <HostVmSettings key={sel.name} h={h} v={sel} kids={selKids} onClose={() => setOpen(null)} />}
    </>
  );
}

/** Deleting a primary with children lists them and asks for the name, in place. */
function DeleteVm({ v, kids, onYes }: { v: HostVm; kids: HostVm[]; onYes: () => void }) {
  const [asking, setAsking] = useState(false);
  const [typed, setTyped] = useState('');
  if (!kids.length) return <Confirm label="Delete" icon="trash" onYes={onYes} />;
  if (!asking) return <Btn sm icon="trash" onClick={() => setAsking(true)}>Delete</Btn>;
  return (
    <span class="confirm col">
      <span class="d">{kids.map(k => k.name).join(', ')}</span>
      <Field value={typed} onInput={setTyped} label={`Type ${v.name}`} placeholder={v.name} mono />
      <span class="confirm"><Btn sm onClick={() => { setAsking(false); setTyped(''); }}>Cancel</Btn><Btn sm kind="red" disabled={typed !== v.name} onClick={onYes}>Delete all</Btn></span>
    </span>
  );
}

function HostVmSettings({ h, v, kids, onClose }: { h: Host; v: HostVm; kids: HostVm[]; onClose: () => void }) {
  const { s, be } = useApp();
  const [cpus, setCpus] = useState(String(v.cpus));
  const [mem, setMem] = useState(String(v.memMaxGb));
  const [net, setNet] = useState(v.network);
  const act = (a: Parameters<typeof be.hostVmAction>[2]) => be.hostVmAction(h.id, v.name, a);
  return (
    <Sec title={v.name} right={<Btn sm icon="x" title="Close" onClick={onClose} />}>
      {v.role === 'primary' && <Row l="Network"><Seg label="Network" opts={['default', 'relayed', 'direct'] as const} labels={{ default: 'Host default', relayed: 'Relayed', direct: 'Direct' }} value={net} onChange={setNet} /></Row>}
      <Row l="vCPUs"><Field value={cpus} onInput={setCpus} label="vCPUs" mono /></Row>
      <Row l={<>Memory <span class="d">GB</span></>}><Field value={mem} onInput={setMem} label="Memory" mono /></Row>
      <Row l="Address"><span class="m">{v.address}</span></Row>
      {h.storageMoveSupported && <MoveDiskRow hostId={h.id} vmName={v.name} diskPath={v.diskPath} hostDefaultPath={h.vmStorageRoot} />}
      {v.role === 'child' && <Row l={<>Lifetime <span class="d m">{v.lease}</span></>}><Btn sm onClick={() => act('renew-lease')}>Restart countdown</Btn></Row>}
      {v.role === 'child' && <Row l="Shared"><Switch on={v.shared} onChange={() => act('toggle-shared')} label="Shared" /></Row>}
      {v.role === 'primary' && <Row l="VM token"><Confirm label="Rotate" disabled={na(s, 'vm-token')} onYes={() => act('rotate-token')} /></Row>}
      {v.power === 'on' && <Row l="Restart"><Confirm label="Restart" danger={false} onYes={() => act('restart')} /></Row>}
      <Row l="Delete"><DeleteVm v={v} kids={kids} onYes={() => { act('delete'); onClose(); }} /></Row>
      <div class="bf pad"><span /><Btn sm onClick={onClose}>Cancel</Btn><Btn sm kind="pri" onClick={() => { be.saveHostVm(h.id, v.name, { cpus: +cpus || v.cpus, memMaxGb: +mem || v.memMaxGb, network: net }); onClose(); }}>Apply</Btn></div>
    </Sec>
  );
}

function HUsers({ h }: { h: Host }) {
  const { s, be } = useApp();
  const [edit, setEdit] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [role, setRole] = useState<'user' | 'admin'>('user');
  const [maxVms, setMaxVms] = useState('1');
  const [fwd, setFwd] = useState<HostUser['hostForwards']>('default');
  const hand = s.handout?.hostId === h.id ? s.handout : null;
  const add = () => { be.addUser(h.id, { name, role, maxVms: +maxVms || 1, hostForwards: fwd }); setName(''); };
  const u = h.users.find(x => x.name === edit);
  return (
    <>
      {hand && (
        <div class="blk ok handout">
          <div class="bh"><Icon n="check" /><b>{hand.user}</b><Btn sm icon="x" title="Close" onClick={() => be.closeHandout()} /></div>
          <div class="cap"><span>Host address</span></div>
          <Copyable text={hand.address} />
          {hand.token && <><div class="cap"><span>Token, shown once</span></div><Copyable text={hand.token} /></>}
          {!hand.token && <div class="line">Windows sign-in</div>}
        </div>
      )}
      <Sec title="Add user">
        <div class="fr pad">
          <Field value={name} onInput={setName} label="User name" placeholder="name or DOMAIN\user" mono onEnter={add} />
          <Seg label="Role" opts={['user', 'admin'] as const} value={role} onChange={setRole} />
          <label class="lab">Max VMs <Field value={maxVms} onInput={setMaxVms} label="Max VMs" mono /></label>
          <Seg label="Host forwards" opts={['default', 'allowed', 'refused'] as const} labels={{ default: 'Forwards: default', allowed: 'allowed', refused: 'refused' }} value={fwd} onChange={setFwd} />
          <Btn sm kind="pri" icon="plus" disabled={!name.trim()} onClick={add}>Add</Btn>
        </div>
      </Sec>
      <Sec title="Users">
        <Table cols={[{ h: 'User' }, { h: 'Role', cls: 'x' }, { h: 'VMs' }, { h: 'Tokens', cls: 'x m' }, { h: 'This month', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={h.users.map(x => [<>{x.name}{!x.enabled && <> <Tag>disabled</Tag></>}</>, x.role, `${x.primaries} + ${x.children} / ${x.maxVms}`, x.tokens.length, x.tokensMonth,
            <Btn sm icon="edit" onClick={() => setEdit(edit === x.name ? null : x.name)}>Edit</Btn>])} />
      </Sec>
      {u && <UserEdit key={u.name} h={h} u={u} onClose={() => setEdit(null)} />}
    </>
  );
}

function UserEdit({ h, u, onClose }: { h: Host; u: HostUser; onClose: () => void }) {
  const { be } = useApp();
  const [d, setD] = useState<HostUser>(structuredClone(u));
  const [label, setLabel] = useState('');
  const set = (p: Partial<HostUser>) => setD({ ...d, ...p });
  const al = (p: Partial<HostUser['allowance']>) => setD({ ...d, allowance: { ...d.allowance, ...p } });
  const num = (v: number | null) => (v === null ? '' : String(v));
  const parse = (v: string) => (v.trim() === '' ? null : +v);
  return (
    <Sec title={u.name} right={<Btn sm icon="x" title="Close" onClick={onClose} />}>
      <Row l="Role"><Seg label="Role" opts={['user', 'admin'] as const} value={d.role} onChange={role => set({ role })} /></Row>
      <Row l="Enabled"><Switch on={d.enabled} onChange={enabled => set({ enabled })} label="Enabled" /></Row>
      <Row l="Max VMs"><Field value={String(d.maxVms)} onInput={v => set({ maxVms: +v || 0 })} label="Max VMs" mono /></Row>
      <Row l="Host forwards"><Seg label="Host forwards" opts={['default', 'allowed', 'refused'] as const} value={d.hostForwards} onChange={hostForwards => set({ hostForwards })} /></Row>
      <Row l="HTTPS forwards"><Switch on={d.httpsForwards} onChange={httpsForwards => set({ httpsForwards })} label="HTTPS forwards" /></Row>
      <Row l="Nested virtualization"><Seg label="Nested" opts={['inherit', 'on', 'off'] as const} value={d.nested} onChange={nested => set({ nested })} /></Row>
      <div class="cap"><span>Allowance</span></div>
      <Row l="Child VMs"><Switch on={d.allowance.childVms} onChange={childVms => al({ childVms })} label="Child VMs" /></Row>
      <Row l="Kept children"><Field value={String(d.allowance.maxChildren)} onInput={v => al({ maxChildren: +v || 0 })} label="Kept children" mono /></Row>
      <Row l={<>CPU budget <span class="d">vCPU</span></>}><Field value={num(d.allowance.cpu)} onInput={v => al({ cpu: parse(v) })} label="CPU budget" placeholder="host default" mono /></Row>
      <Row l={<>RAM budget <span class="d">GB</span></>}><Field value={num(d.allowance.ramGb)} onInput={v => al({ ramGb: parse(v) })} label="RAM budget" placeholder="host default" mono /></Row>
      <Row l={<>Storage budget <span class="d">GB</span></>}><Field value={num(d.allowance.diskGb)} onInput={v => al({ diskGb: parse(v) })} label="Storage budget" placeholder="host default" mono /></Row>
      <Row l="Longest child lifetime"><Field value={d.allowance.maxLifetime} onInput={maxLifetime => al({ maxLifetime })} label="Longest child lifetime" placeholder="3d" mono /></Row>
      <Row l="Lifetime never"><Switch on={d.allowance.neverLifetime} onChange={neverLifetime => al({ neverLifetime })} label="Lifetime never" /></Row>
      <Row l="Sharing"><Switch on={d.allowance.sharing} onChange={sharing => al({ sharing })} label="Sharing" /></Row>
      <div class="bf pad"><Confirm label="Remove user" icon="trash" onYes={() => { be.removeUser(h.id, u.name); onClose(); }} /><span /><Btn sm onClick={onClose}>Cancel</Btn><Btn sm kind="pri" onClick={() => { be.saveUser(h.id, d); onClose(); }}>Save</Btn></div>
      <div class="cap"><span>Tokens</span></div>
      <Table cols={[{ h: 'Id', cls: 'm' }, { h: 'Label' }, { h: 'Created', cls: 'x' }, { h: 'Last used', cls: 'x' }, { h: '', cls: 'e' }]}
        rows={u.tokens.map(t => [t.id, t.label, t.created, t.lastUsed, <Confirm label="Revoke" onYes={() => be.revokeToken(h.id, u.name, t.id)} />])} />
      <div class="line"><Field value={label} onInput={setLabel} label="Token label" placeholder="label" /><Btn sm icon="plus" onClick={() => { be.issueToken(h.id, u.name, label); setLabel(''); }}>Issue token</Btn></div>
    </Sec>
  );
}

function HUsage({ h }: { h: Host }) {
  const { be } = useApp();
  return (
    <>
      <div class="line"><Seg label="Window" opts={['today', 'month', 'all'] as const} labels={{ today: 'Today', month: 'Month', all: 'All' }} value={h.usage.window} onChange={w => be.setUsageWindow(h.id, w)} /></div>
      <Sec title="By user">
        <Table cols={[{ h: 'User' }, { h: 'VMs', cls: 'x' }, { h: 'Tokens', cls: 'm' }, { h: 'Cost', cls: 'e m' }]} rows={h.usage.byUser.map(u => [u.user, u.vms, u.tokens, u.cost])} />
      </Sec>
      <Sec title="By VM">
        <Table cols={[{ h: 'VM' }, { h: 'Owner', cls: 'x' }, { h: 'Tokens', cls: 'm' }, { h: 'Cost', cls: 'e m' }]} rows={h.usage.byVm.map(u => [u.vm, u.owner, u.tokens, u.cost])} />
      </Sec>
    </>
  );
}

function HMedia({ h }: { h: Host }) {
  const { be } = useApp();
  const [prod, setProd] = useState<'Windows 11' | 'Server 2022' | 'Server 2025'>('Windows 11');
  const [ed, setEd] = useState('Pro');
  const [lang, setLang] = useState('en-us');
  const [key, setKey] = useState('');
  const [kind, setKind] = useState<'Retail' | 'MAK' | 'KMS'>('Retail');
  const editions = prod === 'Windows 11' ? ['Pro', 'Enterprise', 'Education'] : ['Standard', 'Datacenter'];
  return (
    <>
      <Sec title="Construct images">
        <Table cols={[{ h: 'Image', cls: 'm' }, { h: 'Size', cls: 'x m' }, { h: '', cls: 'e' }]} rows={h.isos.map(i => [i.name, i.size, i.current ? <Tag>current</Tag> : ''])} />
      </Sec>
      <Sec title="Media" right={<Btn sm icon="repro" onClick={() => be.cleanupMedia(h.id)}>Clean up</Btn>}>
        <Table cols={[{ h: 'Name', cls: 'm' }, { h: 'Owner', cls: 'x' }, { h: 'State' }, { h: 'Size', cls: 'x m' }, { h: 'Used by', cls: 'x' }, { h: '', cls: 'e' }]}
          rows={h.media.map(m => [m.name, m.owner, m.state, m.size, m.refs, m.refs ? '' : <Confirm label="Delete" onYes={() => be.deleteMedia(h.id, m.name)} />])} />
      </Sec>
      <Sec title="Windows media">
        <div class="fr pad">
          <Seg label="Product" opts={['Windows 11', 'Server 2022', 'Server 2025'] as const} value={prod} onChange={p => { setProd(p); setEd(p === 'Windows 11' ? 'Pro' : 'Standard'); }} />
          <Seg label="Edition" opts={editions} value={ed} onChange={setEd} />
          <Field value={lang} onInput={setLang} label="Language" mono />
          <Btn sm icon="save" onClick={() => be.acquireWindows(h.id, prod, ed, lang)}>Download</Btn>
        </div>
      </Sec>
      <Sec title="License keys">
        <Table cols={[{ h: 'Product' }, { h: 'Edition', cls: 'x' }, { h: 'Kind', cls: 'x' }, { h: 'Key', cls: 'm' }, { h: 'Used', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={h.winKeys.map(k => [k.product, k.edition, k.kind, k.partial, k.used, k.held ? <Tag>in use</Tag> : <Confirm label="Remove" onYes={() => be.removeWinKey(h.id, k.id)} />])} />
        <div class="fr pad">
          <Seg label="Key kind" opts={['Retail', 'MAK', 'KMS'] as const} value={kind} onChange={setKind} />
          <Field type="password" value={key} onInput={setKey} label="Product key" placeholder="Product key" mono />
          <Btn sm icon="plus" disabled={key.trim().length < 5} onClick={() => { be.addWinKey(h.id, { product: prod, edition: ed, kind, key }); setKey(''); }}>Add key</Btn>
        </div>
      </Sec>
      <Sec title="Windows guests">
        <Table cols={[{ h: 'VM' }, { h: 'Stage', cls: 'x' }, { h: 'Activation' }, { h: 'Key', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={h.winGuests.map(g => {
            const k = h.winKeys.find(x => x.id === g.keyId);
            const free = h.winKeys.filter(x => !x.held);
            return [g.name, g.stage, <span class={g.activation === 'activated' ? 'okc' : 'badc'}>{g.activation}</span>, k ? k.partial.slice(-5) : '–',
              k ? (g.activation !== 'activated' ? <Btn sm onClick={() => be.activateWindows(h.id, g.name)}>Activate again</Btn> : '')
                : free.length ? <span class="confirm">{free.map(x => <Btn sm onClick={() => be.assignWinKey(h.id, g.name, x.id)}>{x.edition} {x.partial.slice(-5)}</Btn>)}</span> : ''];
          })} />
      </Sec>
    </>
  );
}

function HOps({ h }: { h: Host }) {
  const { s, be } = useApp();
  return (
    <>
      <Sec title="Jobs">
        <Table cols={[{ h: 'Job', cls: 'm' }, { h: 'Kind' }, { h: 'VM', cls: 'x' }, { h: 'Owner', cls: 'x' }, { h: 'State' }, { h: 'Phase', cls: 'x' }, { h: 'Created', cls: 'x' }, { h: '', cls: 'e' }]}
          rows={h.jobs.map(j => [j.id, j.kind, j.vm, j.owner, <span class={j.state === 'failed' ? 'badc' : ''}>{j.state}</span>, j.phase, j.created,
            j.state === 'running' ? <Confirm label="Cancel" onYes={() => be.cancelHostJob(h.id, j.id)} /> : j.state === 'failed' && j.retry && !na(s, 'host-retry') ? (j.kind === 'reinstall'
              ? <Confirm key={`retry-${j.id}`} label="Retry" icon="retry" verb="Reinstall again" onYes={() => be.retryHostJob(h.id, j.id)} />
              : <Btn sm icon="retry" onClick={() => be.retryHostJob(h.id, j.id)}>Retry</Btn>) : ''])} />
      </Sec>
      <Sec title="Audit">
        <Table cols={[{ h: 'At' }, { h: 'Actor' }, { h: 'Action', cls: 'm' }, { h: 'Target', cls: 'e' }]} rows={h.audit.map(a => [a.at, a.actor, a.action, a.target])} />
      </Sec>
    </>
  );
}

function HConfig({ h }: { h: Host }) {
  const { be } = useApp();
  const byName = new Map(h.config.map(c => [c.name, c]));
  const known = HOST_CONFIG_SECTIONS
    .map(spec => ({ spec, item: byName.get(spec.key) }))
    .filter((x): x is { spec: HostConfigSectionSpec; item: Host['config'][number] } => !!x.item);
  const unknown = h.config.filter(c => !DEDICATED_HOST_CONFIG_SECTIONS.has(c.name) && !HOST_CONFIG_SPEC_BY_KEY[c.name]);
  return (
    <>
      {h.storageMoveSupported && <HostStoragePath h={h} />}
      {h.repairAuto !== undefined && (
        <Sec title="Repair">
          <Row l="Repair agent starts by itself"><Switch on={h.repairAuto} onChange={on => be.setRepairAuto(h.id, on)} label="Repair agent starts by itself" /></Row>
          {h.repairChat !== undefined && (
            <Row l="Repair in Construct, not T3 Code"><Switch on={h.repairChat} onChange={on => be.setRepairChat(h.id, on)} label="Repair in Construct, not T3 Code" /></Row>
          )}
        </Sec>
      )}
      {known.map(({ spec, item }) => (
        <HostConfigSection key={`${h.id}:${spec.key}`} h={h} spec={spec} item={item} />
      ))}
      {unknown.map(item => (
        <RawConfigSection key={`${h.id}:${item.name}`} h={h} item={item} />
      ))}
      <Certificate h={h} />
    </>
  );
}

/** GUI settings card for one host configuration section. */
function HostConfigSection({ h, spec, item }: { h: Host; spec: HostConfigSectionSpec; item: Host['config'][number] }) {
  const { be } = useApp();
  const parsed = parseConfigSection(item.json);
  if (!parsed) return <RawConfigSection h={h} item={item} />;
  const fields = visibleFields(spec, h.kind, parsed);
  const initial = toFieldDrafts(fields, parsed);
  const [drafts, setDrafts] = useState<FieldDrafts>(initial);
  const [err, setErr] = useState<string | null>(null);
  const syncKey = `${h.id}\0${h.kind}\0${spec.key}\0${item.json}`;
  const synced = useRef(syncKey);

  useEffect(() => {
    if (synced.current === syncKey) return;
    synced.current = syncKey;
    const p = parseConfigSection(item.json);
    if (!p) return;
    setDrafts(toFieldDrafts(visibleFields(spec, h.kind, p), p));
    setErr(null);
  }, [syncKey]);

  const setField = (key: string, value: string | boolean | null) => {
    setDrafts(prev => ({ ...prev, [key]: value }));
    setErr(null);
  };

  const dirty = fields.some(f => drafts[f.key] !== initial[f.key]);
  const reset = () => { setDrafts(initial); setErr(null); };
  const save = () => {
    const res = buildConfigPayload(spec, fields, parsed, drafts);
    if (res.error !== null) { setErr(res.error); return; }
    const e = be.saveConfig(h.id, spec.key, JSON.stringify(res.value, null, 2));
    setErr(e);
    if (!e) setDrafts(toFieldDrafts(fields, res.value));
  };

  const right = dirty
    ? <Tag warn>unsaved</Tag>
    : item.updated && item.updated !== 'default' && item.updated !== '–'
      ? <span class="d">{item.updated}</span>
      : null;

  return (
    <Sec title={spec.label} right={right}>
      {fields.map(f => (
        <HostConfigFieldRow key={f.key} field={f} value={drafts[f.key]} onChange={v => setField(f.key, v)} onEnter={dirty ? save : undefined} />
      ))}
      {err && <div class="line badc">{err}</div>}
      {dirty && (
        <div class="bf pad">
          <span />
          <Btn sm onClick={reset}>Reset</Btn>
          <Btn sm kind="pri" onClick={save}>Save</Btn>
        </div>
      )}
    </Sec>
  );
}

function HostConfigFieldRow({ field, value, onChange, onEnter }: {
  field: HostConfigField;
  value: string | boolean | null | undefined;
  onChange: (v: string | boolean | null) => void;
  onEnter?: () => void;
}) {
  let control;
  switch (field.type) {
    case 'bool':
      if (field.nullable) {
        const cur = value === true ? 'true' : value === false ? 'false' : 'unset';
        control = (
          <Seg
            label={field.label}
            opts={['unset', 'true', 'false'] as const}
            labels={{ unset: 'No cap', true: 'Allow', false: 'Deny' }}
            value={cur}
            onChange={v => onChange(v === 'unset' ? null : v === 'true')}
          />
        );
      } else {
        control = <Switch on={Boolean(value)} onChange={onChange} label={field.label} />;
      }
      break;

    case 'enum':
      control = (
        <Seg
          label={field.label}
          opts={field.options}
          labels={field.labels}
          value={typeof value === 'string' && field.options.includes(value) ? value : field.options[0]}
          onChange={onChange}
        />
      );
      break;

    case 'channel': {
      const strVal = typeof value === 'string' ? value : 'stable';
      const choice = channelChoice(strVal);
      control = (
        <div class="cfg-chan">
          <Seg
            label={field.label}
            opts={['stable', 'nightly', 'custom'] as const}
            labels={{ stable: 'Stable', nightly: 'Nightly', custom: 'Custom URL' }}
            value={choice}
            onChange={c => {
              if (c === 'stable' || c === 'nightly') onChange(c);
              else onChange(choice === 'custom' ? strVal : 'https://');
            }}
          />
          {choice === 'custom' && (
            <Field
              value={strVal}
              onInput={onChange}
              label="Custom update manifest URL"
              placeholder="https://example.net/manifest.json"
              mono
              onEnter={onEnter}
            />
          )}
        </div>
      );
      break;
    }

    case 'int':
    case 'bytes':
      control = (
        <span class="cfg-unit">
          <Field
            value={typeof value === 'string' ? value : ''}
            onInput={onChange}
            label={field.label}
            placeholder={field.nullable ? (field.nullLabel ?? 'Default') : undefined}
            mono
            onEnter={onEnter}
          />
          {field.unit && <span class="u">{field.unit}</span>}
        </span>
      );
      break;

    case 'string':
      control = (
        <Field
          value={typeof value === 'string' ? value : ''}
          onInput={onChange}
          label={field.label}
          placeholder={field.placeholder ?? (field.nullable ? field.nullLabel : undefined)}
          mono
          onEnter={onEnter}
        />
      );
      break;
  }

  return (
    <div class="r cfg-row">
      <span class="rl">
        <span>{field.label}</span>
        <small>{field.help}</small>
      </span>
      <span class="rr">{control}</span>
    </div>
  );
}

/** Fallback editor for any future config section not yet described in `HOST_CONFIG_SECTIONS`. */
function RawConfigSection({ h, item }: { h: Host; item: Host['config'][number] }) {
  const { be } = useApp();
  const [text, setText] = useState(item.json);
  const [err, setErr] = useState<string | null>(null);
  const syncKey = `${h.id}\0${item.name}\0${item.json}`;
  const synced = useRef(syncKey);
  useEffect(() => {
    if (synced.current === syncKey) return;
    synced.current = syncKey;
    setText(item.json);
    setErr(null);
  }, [syncKey]);
  const dirty = text !== item.json;
  return (
    <Sec title={item.name} right={dirty ? <Tag warn>unsaved</Tag> : <span class="d">{item.updated}</span>}>
      <textarea class="code edit" spellcheck={false} aria-label={`${item.name} configuration`} value={text} onInput={e => { setText(e.currentTarget.value); setErr(null); }} rows={Math.max(3, text.split('\n').length + 1)} />
      {err && <div class="line badc">{err}</div>}
      {dirty && (
        <div class="bf pad">
          <span />
          <Btn sm onClick={() => { setText(item.json); setErr(null); }}>Reset</Btn>
          <Btn sm kind="pri" onClick={() => setErr(be.saveConfig(h.id, item.name, text))}>Save</Btn>
        </div>
      )}
    </Sec>
  );
}

/** Host default VM disk folder (`storage.vmStorageRoot`), with a confirmation popup offering to auto-move all existing VMs when changed. */
function HostStoragePath({ h }: { h: Host }) {
  const { be } = useApp();
  const saved = h.vmStorageRoot ?? '';
  const [value, setValue] = useState(saved);
  const [err, setErr] = useState<string | null>(null);
  const [pending, setPending] = useState<{ path: string | null; display: string; movable: number } | null>(null);
  const syncKey = `${h.id}\0${h.vmStorageRoot ?? ''}`;
  const synced = useRef(syncKey);
  useEffect(() => {
    if (synced.current === syncKey) return;
    synced.current = syncKey;
    setValue(h.vmStorageRoot ?? '');
    setErr(null);
  }, [syncKey]);

  const requestSave = (raw: string | null) => {
    const trimmed = raw?.trim() ?? '';
    const bad = windowsDirError(trimmed, true);
    if (bad) { setErr(bad); return; }
    setErr(null);
    const next = trimmed ? normalizeWindowsDir(trimmed) : null;
    const targetDir = next ?? DEFAULT_WINDOWS_DISK_DIR;
    const movable = h.vms.filter(v => {
      const cur = dirOfWindowsPath(v.diskPath);
      return !cur || !sameWindowsDir(cur, targetDir);
    }).length;
    if (movable > 0) {
      setPending({ path: next, display: targetDir, movable });
    } else {
      be.setHostStoragePath(h.id, next, false);
    }
  };

  const trimmedValue = value.trim();
  const normalizedValue = trimmedValue && !windowsDirError(trimmedValue, true) ? normalizeWindowsDir(trimmedValue) : trimmedValue;
  const normalizedSaved = saved ? normalizeWindowsDir(saved) : '';
  const dirty = normalizedValue.toLowerCase() !== normalizedSaved.toLowerCase();
  const apply = (moveAll: boolean) => {
    if (!pending) return;
    const p = pending.path;
    setPending(null);
    be.setHostStoragePath(h.id, p, moveAll);
  };

  return (
    <>
      <Sec title="VM storage">
        <div class="storage-form">
          <div class="storage-row">
            <Field value={value} onInput={v => { setValue(v); setErr(null); }} label="Default VM disk path" placeholder={DEFAULT_WINDOWS_DISK_DIR} mono wide onEnter={() => { if (dirty) requestSave(value); }} />
            {saved && <Btn sm onClick={() => requestSave(null)}>Reset</Btn>}
            <Btn sm kind="pri" disabled={!dirty} onClick={() => requestSave(value)}>Save</Btn>
          </div>
          <div class="d">Folder where new Hyper-V VM disks (.vhdx) are created. Leave empty for Hyper-V&apos;s default folder.</div>
          {err && <div class="badc">{err}</div>}
        </div>
      </Sec>
      {pending && (
        <Modal title="Move existing VMs?" onClose={() => setPending(null)} acts={
          <>
            <span />
            <Btn sm onClick={() => setPending(null)}>Cancel</Btn>
            <Btn sm onClick={() => apply(false)}>Only new VMs</Btn>
            <Btn sm kind="pri" icon="folder" onClick={() => apply(true)}>Move all VMs</Btn>
          </>
        }>
          <div>The default disk path will change to:</div>
          <code>{pending.display}</code>
          <div>
            This host has {pending.movable} existing {pending.movable === 1 ? 'VM' : 'VMs'} in another location.
            Do you want to automatically move {pending.movable === 1 ? 'it' : 'them'} to the new path too?
          </div>
        </Modal>
      )}
    </>
  );
}

function Certificate({ h }: { h: Host }) {
  const { be } = useApp();
  const [pem, setPem] = useState<string | null>(null);
  const c = h.cert;
  return (
    <Sec title="HTTPS certificate" right={pem === null && <Btn sm onClick={() => setPem('')}>{c ? 'Replace' : 'Set'}</Btn>}>
      {c ? (
        <>
          <KV k="Subject"><span class="m">{c.subject}</span></KV>
          <KV k="Valid until">{c.validUntil} <Tag>{c.daysLeft < 14 ? <span class="badc">{c.daysLeft} d</span> : `${c.daysLeft} d`}</Tag></KV>
          <KV k="Issuer">{c.issuer}</KV>
          <KV k="SHA-256"><span class="m">{c.sha256}</span></KV>
          <Row l="Remove"><Confirm label="Remove" onYes={() => be.removeCertificate(h.id)} /></Row>
        </>
      ) : <div class="line">None</div>}
      {pem !== null && (
        <>
          <textarea class="code edit" spellcheck={false} aria-label="PEM chain and key" placeholder="-----BEGIN CERTIFICATE-----" value={pem} onInput={e => setPem(e.currentTarget.value)} rows={6} />
          <div class="bf pad"><span /><Btn sm onClick={() => setPem(null)}>Cancel</Btn><Btn sm kind="pri" disabled={!pem.trim()} onClick={() => { be.installCertificate(h.id, pem); setPem(null); }}>Install</Btn></div>
        </>
      )}
    </Sec>
  );
}
