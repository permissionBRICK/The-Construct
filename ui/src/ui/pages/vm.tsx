import { useState } from 'preact/hooks';
import type { Agent, Profile, SettingsPatch, Vm, VmSettings } from '../../backend/types';
import { activeJob, vmDot, vmWord, newer, vmUpdateTarget } from '../../model/vm';
import { groupApplies, hwChange } from '../../model/settings';
import { profileError } from '../../model/profile';
import { Icon } from '../icons';
import { Btn, Confirm, Copyable, Dot, Field, Head, KV, NotYet, Page, Row, Sec, Seg, Switch, Table, Tabs, Tag, UpTag } from '../kit';
import { copy, na, useApp } from '../ctx';
import { ActionButtons, JobBlock, PowerBtn, ReinstallConfirm, VaultCards } from '../blocks';

/* Pages of one VM: overview, projects (list, one project, its file), connections, backups, settings. */

/** A newer version of a coding agent is out: the engine says so, else the versions are compared. */
const agentBehind = (a: Agent) => a.newer ?? newer(a.latest, a.version);

export function useBack() {
  const { surf, go } = useApp();
  return surf === 'phone' ? () => go({ page: 'home' }) : undefined;
}

export function Overview({ vm }: { vm: Vm }) {
  const { s, be } = useApp();
  const [wipe, setWipe] = useState(false);
  const job = activeJob(s, vm.id);
  const host = s.hosts.find(h => h.id === vm.hostId);
  const outdated = vm.agents.some(agentBehind);
  const vmUp = vmUpdateTarget(s, vm);
  const big = (n: number | null) => (n === null ? '–' : String(n));
  const use = [
    { k: 'CPU', big: big(vm.usage.cpuPct), u: '%', p: vm.usage.cpuPct ?? 0 },
    { k: 'Memory', big: big(vm.usage.memUsedGb), u: `of ${vm.memTotalGb} GB`, p: ((vm.usage.memUsedGb ?? 0) / vm.memTotalGb) * 100 },
    { k: 'Disk', big: big(vm.usage.diskUsedGb), u: `of ${vm.diskTotalGb} GB`, p: ((vm.usage.diskUsedGb ?? 0) / vm.diskTotalGb) * 100 },
  ];
  return (
    <Page>
      <VaultCards />
      <div class="vp">
        <div class="vp1">
          <div class="vpn">
            <div class="ey"><Dot s={vmDot(vm, job)} />{vmWord(vm, job)} · {host?.name} {host?.kind}</div>
            <h1>{vm.name}</h1>
            <span class="d m">{vm.address}{vm.os && ` · ${vm.os.split(' ').slice(0, 2).join(' ')}`}</span>
          </div>
          <PowerBtn vm={vm} />
        </div>
        <div class="vp2">
          <Btn kind="pri" icon="open" onClick={() => be.open(vm.id, 't3')} disabled={na(s, 'open')}>T3 Code</Btn>
          <Btn icon="code" onClick={() => be.open(vm.id, 'vscode')} disabled={na(s, 'open')}>VS Code</Btn>
          <Btn icon="term" onClick={() => be.open(vm.id, 'ssh')} disabled={na(s, 'open')}>SSH</Btn>
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
        <Sec title="Coding agents" right={outdated && !na(s, 'agent-update') && <Btn sm onClick={() => be.updateAgents(vm.id)}>Update all</Btn>}>
          {vm.problems?.agents && <div class="line badc">{vm.problems.agents}</div>}
          {na(s, 'agents') ? <NotYet /> : <Table cols={[{ h: 'Agent' }, { h: 'Version', cls: 'e m' }]} rows={vm.agents.filter(a => a.installed).map(a => [a.name, <>{a.version}{agentBehind(a) && <> <Tag warn>→ {a.latest}</Tag></>}</>])} />}
        </Sec>
        <Sec title="Child VMs">
          <Table cols={[{ h: 'Name' }, { h: 'System', cls: 'x' }, { h: 'Left', cls: 'm' }, { h: '', cls: 'e' }]}
            rows={vm.children.map(c => [<><Dot s={c.power === 'on' ? 'on' : 'off'} />{c.name}</>, c.os, c.left, <Btn sm onClick={() => be.openChildConsole(vm.id, c.name)} disabled={na(s, 'console')}>Console</Btn>])} />
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
  if (na(s, 'projects')) return <Page><Head title="Projects" back={useBack()} /><NotYet /></Page>;
  return (
    <Page>
      <Head title="Projects" back={useBack()} acts={<><Btn sm icon="repro" onClick={() => be.syncProjects()}>Sync</Btn><Btn sm icon="plus" onClick={() => setAdding(!adding)}>Add</Btn></>} />
      {adding && (
        <div class="line"><Field value={name} onInput={setName} label="Project name" placeholder="name" mono onEnter={add} /><Btn sm onClick={() => setAdding(false)}>Cancel</Btn><Btn sm kind="pri" onClick={add} disabled={!name.trim()}>Add</Btn></div>
      )}
      <div class="line"><span>Synced {s.sync.when}</span><code>{s.sync.remote}</code>{s.sync.rev && <code>{s.sync.rev}</code>}</div>
      {s.problems.projects && <div class="line badc">{s.problems.projects}</div>}
      <Sec>
        <Table
          cols={[{ h: 'Project' }, { h: 'Repos', cls: 'x m' }, { h: 'SDKs', cls: 'x m' }, { h: 'MCP', cls: 'x m' }, { h: na(s, 'project-on') ? '' : `On ${vm.name}`, cls: 'e' }]}
          rows={s.projects.map(p => [
            <>{p.name}{p.conflict && <> <Tag>an agent is resolving this</Tag></>}</>, p.repos.length, Object.entries(p.sdks).map(e => e.join(' ')).join(', ') || '–', p.mcp.length || '–',
            na(s, 'project-on') ? '' : <span onClick={e => e.stopPropagation()}><Switch on={vm.projectsOn.includes(p.name)} onChange={on => be.setProjectOn(vm.id, p.name, on)} label={`${p.name} on ${vm.name}`} /></span>,
          ])}
          onRow={i => go({ page: 'project', arg: s.projects[i].name })}
        />
      </Sec>
    </Page>
  );
}

function ProjHead({ p, vm, cur }: { p: Profile; vm: Vm; cur: 'project' | 'file' }) {
  const { s, be, go } = useApp();
  return (
    <>
      <Head title={p.name} back={() => go({ page: 'projects' })} acts={na(s, 'project-on') ? undefined : <><span class="d">On {vm.name}</span><Switch on={vm.projectsOn.includes(p.name)} onChange={on => be.setProjectOn(vm.id, p.name, on)} label={`${p.name} on ${vm.name}`} /></>} />
      {p.conflict && <div class="line"><Tag>an agent is resolving this</Tag></div>}
      <Tabs items={[['project', 'Overview'], ['file', 'File']]} value={cur} onChange={t => go({ page: t, arg: p.name })} />
    </>
  );
}

/** The profile as its file holds it: the store's document, else the view without the backend's own fields. */
function profileText(p: Profile): string {
  if (p.doc) return JSON.stringify(p.doc, null, 2);
  const { revision: _r, conflict: _c, doc: _d, ...rest } = p;
  return JSON.stringify(rest, null, 2);
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
  // The editor keeps the revision it was opened at; the save names it.
  const [edit, setEdit] = useState<{ text: string; revision: number | undefined } | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  if (!p) return null;
  const text = profileText(p);
  const save = () => {
    if (!edit || busy) return;
    let o: Profile;
    try {
      o = JSON.parse(edit.text) as Profile;
      const bad = profileError(o);
      if (bad) throw new Error(bad);
      if (o.name !== p.name) throw new Error('The name cannot change here');
    } catch (e) { setErr((e as Error).message); return; }
    setBusy(true);
    void be.saveProfile(p.name, o as unknown as Record<string, unknown>, edit.revision).then(r => {
      setBusy(false);
      if (r.ok) { setEdit(null); setErr(''); toast(r.text); }
      else if (r.changed) { setEdit(null); setErr(r.text); }
      else setErr(r.text);
    });
  };
  return (
    <Page>
      <ProjHead p={p} vm={vm} cur="file" />
      <div class="line">
        <code class="grow">/opt/construct/projects/{p.name}.json</code>
        {edit === null ? <><Btn sm icon="copy" onClick={() => { copy(text); toast('Copied'); }}>Copy</Btn><Btn sm icon="edit" onClick={() => { setErr(''); setEdit({ text, revision: p.revision }); }}>Edit</Btn></> : <><Btn sm onClick={() => { setEdit(null); setErr(''); }}>Cancel</Btn><Btn sm kind="pri" disabled={busy} onClick={save}>Save</Btn></>}
      </div>
      {err && <div class="blk bad row"><Icon n="x" /><span>{err}</span></div>}
      {edit === null ? <JsonView text={text} /> : (
        <textarea class="code edit" spellcheck={false} aria-label="Profile JSON" value={edit.text} onInput={e => setEdit({ ...edit, text: e.currentTarget.value })} rows={Math.max(12, edit.text.split('\n').length + 1)} />
      )}
    </Page>
  );
}

export function Connections({ vm }: { vm: Vm }) {
  const { s, be } = useApp();
  const can = s.device.can;
  const off = <span class="d">Not on this device</span>;
  const ssh = vm.link?.alias ? `ssh ${vm.link.alias}` : `ssh ${vm.sshPort && vm.sshPort !== 22 ? `-p ${vm.sshPort} ` : ''}root@${vm.address}`;
  const aliasOn = !!vm.link && vm.link.sshConfig !== 'removed' && vm.link.sshConfig !== 'failed';
  return (
    <Page>
      <Head title="Connections" back={useBack()} />
      {vm.problems?.connection && <div class="line badc">{vm.problems.connection}</div>}
      <Sec title="Forwards">
        {na(s, 'forwards') ? <NotYet /> : <Table cols={[{ h: 'Port', cls: 'm' }, { h: 'Label' }, { h: 'To', cls: 'x' }, { h: 'Link', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={vm.forwards.map(f => [f.port, f.label, f.to, f.url, <>{f.url && <Btn sm icon="open" onClick={() => be.openForward(vm.id, f.port)}>Open</Btn>}{f.closable && <> <Btn sm onClick={() => be.closeForward(vm.id, f.id)}>Close</Btn></>}</>])} />}
      </Sec>
      <Sec title="SSH" right={vm.link && (aliasOn
        ? <Btn sm onClick={() => be.setSshAlias(vm.id, false)}>Remove alias</Btn>
        : <Btn sm onClick={() => be.setSshAlias(vm.id, true)}>Write alias</Btn>)}>
        <Copyable text={ssh} />
        {vm.link?.sshConfig === 'failed' && <div class="line badc">The alias could not be written to the SSH config</div>}
      </Sec>
      <Sec title="Sharing">
        <Row l={<>VS Code web <span class="d m">:8000</span></>}>{na(s, 'sharing') ? <span class="d">Not available yet</span> : <Switch on={vm.sharing.vscodeWeb} onChange={v => be.setSharing(vm.id, 'vscodeWeb', v)} label="VS Code web" />}</Row>
        <Row l={<>SMB share {vm.link?.share && <span class="d m">{vm.link.share}</span>}</>}>{can && !can.share ? off : <Switch on={vm.sharing.smb} onChange={v => be.setSharing(vm.id, 'smb', v)} label="SMB share" />}</Row>
        <Row l="Microphone">{can && !can.mic ? off : <Switch on={vm.sharing.mic} onChange={v => be.setSharing(vm.id, 'mic', v)} label="Microphone" />}</Row>
      </Sec>
    </Page>
  );
}

const withValue = (opts: readonly number[], v: number) => (opts.includes(v) || !v ? opts : [...opts, v].sort((a, b) => a - b));

export function Backups({ vm }: { vm: Vm }) {
  const { s, be } = useApp();
  return (
    <Page>
      <Head title="Backups" back={useBack()} acts={<Btn sm icon="plus" onClick={() => be.backupNow(vm.id)}>Back up now</Btn>} />
      <Sec title="Schedule">
        <Row l="Every"><Seg label="Every" opts={withValue([6, 12, 24], vm.backupSchedule.everyH)} labels={{ [vm.backupSchedule.everyH]: `${vm.backupSchedule.everyH} h`, 6: '6 h', 12: '12 h', 24: '24 h' }} value={vm.backupSchedule.everyH} onChange={v => be.setBackupSchedule(vm.id, { everyH: v })} /></Row>
        <Row l="Keep"><Seg label="Keep" opts={withValue([5, 10, 20], vm.backupSchedule.keep)} value={vm.backupSchedule.keep} onChange={v => be.setBackupSchedule(vm.id, { keep: v })} /></Row>
      </Sec>
      {vm.problems?.backups && <div class="line badc">{vm.problems.backups}</div>}
      <Sec>
        <Table cols={[{ h: 'When' }, { h: 'Kind', cls: 'x' }, { h: 'Size', cls: 'x m' }, { h: 'Files', cls: 'x m' }, { h: '', cls: 'e' }]}
          rows={vm.backups.map(b => [<>{b.at}{b.protected && <> <Tag>protected</Tag></>}</>, b.kind, b.size, b.files === null ? '–' : b.files.toLocaleString('en-US'), <>
            <button type="button" class={`lk ${b.pinned ? 'on' : ''}`} aria-pressed={b.pinned} title="Protect" aria-label="Protect" onClick={() => be.pinBackup(vm.id, b.id, !b.pinned)}><Icon n="lock" /></button>{' '}
            <Confirm key={`restore-${b.id}`} label="Restore" danger={false} disabled={na(s, 'restore')} onYes={() => be.restoreBackup(vm.id, b.id)} />{' '}
            <Confirm key={`delete-${b.id}`} label="Delete" icon="trash" verb={b.protected ? 'Delete protected' : 'Delete'} onYes={() => be.deleteBackup(vm.id, b.id)} />
          </>])} />
      </Sec>
    </Page>
  );
}

type Tab = 'res' | 'svc' | 'id' | 'agents';

export function Settings({ vm }: { vm: Vm }) {
  const { s, be, route, go } = useApp();
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
          <Row l={<>Memory <span class="d">GB</span></>}><Seg label="Memory" opts={withValue([8, 16, 32, 64], wantHw.memGb)} value={wantHw.memGb} onChange={v => setHw({ ...wantHw, memGb: v })} /></Row>
          <Row l="vCPUs"><Seg label="vCPUs" opts={withValue([2, 4, 8, 16], wantHw.cpus)} value={wantHw.cpus} onChange={v => setHw({ ...wantHw, cpus: v })} /></Row>
          {draft && (
            <div class="blk warn row restart">
              <b class="grow">{draft}</b>
              <Btn sm onClick={() => setHw(null)}>Cancel</Btn>
              <Btn sm onClick={() => { be.saveSettings(vm.id, { hw: hw! }, 'later'); setHw(null); }}>Later</Btn>
              <Btn sm kind="pri" icon="power" onClick={() => { be.saveSettings(vm.id, { hw: hw! }, 'now'); setHw(null); }}>Restart now</Btn>
            </div>
          )}
          {na(s, 'vm-boot') ? null : <>
          <Row l="Checkpoints"><Switch on={st.hw.checkpoints} onChange={v => save({ hw: { checkpoints: v } })} label="Checkpoints" /></Row>
          <Row l="Start with host"><Switch on={st.hw.startWithHost} onChange={v => save({ hw: { startWithHost: v } })} label="Start with host" /></Row>
          </>}
        </Sec>
        <Sec title="Disk and system" right={tag('disk')}>
          {na(s, 'vm-disk') ? <NotYet /> : <><Row l={<>Disk <span class="d">GB</span></>}><Seg label="Disk" opts={[60, 120, 240] as const} value={st.disk.diskGb as 120} onChange={v => save({ disk: { diskGb: v } })} /></Row>
          <Row l="Ubuntu"><Seg label="Ubuntu" opts={['22.04', '24.04'] as const} value={st.disk.ubuntu as '24.04'} onChange={v => save({ disk: { ubuntu: v } })} /></Row></>}
        </Sec>
        <Sec title="When idle">
          <Row l={<>After <span class="d">min</span></>}><Seg label="Idle after" opts={withValue([15, 30, 60], st.idle.afterMin)} value={st.idle.afterMin} onChange={v => save({ idle: { afterMin: v } })} /></Row>
          <Row l="Then"><Seg label="Then" opts={['save', 'shutdown', 'never'] as const} labels={{ save: 'Save', shutdown: 'Shut down', never: 'Never' }} value={st.idle.then} onChange={v => save({ idle: { then: v } })} /></Row>
        </Sec>
      </>
    ),
    svc: () => na(s, 'vm-services') ? <Sec title="Services"><NotYet /></Sec> : (
      <Sec title="Services" right={tag('services')}>
        <Row l={<>T3 Code web <span class="d m">:5178</span></>}><Switch on={st.services.t3Web} onChange={v => svc('t3Web', v)} label="T3 Code web" /></Row>
        <Row l={<>VS Code web <span class="d m">:8000</span></>}><Switch on={st.services.vscodeWeb} onChange={v => svc('vscodeWeb', v)} label="VS Code web" /></Row>
        <Row l="VS Code tunnel"><Switch on={st.services.vscodeTunnel} onChange={v => svc('vscodeTunnel', v)} label="VS Code tunnel" /></Row>
        <Row l={<>SMB share <span class="d m">Z:</span></>}><Switch on={st.services.smb} onChange={v => svc('smb', v)} label="SMB share" /></Row>
        <Row l="Microphone"><Switch on={st.services.mic} onChange={v => svc('mic', v)} label="Microphone" /></Row>
        <Row l="Claude Code live streaming"><Switch on={st.services.claudeLive} onChange={v => svc('claudeLive', v)} label="Claude Code live streaming" /></Row>
        <Row l="OpenCode background watcher"><Switch on={st.services.opencodeWatcher} onChange={v => svc('opencodeWatcher', v)} label="OpenCode background watcher" /></Row>
        <Row l="T3 proxy URL">{na(s, 't3-proxy') ? <span class="d">Not available yet</span> : inp('T3 proxy URL', st.services.t3ProxyUrl, v => svc('t3ProxyUrl', v))}</Row>
      </Sec>
    ),
    id: () => na(s, 'vm-services') ? <Sec title="Git"><NotYet /></Sec> : (
      <>
        <Sec title="Git" right={tag('identity')}>
          <Row l="Name">{inp('Git name', st.identity.gitName, v => save({ identity: { gitName: v } }))}</Row>
          <Row l="Email">{inp('Git email', st.identity.gitEmail, v => save({ identity: { gitEmail: v } }))}</Row>
          <Row l="Keep credentials on the VM"><Switch on={st.identity.keepCredentials} onChange={v => save({ identity: { keepCredentials: v } })} label="Keep credentials on the VM" /></Row>
        </Sec>
        <Sec title="Login">
          <Row l={<>Password <span class="d m">agent</span></>}>{na(s, 'vm-password') ? <span class="d">Not available yet</span> : <PasswordChange vm={vm} />}</Row>
          <Row l={<>Chat history in backups <span class="d">days</span></>}>{na(s, 'vm-history') ? <span class="d">Not available yet</span> : <Seg label="Chat history" opts={['30', '90', 'all'] as const} labels={{ all: 'All' }} value={st.identity.historyDays} onChange={v => save({ identity: { historyDays: v } })} />}</Row>
        </Sec>
      </>
    ),
    agents: () => na(s, 'agents') ? <Sec title="Coding agents"><NotYet /></Sec> : (
      <Sec title="Coding agents" right={vm.agents.some(agentBehind) && !na(s, 'agent-update') && <Btn sm onClick={() => be.updateAgents(vm.id)}>Update all</Btn>}>
        {vm.problems?.agents && <div class="line badc">{vm.problems.agents}</div>}
        <Table cols={[{ h: 'Agent' }, { h: 'Version', cls: 'm' }, { h: 'Latest', cls: 'x m' }, { h: 'Installed', cls: 'e' }]}
          rows={vm.agents.map(a => [a.name, a.version, agentBehind(a) ? <Tag warn>{a.latest}</Tag> : a.latest, <Switch on={a.installed} onChange={() => {}} label={`${a.name} installed`} disabled />])} />
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
      {vm.problems?.settings && (tab === 'svc' || tab === 'id') && <div class="line badc">{vm.problems.settings}</div>}
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
