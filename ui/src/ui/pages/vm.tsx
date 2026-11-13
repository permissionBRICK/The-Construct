import { useState } from 'preact/hooks';
import type { Profile, SettingsPatch, Vm, VmSettings } from '../../backend/types';
import { activeJob, vmDot, vmWord, newer } from '../../model/vm';
import { groupApplies, hwChange } from '../../model/settings';
import { profileError } from '../../model/profile';
import { Icon } from '../icons';
import { Btn, Confirm, Copyable, Dot, Field, Head, KV, Page, Row, Sec, Seg, Switch, Table, Tabs, Tag, UpTag } from '../kit';
import { copy, useApp } from '../ctx';
import { ActionButtons, JobBlock, PowerBtn, ReinstallConfirm, VaultCards } from '../blocks';

/* Pages of one VM: overview, projects (list, one project, its file), connections, backups, settings. */

export function useBack() {
  const { surf, go } = useApp();
  return surf === 'phone' ? () => go({ page: 'home' }) : undefined;
}

export function Overview({ vm }: { vm: Vm }) {
  const { s, be } = useApp();
  const [wipe, setWipe] = useState(false);
  const job = activeJob(s, vm.id);
  const host = s.hosts.find(h => h.id === vm.hostId);
  const outdated = vm.agents.some(a => newer(a.latest, a.version));
  const vmUp = s.release && newer(s.release, vm.version) ? s.release : null;
  const use = [
    { k: 'CPU', big: String(vm.usage.cpuPct), u: '%', p: vm.usage.cpuPct },
    { k: 'Memory', big: String(vm.usage.memUsedGb), u: `of ${vm.memTotalGb} GB`, p: (vm.usage.memUsedGb / vm.memTotalGb) * 100 },
    { k: 'Disk', big: String(vm.usage.diskUsedGb), u: `of ${vm.diskTotalGb} GB`, p: (vm.usage.diskUsedGb / vm.diskTotalGb) * 100 },
  ];
  return (
    <Page>
      <VaultCards />
      <div class="vp">
        <div class="vp1">
          <div class="vpn">
            <div class="ey"><Dot s={vmDot(vm, job)} />{vmWord(vm, job)} · {host?.name} {host?.kind}</div>
            <h1>{vm.name}</h1>
            <span class="d m">{vm.address} · {vm.os.split(' ').slice(0, 2).join(' ')}</span>
          </div>
          <PowerBtn vm={vm} />
        </div>
        <div class="vp2">
          <Btn kind="pri" icon="open" onClick={() => be.open(vm.id, 't3')}>T3 Code</Btn>
          <Btn icon="code" onClick={() => be.open(vm.id, 'vscode')}>VS Code</Btn>
          <Btn icon="term" onClick={() => be.open(vm.id, 'ssh')}>SSH</Btn>
          <span class="grow" />
          <ActionButtons vm={vm} busy={job?.state === 'running'} onReinstall={() => setWipe(true)} />
        </div>
      </div>
      {job ? <JobBlock job={job} vm={vm} /> : wipe && <ReinstallConfirm vm={vm} onCancel={() => setWipe(false)} />}
      <div class="strip">
        {use.map(u => (
          <div><span class="sk">{u.k}</span><b>{u.big}<small>{u.u}</small></b><div class="bar"><i style={{ width: `${Math.min(100, u.p)}%` }} /></div></div>
        ))}
        <div><span class="sk">Construct</span><b>{vm.version}</b>{vmUp && <UpTag v={vmUp} />}</div>
      </div>
      <div class="g2">
        <Sec title="Coding agents" right={outdated && <Btn sm onClick={() => be.updateAgents(vm.id)}>Update all</Btn>}>
          <Table cols={[{ h: 'Agent' }, { h: 'Version', cls: 'e m' }]} rows={vm.agents.filter(a => a.installed).map(a => [a.name, <>{a.version}{newer(a.latest, a.version) && <> <Tag warn>→ {a.latest}</Tag></>}</>])} />
        </Sec>
        <Sec title="Child VMs">
          <Table cols={[{ h: 'Name' }, { h: 'System', cls: 'x' }, { h: 'Left', cls: 'm' }, { h: '', cls: 'e' }]}
            rows={vm.children.map(c => [<><Dot s={c.power === 'on' ? 'on' : 'off'} />{c.name}</>, c.os, c.left, <Btn sm onClick={() => be.openChildConsole(vm.id, c.name)}>Console</Btn>])} />
        </Sec>
      </div>
    </Page>
  );
}

export function Projects({ vm }: { vm: Vm }) {
  const { s, be, go } = useApp();
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const add = () => { be.addProject(name); setName(''); setAdding(false); };
  return (
    <Page>
      <Head title="Projects" back={useBack()} acts={<><Btn sm icon="repro" onClick={() => be.syncProjects()}>Sync</Btn><Btn sm icon="plus" onClick={() => setAdding(!adding)}>Add</Btn></>} />
      {adding && (
        <div class="line"><Field value={name} onInput={setName} label="Project name" placeholder="name" mono onEnter={add} /><Btn sm onClick={() => setAdding(false)}>Cancel</Btn><Btn sm kind="pri" onClick={add} disabled={!name.trim()}>Add</Btn></div>
      )}
      <div class="line"><span>Synced {s.sync.when}</span><code>{s.sync.remote}</code><code>{s.sync.rev}</code></div>
      <Sec>
        <Table
          cols={[{ h: 'Project' }, { h: 'Repos', cls: 'x m' }, { h: 'SDKs', cls: 'x m' }, { h: 'MCP', cls: 'x m' }, { h: `On ${vm.name}`, cls: 'e' }]}
          rows={s.projects.map(p => [
            p.name, p.repos.length, Object.entries(p.sdks).map(e => e.join(' ')).join(', ') || '–', p.mcp.length || '–',
            <span onClick={e => e.stopPropagation()}><Switch on={vm.projectsOn.includes(p.name)} onChange={on => be.setProjectOn(vm.id, p.name, on)} label={`${p.name} on ${vm.name}`} /></span>,
          ])}
          onRow={i => go({ page: 'project', arg: s.projects[i].name })}
        />
      </Sec>
    </Page>
  );
}

function ProjHead({ p, vm, cur }: { p: Profile; vm: Vm; cur: 'project' | 'file' }) {
  const { be, go } = useApp();
  return (
    <>
      <Head title={p.name} back={() => go({ page: 'projects' })} acts={<><span class="d">On {vm.name}</span><Switch on={vm.projectsOn.includes(p.name)} onChange={on => be.setProjectOn(vm.id, p.name, on)} label={`${p.name} on ${vm.name}`} /></>} />
      <Tabs items={[['project', 'Overview'], ['file', 'File']]} value={cur} onChange={t => go({ page: t, arg: p.name })} />
    </>
  );
}

export function Project({ vm, name }: { vm: Vm; name?: string }) {
  const { s } = useApp();
  const p = s.projects.find(x => x.name === name) ?? s.projects[0];
  if (!p) return null;
  const tags = (xs: string[]) => (xs.length ? <div class="tags">{xs.map(x => <Tag>{x}</Tag>)}</div> : <div class="line">None</div>);
  return (
    <Page>
      <ProjHead p={p} vm={vm} cur="project" />
      <Sec title="Repositories"><Table cols={[{ h: 'URL', cls: 'w m' }, { h: 'Directory', cls: 'x m' }]} rows={p.repos.map(r => [r.url, '/root/repos/' + r.directory])} /></Sec>
      <div class="two">
        <Sec title="SDKs">{tags(Object.entries(p.sdks).map(e => e.join(' ')))}</Sec>
        <Sec title="Packages">{tags(p.hostPackages)}</Sec>
      </div>
      <Sec title="Provision commands" right={<Tag>every reprovision</Tag>}>
        {p.provisionCommands.length ? <pre class="code">{p.provisionCommands.map(c => <span class="l">{c}</span>)}</pre> : <div class="line">None</div>}
      </Sec>
      <Sec title="MCP servers"><Table cols={[{ h: 'Name' }, { h: 'Type', cls: 'x' }, { h: 'Command', cls: 'w m' }]} rows={p.mcp.map(m => [m.name, m.type, `${m.command} ${m.args.join(' ')}`])} /></Sec>
      <Sec title="Forwards"><Table cols={[{ h: 'Port', cls: 'm' }, { h: 'Label' }]} rows={p.forwards.map(f => [f.port, f.label])} /></Sec>
    </Page>
  );
}

/** Profile JSON with keys and punctuation coloured. */
function JsonView({ text }: { text: string }) {
  return (
    <pre class="code">
      {text.split('\n').map(line => {
        const parts: (string | preact.JSX.Element)[] = [];
        const re = /("(?:[^"\\]|\\.)*")(\s*:)?|([{}[\],])/g;
        let last = 0, m: RegExpExecArray | null;
        while ((m = re.exec(line))) {
          if (m.index > last) parts.push(line.slice(last, m.index));
          if (m[1] && m[2]) parts.push(<span class="k">{m[1]}</span>, <span class="p">{m[2]}</span>);
          else if (m[1]) parts.push(m[1]);
          else parts.push(<span class="p">{m[3]}</span>);
          last = re.lastIndex;
        }
        parts.push(line.slice(last));
        return <span class="l">{parts}</span>;
      })}
    </pre>
  );
}

export function ProjectFile({ vm, name }: { vm: Vm; name?: string }) {
  const { s, be, toast } = useApp();
  const p = s.projects.find(x => x.name === name) ?? s.projects[0];
  const [edit, setEdit] = useState<string | null>(null);
  const [err, setErr] = useState('');
  if (!p) return null;
  const text = JSON.stringify(p, null, 2);
  const save = () => {
    try {
      const o = JSON.parse(edit!) as Profile;
      const bad = profileError(o);
      if (bad) throw new Error(bad);
      if (o.name !== p.name) throw new Error('The name cannot change here');
      be.saveProfile(o);
      setEdit(null); setErr('');
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Page>
      <ProjHead p={p} vm={vm} cur="file" />
      <div class="line">
        <code class="grow">/opt/construct/projects/{p.name}.json</code>
        {edit === null ? <><Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn><Btn sm icon="edit" onClick={() => setEdit(text)}>Edit</Btn></> : <><Btn sm onClick={() => { setEdit(null); setErr(''); }}>Cancel</Btn><Btn sm kind="pri" onClick={save}>Save</Btn></>}
      </div>
      {err && <div class="blk bad row"><Icon n="x" /><span>{err}</span></div>}
      {edit === null ? <JsonView text={text} /> : (
        <textarea class="code edit" spellcheck={false} aria-label="Profile JSON" value={edit} onInput={e => setEdit(e.currentTarget.value)} rows={Math.max(12, edit.split('\n').length + 1)} />
      )}
    </Page>
  );
}

export function Connections({ vm }: { vm: Vm }) {
  const { be } = useApp();
  return (
    <Page>
      <Head title="Connections" back={useBack()} />
      <Sec title="Forwards">
        <Table cols={[{ h: 'Port', cls: 'm' }, { h: 'Label' }, { h: 'To', cls: 'x' }, { h: 'Link', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={vm.forwards.map(f => [f.port, f.label, f.to, f.url, <><Btn sm icon="open" onClick={() => be.openForward(vm.id, f.port)}>Open</Btn> <Btn sm onClick={() => be.closeForward(vm.id, f.port)}>Close</Btn></>])} />
      </Sec>
      <Sec title="SSH"><Copyable text={`ssh root@${vm.address}`} /></Sec>
      <Sec title="Sharing">
        <Row l={<>VS Code web <span class="d m">:8000</span></>}><Switch on={vm.sharing.vscodeWeb} onChange={v => be.setSharing(vm.id, 'vscodeWeb', v)} label="VS Code web" /></Row>
        <Row l={<>SMB share <span class="d m">Z:</span></>}><Switch on={vm.sharing.smb} onChange={v => be.setSharing(vm.id, 'smb', v)} label="SMB share" /></Row>
        <Row l="Microphone"><Switch on={vm.sharing.mic} onChange={v => be.setSharing(vm.id, 'mic', v)} label="Microphone" /></Row>
      </Sec>
    </Page>
  );
}

export function Backups({ vm }: { vm: Vm }) {
  const { be } = useApp();
  return (
    <Page>
      <Head title="Backups" back={useBack()} acts={<Btn sm icon="plus" onClick={() => be.backupNow(vm.id)}>Back up now</Btn>} />
      <Sec title="Schedule">
        <Row l="Every"><Seg label="Every" opts={[6, 12, 24] as const} labels={{ 6: '6 h', 12: '12 h', 24: '24 h' }} value={vm.backupSchedule.everyH} onChange={v => be.setBackupSchedule(vm.id, { everyH: v })} /></Row>
        <Row l="Keep"><Seg label="Keep" opts={[5, 10, 20] as const} value={vm.backupSchedule.keep} onChange={v => be.setBackupSchedule(vm.id, { keep: v })} /></Row>
      </Sec>
      <Sec>
        <Table cols={[{ h: 'When' }, { h: 'Kind', cls: 'x' }, { h: 'Size', cls: 'x m' }, { h: 'Files', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={vm.backups.map(b => [<>{b.at}{b.protected && <> <Tag>protected</Tag></>}</>, b.kind, b.size, b.files.toLocaleString('en-US'), <>
            <button type="button" class={`lk ${b.pinned ? 'on' : ''}`} aria-pressed={b.pinned} title="Protect" aria-label="Protect" onClick={() => be.pinBackup(vm.id, b.id, !b.pinned)}><Icon n="lock" /></button>{' '}
            <Confirm label="Restore" danger={false} onYes={() => be.restoreBackup(vm.id, b.id)} />{' '}
            <Confirm label="Delete" icon="trash" verb={b.protected ? 'Delete protected' : 'Delete'} onYes={() => be.deleteBackup(vm.id, b.id)} />
          </>])} />
      </Sec>
    </Page>
  );
}

type Tab = 'res' | 'svc' | 'id' | 'agents';

export function Settings({ vm }: { vm: Vm }) {
  const { be, route, go } = useApp();
  const tab = (route.arg as Tab) || 'res';
  const [hw, setHw] = useState<{ memGb: number; cpus: number } | null>(null);
  const st = vm.settings;
  const save = (patch: SettingsPatch) => be.saveSettings(vm.id, patch);
  const wantHw = hw ?? { memGb: st.hw.memGb, cpus: st.hw.cpus };
  const draft = hw ? hwChange({ memGb: st.hw.memGb, cpus: st.hw.cpus }, hw) : '';
  const pendingRestart = hwChange(vm.appliedHw, { memGb: st.hw.memGb, cpus: st.hw.cpus });
  const svc = <K extends keyof VmSettings['services']>(k: K, v: VmSettings['services'][K]) => save({ services: { [k]: v } });
  const inp = (label: string, value: string, onSave: (v: string) => void) => <BlurField label={label} value={value} onSave={onSave} />;
  const tag = (g: keyof VmSettings) => <Tag>{groupApplies(g)}</Tag>;

  const body: Record<Tab, () => preact.JSX.Element> = {
    res: () => (
      <>
        <Sec title="Hardware" right={tag('hw')}>
          <Row l={<>Memory <span class="d">GB</span></>}><Seg label="Memory" opts={[8, 16, 32, 64] as const} value={wantHw.memGb as 8} onChange={v => setHw({ ...wantHw, memGb: v })} /></Row>
          <Row l="vCPUs"><Seg label="vCPUs" opts={[2, 4, 8, 16] as const} value={wantHw.cpus as 8} onChange={v => setHw({ ...wantHw, cpus: v })} /></Row>
          {draft && (
            <div class="blk warn row restart">
              <b class="grow">{draft}</b>
              <Btn sm onClick={() => setHw(null)}>Cancel</Btn>
              <Btn sm onClick={() => { be.saveSettings(vm.id, { hw: hw! }, 'later'); setHw(null); }}>Later</Btn>
              <Btn sm kind="pri" icon="power" onClick={() => { be.saveSettings(vm.id, { hw: hw! }, 'now'); setHw(null); }}>Restart now</Btn>
            </div>
          )}
          <Row l="Checkpoints"><Switch on={st.hw.checkpoints} onChange={v => save({ hw: { checkpoints: v } })} label="Checkpoints" /></Row>
          <Row l="Start with host"><Switch on={st.hw.startWithHost} onChange={v => save({ hw: { startWithHost: v } })} label="Start with host" /></Row>
        </Sec>
        <Sec title="Disk and system" right={tag('disk')}>
          <Row l={<>Disk <span class="d">GB</span></>}><Seg label="Disk" opts={[60, 120, 240] as const} value={st.disk.diskGb as 120} onChange={v => save({ disk: { diskGb: v } })} /></Row>
          <Row l="Ubuntu"><Seg label="Ubuntu" opts={['22.04', '24.04'] as const} value={st.disk.ubuntu as '24.04'} onChange={v => save({ disk: { ubuntu: v } })} /></Row>
        </Sec>
        <Sec title="When idle">
          <Row l={<>After <span class="d">min</span></>}><Seg label="Idle after" opts={[15, 30, 60] as const} value={st.idle.afterMin as 30} onChange={v => save({ idle: { afterMin: v } })} /></Row>
          <Row l="Then"><Seg label="Then" opts={['save', 'shutdown', 'never'] as const} labels={{ save: 'Save', shutdown: 'Shut down', never: 'Never' }} value={st.idle.then} onChange={v => save({ idle: { then: v } })} /></Row>
        </Sec>
      </>
    ),
    svc: () => (
      <Sec title="Services" right={tag('services')}>
        <Row l={<>T3 Code web <span class="d m">:5178</span></>}><Switch on={st.services.t3Web} onChange={v => svc('t3Web', v)} label="T3 Code web" /></Row>
        <Row l={<>VS Code web <span class="d m">:8000</span></>}><Switch on={st.services.vscodeWeb} onChange={v => svc('vscodeWeb', v)} label="VS Code web" /></Row>
        <Row l="VS Code tunnel"><Switch on={st.services.vscodeTunnel} onChange={v => svc('vscodeTunnel', v)} label="VS Code tunnel" /></Row>
        <Row l={<>SMB share <span class="d m">Z:</span></>}><Switch on={st.services.smb} onChange={v => svc('smb', v)} label="SMB share" /></Row>
        <Row l="Microphone"><Switch on={st.services.mic} onChange={v => svc('mic', v)} label="Microphone" /></Row>
        <Row l="Claude Code live streaming"><Switch on={st.services.claudeLive} onChange={v => svc('claudeLive', v)} label="Claude Code live streaming" /></Row>
        <Row l="OpenCode background watcher"><Switch on={st.services.opencodeWatcher} onChange={v => svc('opencodeWatcher', v)} label="OpenCode background watcher" /></Row>
        <Row l="T3 proxy URL">{inp('T3 proxy URL', st.services.t3ProxyUrl, v => svc('t3ProxyUrl', v))}</Row>
      </Sec>
    ),
    id: () => (
      <>
        <Sec title="Git" right={tag('identity')}>
          <Row l="Name">{inp('Git name', st.identity.gitName, v => save({ identity: { gitName: v } }))}</Row>
          <Row l="Email">{inp('Git email', st.identity.gitEmail, v => save({ identity: { gitEmail: v } }))}</Row>
          <Row l="Keep credentials on the VM"><Switch on={st.identity.keepCredentials} onChange={v => save({ identity: { keepCredentials: v } })} label="Keep credentials on the VM" /></Row>
        </Sec>
        <Sec title="Login">
          <Row l={<>Password <span class="d m">agent</span></>}><PasswordChange vm={vm} /></Row>
          <Row l={<>Chat history in backups <span class="d">days</span></>}><Seg label="Chat history" opts={['30', '90', 'all'] as const} labels={{ all: 'All' }} value={st.identity.historyDays} onChange={v => save({ identity: { historyDays: v } })} /></Row>
        </Sec>
      </>
    ),
    agents: () => (
      <Sec title="Coding agents" right={vm.agents.some(a => newer(a.latest, a.version)) && <Btn sm onClick={() => be.updateAgents(vm.id)}>Update all</Btn>}>
        <Table cols={[{ h: 'Agent' }, { h: 'Version', cls: 'm' }, { h: 'Latest', cls: 'x m' }, { h: 'Installed', cls: 'e' }]}
          rows={vm.agents.map(a => [a.name, a.version, newer(a.latest, a.version) ? <Tag warn>{a.latest}</Tag> : a.latest, <Switch on={a.installed} onChange={() => {}} label={`${a.name} installed`} disabled />])} />
      </Sec>
    ),
  };
  return (
    <Page>
      <Head title="Settings" back={useBack()} />
      {pendingRestart && !draft && (
        <div class="blk warn row"><b class="grow">{pendingRestart}</b><Btn sm kind="pri" icon="power" onClick={() => be.restart(vm.id)}>Restart now</Btn></div>
      )}
      <Tabs items={[['res', 'Resources'], ['svc', 'Services'], ['id', 'Identity'], ['agents', 'Agents']]} value={tab} onChange={t => go({ page: 'settings', arg: t })} />
      {body[tab]()}
    </Page>
  );
}

/** A text setting that saves when it loses focus or on Enter. */
function BlurField({ label, value, onSave }: { label: string; value: string; onSave: (v: string) => void }) {
  const [v, setV] = useState(value);
  const commit = () => { if (v !== value) onSave(v); };
  return <input class="in m" aria-label={label} value={v} onInput={e => setV(e.currentTarget.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') commit(); }} />;
}

function PasswordChange({ vm }: { vm: Vm }) {
  const { be } = useApp();
  const [open, setOpen] = useState(false);
  const [v, setV] = useState('');
  if (!open) return <Btn sm onClick={() => setOpen(true)}>Change</Btn>;
  return (
    <span class="confirm">
      <Field type="password" value={v} onInput={setV} label="New password" placeholder="New password" />
      <Btn sm onClick={() => { setOpen(false); setV(''); }}>Cancel</Btn>
      <Btn sm kind="pri" disabled={v.length < 8} onClick={() => { be.setVmPassword(vm.id, v); setOpen(false); setV(''); }}>Save</Btn>
    </span>
  );
}

export { KV };
