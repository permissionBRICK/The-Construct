import { describe, expect, it } from 'vitest';
import { ApiBackend } from '../../src/backend/api/backend';
import { SseParser } from '../../src/backend/api/events';
import { spanText } from '../../src/backend/api/map';
import { activeJob } from '../../src/model/vm';
import { updateParts } from '../../src/model/update';

/* ApiBackend against hand-written agent and engine responses. The fake fetch routes on method and path,
   records every call, and answers 404 for anything it does not know. */

const BASE = 'http://127.0.0.1:5000';
const TOKEN = 'agent-token-secret';
const E1 = 'aaaaaaaaaaaa', E2 = 'bbbbbbbbbbbb';

type Reply = { status?: number; json?: unknown };
type Handler = (body: unknown, url: URL) => Reply | undefined;
interface Call { method: string; path: string; query: string; body: unknown; auth: string | null }

function vmResponse(name: string, owner: string, extra: Record<string, unknown> = {}) {
  return {
    name, owner, cpu: 4, ramGb: 8, diskGb: 60, created: '2026-11-01T10:00:00Z', state: 'running', sshForwardPort: 2201, publicHost: 'host.test', deleting: false,
    idlePolicy: { timeoutMinutes: 30, action: 'save', maxTimeoutMinutes: 0, clamped: false }, forwards: [], kind: 'primary', parent: null, sharing: 'private', shared: false,
    resourceUsage: { observedAt: null, stale: false, state: null, cpuUsagePercent: 12, memoryAssignedBytes: 4 * 1024 ** 3, memoryDemandBytes: null, diskFileBytes: 20 * 1024 ** 3, uptimeSeconds: 1 },
    pendingCpu: null, pendingRamGb: null, ...extra,
  };
}

const job = (id: string, vm: string, state: string, kind = 'provision', progress: unknown[] = []) => ({
  id, kind, vmName: vm, state, created: '2026-11-13T10:00:00Z', finished: null, progress, result: null, error: state === 'failed' ? 'provision-steps-failed' : null, phase: null,
});

function world(opts: { registered?: boolean; unconfirmed?: boolean; engines?: { id: string; name: string; url: string }[] } = {}) {
  const calls: Call[] = [];
  const routes = new Map<string, Handler>();
  const on = (method: string, path: string, h: Handler | Reply) => routes.set(`${method} ${path}`, typeof h === 'function' ? h : () => h);
  const engines = (opts.engines ?? [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }]).map(e => ({ ...e, pin: 'AB', engineId: 'x', enrolled: true, signedIn: false, store: e.id === E1 }));
  on('GET', '/api/health', { json: { status: 'ok', version: '3.1.0', pid: 1, startedAt: '2026-11-13T00:00:00Z' } });
  on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: opts.registered ?? true, storeEngine: E1, protection: 'file', protectionWeak: true, recoveryUnconfirmed: opts.unconfirmed ?? false }, engines, pairing: null } });
  on('GET', '/api/update', { json: { version: '3.1.0', channel: 'stable', available: null, state: 'idle', error: null, installed: true, checkedAt: null } });
  on('GET', '/api/device/settings', { json: { channel: 'stable', autoUpdate: false, startWithOs: false } });
  on('GET', '/api/device/features', { json: { platform: 'linux', notifications: true, microphone: false, share: false, startWithOs: true, shell: false } });
  on('GET', '/api/device/ssh-key', { json: { publicKey: 'ssh-ed25519 AAAA device', path: '/home/u/.ssh/construct_ed25519' } });
  on('GET', '/api/update/everything', { json: { running: false, parts: [] } });
  on('GET', '/api/connections', { json: [] });
  on('GET', '/api/devices', { json: [{ id: 'd1', name: 'laptop', me: true, revoked: false, added: '2026-11-01T00:00:00Z', acknowledgedBy: [] }, { id: 'd2', name: 'phone', me: false, revoked: false, added: '2026-11-02T00:00:00Z', acknowledgedBy: [] }] });
  for (const e of engines) {
    const p = `/api/engines/${e.id}/api/v1`;
    on('GET', `${p}/whoami`, { json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: e.id === E1 ? 'admin' : 'user', maxVms: 5, allowHostForwards: true } });
    on('GET', `${p}/health`, { json: { status: 'ok', schemaVersion: 1, schemaMinReadableBy: 0, apiFeatures: [], maintenance: null, commit: 'c'.repeat(40), packageVersion: '3.1.0' } });
    on('GET', `${p}/me/updates`, { json: { autoReprovision: false } });
    on('GET', `${p}/jobs`, { json: [] });
    on('GET', `${p}/host/capabilities`, { json: { backend: 'x', kind: e.id === E1 ? 'hyperv' : 'proxmox' } });
    on('GET', `${p}/vms`, { json: e.id === E1 ? [vmResponse('atlas', 'alex'), vmResponse('kai-box', 'kai')] : [vmResponse('harbor', 'alex')] });
    for (const vm of ['atlas', 'harbor']) {
      on('GET', `${p}/vms/${vm}/provision-status`, { json: { settingsChanged: vm === 'atlas', newerPayload: false, hardwareChanged: false, desiredHash: 'h', availableCommit: 'c'.repeat(40), applied: { commit: 'd'.repeat(40), payloadHash: null, desiredHash: 'g', channel: 'main', installedAt: '2026-11-01T10:00:00Z', reprovisionedAt: '2026-11-10T10:00:00Z', readAt: '2026-11-10T10:00:00Z', jobId: 'j0' }, pendingCpu: null, pendingRamGb: null } });
      on('GET', `${p}/vms/${vm}/update`, { json: { newerPayload: vm === 'harbor', availableCommit: 'c'.repeat(40), appliedCommit: 'd'.repeat(40), hostUpdate: null } });
      on('GET', `${p}/vms/${vm}/backups`, { json: { backups: [
        { id: 'b'.repeat(32), createdAt: '2026-11-12T03:00:00Z', trigger: 'scheduled', sizeBytes: 415e6, sha256: null, pinnedUntil: null, kind: 'host', pinned: false, protected: true, fileCount: 48230 },
        { id: 'c'.repeat(32), createdAt: '2026-11-13T03:00:00Z', trigger: 'manual', sizeBytes: 400e6, sha256: null, pinnedUntil: null, kind: 'host', pinned: false, protected: false, fileCount: null },
      ] } });
      on('GET', `${p}/vms/${vm}/desired`, { json: { desired: { projects: ['webshop'], settings: { vsCodeTunnel: 'true', gitUserName: 'Alex', aiTools: 'claude-code' } }, hash: 'h', updatedAt: null, updatedBy: null } });
      on('GET', `${p}/vms/${vm}/agents`, { json: { reportedAt: null, agents: [{ tool: 'claude', name: 'Claude Code', version: '2.1.0', latest: '2.2.0', updateAvailable: true }, { tool: 'codex', name: 'Codex', version: '0.9.0', latest: '0.9.0', updateAvailable: false }] } });
      on('GET', `${p}/vms/${vm}/backups/settings`, { json: { intervalHours: null, keep: null, effective: { enabled: true, intervalHours: 24, keep: 4 }, host: { enabled: true, intervalHours: 24, keep: 4, maxKeep: 10 } } });
    }
    if (e.id === E1) {
      on('GET', `${p}/users`, { json: [] });
      on('GET', `${p}/host/status`, { json: { health: { hypervisor: 'ok', database: 'ok', media: 'ok', inventory: 'ok' }, capacity: { ram: { totalBytes: 64 * 1024 ** 3, committedBytes: 16 * 1024 ** 3 }, cpu: { logical: 16, active: 8 }, volumes: [] }, capacityMode: 'observe' } });
      on('GET', `${p}/host/updates/status`, { json: { supportsAutoApply: true, installed: { commit: 'c'.repeat(40), packageVersion: '3.1.0', installedAt: null, source: 'x' }, current: null, history: [], recoveryRecord: null, latestKnown: null, version: '3.1.0', channel: 'main', auto: false, available: { version: '3.2.0', commit: 'e'.repeat(40) }, availableError: null, databaseCopy: null } });
      for (const r of ['media', 'audit']) on('GET', `${p}/${r}`, { json: [] });
      on('GET', `${p}/host/usage`, { json: { window: 'month', generatedAt: '', totals: { tokens: 0, costUsd: 0 }, byUser: [], byVm: [] } });
      on('GET', `${p}/host/config`, { json: { backups: { enabled: true, keep: 4, source: 'default', updatedAt: null } } });
      on('GET', `${p}/host/https`, { json: { certificate: null, directory: '', config: {}, names: { publicHost: '', example: '' }, listeners: [], warnings: [] } });
      on('GET', `${p}/host/windows`, { json: { keys: [], guests: [], machines: [] } });
      on('GET', `${p}/host/iso-catalog`, { json: { mode: 'prebuilt', source: { path: null, url: null, sha256Configured: false, present: true, sizeBytes: null }, current: null, entries: [{ fileName: 'construct-3.1.0.iso', sizeBytes: 3.2e9, isCurrent: true, builtAt: null, sidecarReadable: true }] } });
    }
  }
  const fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const req = input instanceof Request ? input : new Request(String(input), init);
    const url = new URL(req.url);
    const text = req.body ? await req.text() : '';
    const body = text ? JSON.parse(text) : undefined;
    calls.push({ method: req.method, path: url.pathname, query: url.search, body, auth: req.headers.get('authorization') });
    const r = routes.get(`${req.method} ${url.pathname}`)?.(body, url) ?? { status: 404, json: { title: 'not found', status: 404 } };
    const status = r.status ?? 200;
    return new Response(r.json === undefined || status === 204 ? null : JSON.stringify(r.json), { status, headers: { 'content-type': 'application/json' } });
  };
  const be = new ApiBackend({ base: BASE, token: TOKEN, fetch: fetch as typeof globalThis.fetch, pollMs: 0, events: false });
  const settle = () => new Promise(r => setTimeout(r, 20));
  return { be, calls, on, settle };
}

const posts = (calls: Call[]) => calls.filter(c => c.method !== 'GET');

describe('ApiBackend', () => {
  it('assembles the state from the agent and each engine', async () => {
    const { be } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    await be.start();
    const s = be.snapshot();
    expect(s.version).toBe('3.1.0');
    expect(s.device.name).toBe('laptop');
    expect(s.firstRun).toBeNull();
    // Only the user's own primaries; another user's VM on a host they administer is host administration.
    expect(s.vms.map(v => v.name).sort()).toEqual(['atlas', 'harbor']);
    const atlas = s.vms.find(v => v.name === 'atlas')!;
    expect(atlas.hostId).toBe(E1);
    expect(atlas.changeWaits).toBe('Settings');
    expect(atlas.updateTo).toBeNull();
    expect(atlas.version).toBe('ddddddd');
    expect(atlas.usage).toEqual({ cpuPct: 12, memUsedGb: 4, diskUsedGb: 20 });
    expect(atlas.backups.map(b => [b.kind, b.protected, b.files])).toEqual([['Scheduled', true, 48230], ['Requested', false, null]]);
    expect(atlas.backupSchedule).toEqual({ everyH: 24, keep: 4 });
    const harbor = s.vms.find(v => v.name === 'harbor')!;
    expect(harbor.updateTo).toBe('3.1.0');
    const orchard = s.hosts.find(h => h.id === E1)!;
    expect(orchard.admin).toBe(true);
    expect(orchard.vms.map(v => v.name)).toEqual(['atlas', 'kai-box']);
    expect(orchard.updateTo).toBe('3.2.0');
    expect(orchard.capacity.ram).toEqual([16, 64]);
    expect(orchard.config[0]).toMatchObject({ name: 'backups', updated: 'default' });
    expect(JSON.parse(orchard.config[0].json)).toEqual({ enabled: true, keep: 4 });
    const lab = s.hosts.find(h => h.id === E2)!;
    expect(lab.admin).toBe(false);
    expect(lab.vms).toEqual([]);
    expect(updateParts(s).filter(p => p.to).map(p => p.name).sort()).toEqual(['harbor', 'orchard']);
    expect(s.devices.map(d => [d.name, d.me])).toEqual([['laptop', true], ['phone', false]]);
    expect(s.na).not.toContain('vault');
    expect(s.na).not.toContain('reinstall');
    expect(s.na).not.toContain('restore');
    expect(s.na).not.toContain('repair-chat');
    // Windows sign-in only on a device the agent reports as Windows; this one is Linux.
    expect(s.na).toContain('windows-sign-in');
    // Settings and projects from the desired state; agents from the guest's report.
    expect(atlas.settings.services).toMatchObject({ vscodeTunnel: true, vscodeWeb: true, mic: false });
    expect(atlas.settings.identity).toMatchObject({ gitName: 'Alex', gitEmail: '', keepCredentials: false });
    expect(atlas.projectsOn).toEqual(['webshop']);
    expect(atlas.agents.map(a => [a.name, a.newer])).toEqual([['Claude Code', true], ['Codex', false]]);
    // Host facts: the kind from the capabilities, the ISO catalog, no LAN switch on a host that is not solo.
    expect(orchard.kind).toBe('Hyper-V');
    expect(lab.kind).toBe('Proxmox');
    expect(orchard.isos).toEqual([{ name: 'construct-3.1.0.iso', current: true, size: '3.2 GB' }]);
    expect(orchard.lan).toBeUndefined();
    expect(s.device.sshKey).toBe('ssh-ed25519 AAAA device');
    expect(s.device.can).toEqual({ mic: false, share: false, startWithOs: true });
  });

  it('keeps an engine that fails to its own host and shows the others', async () => {
    const { be, on } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    on('GET', `/api/engines/${E2}/api/v1/whoami`, { status: 502, json: { title: 'engine-unreachable', status: 502, code: 'engine-unreachable' } });
    await be.start();
    const s = be.snapshot();
    expect(s.vms.map(v => v.name)).toEqual(['atlas']);
    expect(s.hosts.find(h => h.id === E2)!.problem).toBe('engine-unreachable');
    expect(s.hosts.find(h => h.id === E1)!.problem).toBeNull();
  });

  it('keeps a failing route to its part of the VM', async () => {
    const { be, on } = world();
    on('GET', `/api/engines/${E1}/api/v1/vms/atlas/backups`, { status: 500, json: { title: 'backup store down', status: 500 } });
    await be.start();
    const atlas = be.snapshot().vms.find(v => v.name === 'atlas')!;
    expect(atlas.problems?.backups).toBe('backup store down');
    expect(atlas.changeWaits).toBe('Settings');
  });

  it('sends the token only as a header, never in a URL', async () => {
    const { be, calls } = world();
    await be.start();
    be.reprovision('atlas');
    await new Promise(r => setTimeout(r, 20));
    expect(calls.length).toBeGreaterThan(10);
    for (const c of calls) {
      expect(c.auth).toBe(`Bearer ${TOKEN}`);
      expect(c.path + c.query).not.toContain(TOKEN);
    }
  });

  it('starts a reprovision as a provision job on the VM\'s engine, sending defaults when the engine has none', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    let n = 0;
    on('POST', `/api/engines/${E2}/vms/harbor/jobs`, body => (++n === 1 ? { status: 409, json: { title: 'desired-state-missing', status: 409, code: 'desired-state-missing' } } : (expect(body).toMatchObject({ kind: 'provision', desired: { projects: [], settings: {} } }), { status: 202, json: { jobId: 'j9', kind: 'provision', replayed: false } })));
    on('GET', `/api/engines/${E2}/api/v1/jobs/j9`, { json: job('j9', 'harbor', 'running') });
    await be.start();
    be.reprovision('harbor');
    await settle();
    const p = posts(calls);
    expect(p.map(c => c.path)).toEqual([`/api/engines/${E2}/vms/harbor/jobs`, `/api/engines/${E2}/vms/harbor/jobs`]);
    expect(p[0].body).toMatchObject({ kind: 'provision' });
    expect((p[0].body as { desired?: unknown }).desired).toBeUndefined();
    expect((p[0].body as { requestId: string }).requestId).not.toBe((p[1].body as { requestId: string }).requestId);
    const j = activeJob(be.snapshot(), 'harbor')!;
    expect(j).toMatchObject({ kind: 'reprovision', state: 'running' });
  });

  it('follows job progress and the end from the agent\'s events, reading the job again each time', async () => {
    const { be, on, settle } = world();
    on('POST', `/api/engines/${E1}/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'queued') });
    await be.start();
    be.reprovision('atlas');
    await settle();
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'running', 'provision', [{ at: '', text: 'copying', step: 'copy', state: 'running', percent: 30 }]) });
    be.onEvent('job.progress', { seq: 1, type: 'job.progress', engine: E1, data: { id: 'j1', vm: 'atlas' } });
    await settle();
    const j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ state: 'running', step: 'Copying', stepIndex: 1, pct: 30 });
    expect(j.log).toEqual(['copying']);
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'succeeded') });
    on('GET', `/api/engines/${E1}/api/v1/jobs`, { json: [job('j1', 'atlas', 'succeeded')] });
    be.onEvent('job.ended', { seq: 2, type: 'job.ended', engine: E1, data: { id: 'j1', vm: 'atlas' } });
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toBeNull();
    expect(be.snapshot().jobs.at(-1)).toMatchObject({ state: 'done', pct: 100 });
    expect(be.snapshot().notice?.text).toBe('Reprovisioned atlas');
    await settle();
  });

  it('deletes the protected backup only with the engine\'s confirm flag', async () => {
    const { be, calls, on, settle } = world();
    on('DELETE', `/api/engines/${E1}/api/v1/vms/atlas/backups/${'b'.repeat(32)}`, { status: 204 });
    on('DELETE', `/api/engines/${E1}/api/v1/vms/atlas/backups/${'c'.repeat(32)}`, { status: 204 });
    await be.start();
    be.deleteBackup('atlas', 'b'.repeat(32));
    be.deleteBackup('atlas', 'c'.repeat(32));
    await settle();
    const d = calls.filter(c => c.method === 'DELETE');
    expect(d.map(c => c.query)).toEqual(['?confirm=true', '']);
  });

  it('pins, schedules and requests backups on the VM\'s engine', async () => {
    const { be, calls, on, settle } = world();
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/backups/${'c'.repeat(32)}/pin`, { status: 204 });
    on('PUT', `/api/engines/${E1}/api/v1/vms/atlas/backups/settings`, { json: {} });
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/backups/request`, { status: 202, json: { requestPending: true, trigger: 'manual' } });
    await be.start();
    be.pinBackup('atlas', 'c'.repeat(32), true);
    be.setBackupSchedule('atlas', { keep: 10 });
    be.backupNow('atlas');
    await settle();
    expect(posts(calls).map(c => [c.method, c.path.replace(`/api/engines/${E1}/api/v1/vms/atlas/backups`, ''), c.body])).toEqual([
      ['POST', `/${'c'.repeat(32)}/pin`, undefined], ['PUT', '/settings', { intervalHours: 24, keep: 10 }], ['POST', '/request', { trigger: 'manual' }],
    ]);
  });

  it('reports what the API does not offer and sends nothing', async () => {
    const { be, calls, settle } = world();
    await be.start();
    const before = calls.length;
    be.saveSettings('atlas', { services: { t3ProxyUrl: 'https://x' } });
    expect(be.snapshot().notice?.text).toBe('Not available yet');
    be.saveSettings('atlas', { identity: { historyDays: '90' } });
    be.saveSettings('atlas', { disk: { diskGb: 120 } });
    be.setSharing('atlas', 'vscodeWeb', true);
    be.updateAgents('atlas');
    be.takeBack('no-such-lease');
    be.closeForward('atlas', 'no-such-forward');
    be.retryHostJob(E1, 'no-such-job');
    be.setLanAccess(E1, true);
    await settle();
    expect(calls.length).toBe(before);
  });

  it('saves a VM\'s settings into its own desired state, keeping the fields the page does not show', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    const stored: unknown[] = [];
    on('PUT', `/api/engines/${E1}/api/v1/vms/atlas/desired`, b => { stored.push(b); return { json: { desired: b, hash: 'h2', updatedAt: null, updatedBy: null } }; });
    on('PUT', `/api/engines/${E2}/api/v1/vms/harbor/desired`, b => { stored.push(b); return { json: { desired: b, hash: 'h2', updatedAt: null, updatedBy: null } }; });
    await be.start();
    // The page shows no waiting change for harbor until its engine reports one.
    expect(be.snapshot().vms.find(v => v.name === 'harbor')!.changeWaits).toBeNull();
    on('GET', `/api/engines/${E2}/api/v1/vms/harbor/provision-status`, { json: { settingsChanged: true, newerPayload: false, hardwareChanged: false, desiredHash: 'h2', availableCommit: null, applied: null, pendingCpu: null, pendingRamGb: null } });
    be.saveSettings('harbor', { services: { smb: false }, identity: { gitEmail: ' alex@example.net ' } });
    await settle();
    be.setProjectOn('atlas', 'toolkit', true);
    await settle();
    be.setProjectOn('atlas', 'webshop', false);
    await settle();
    const puts = posts(calls).map(c => [c.method, c.path.replace(/\/api\/engines\/(\w+)\/api\/v1/, '$1')]);
    expect(puts).toEqual([['PUT', `${E2}/vms/harbor/desired`], ['PUT', `${E1}/vms/atlas/desired`], ['PUT', `${E1}/vms/atlas/desired`]]);
    expect(stored[0]).toEqual({ projects: ['webshop'], settings: { vsCodeTunnel: 'true', gitUserName: 'Alex', aiTools: 'claude-code', smbShare: 'false', gitEmail: 'alex@example.net' } });
    expect((stored[1] as { projects: string[] }).projects).toEqual(['webshop', 'toolkit']);
    expect((stored[1] as { settings: unknown }).settings).toEqual({ vsCodeTunnel: 'true', gitUserName: 'Alex', aiTools: 'claude-code' });
    expect((stored[2] as { projects: string[] }).projects).toEqual([]);
    // Nothing ran on the VM; the dot comes from the engine's provision-status.
    expect(calls.some(c => c.method === 'POST' && c.path.includes('/jobs'))).toBe(false);
    expect(be.snapshot().vms.find(v => v.name === 'harbor')!.changeWaits).toBe('Settings');
  });

  it('stores two quick changes of one VM one after the other, so neither is lost', async () => {
    const { be, on, settle } = world();
    let doc: Record<string, unknown> = { projects: ['webshop'], settings: { aiTools: 'claude-code' } };
    on('GET', `/api/engines/${E1}/api/v1/vms/atlas/desired`, () => ({ json: { desired: doc, hash: 'h', updatedAt: null, updatedBy: null } }));
    on('PUT', `/api/engines/${E1}/api/v1/vms/atlas/desired`, b => { doc = b as Record<string, unknown>; return { json: { desired: b, hash: 'h2', updatedAt: null, updatedBy: null } }; });
    await be.start();
    be.setProjectOn('atlas', 'toolkit', true);
    be.saveSettings('atlas', { services: { smb: false } });
    await settle();
    await settle();
    expect(doc).toEqual({ projects: ['webshop', 'toolkit'], settings: { aiTools: 'claude-code', smbShare: 'false' } });
  });

  it('shows T3 Code web on only where T3 Code is on, and switching it on turns both on', async () => {
    const { be, on, calls, settle } = world();
    on('GET', `/api/engines/${E1}/api/v1/vms/atlas/desired`, { json: { desired: { projects: [], settings: { t3Code: 'false', t3CodeHttps: 'true' } }, hash: 'h', updatedAt: null, updatedBy: null } });
    on('PUT', `/api/engines/${E1}/api/v1/vms/atlas/desired`, b => ({ json: { desired: b, hash: 'h2', updatedAt: null, updatedBy: null } }));
    await be.start();
    expect(be.snapshot().vms.find(v => v.name === 'atlas')!.settings.services.t3Web).toBe(false);
    be.saveSettings('atlas', { services: { t3Web: true } });
    await settle();
    expect(calls.find(c => c.method === 'PUT')!.body).toEqual({ projects: [], settings: { t3Code: 'true', t3CodeHttps: 'true' } });
  });

  it('labels an approval span exactly', () => {
    expect([7200, 900, 3700, 86400, 90].map(spanText)).toEqual(['2 h', '15 min', '1 h 1 min 40 s', '24 h', '1 min 30 s']);
  });

  it('shows a failing desired-state read on the Settings page only', async () => {
    const { be, on } = world();
    on('GET', `/api/engines/${E1}/api/v1/vms/atlas/desired`, { status: 500, json: { title: 'store down', status: 500 } });
    await be.start();
    const atlas = be.snapshot().vms.find(v => v.name === 'atlas')!;
    expect(atlas.problems).toEqual({ settings: 'store down' });
    expect(atlas.backups.length).toBe(2);
  });

  it('saves hardware and restarts only when asked to', async () => {
    const { be, calls, on, settle } = world();
    on('PUT', `/api/engines/${E1}/api/v1/vms/atlas/memory`, { json: {} });
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/lifecycle`, { status: 202, json: { jobId: 'r1' } });
    await be.start();
    be.saveSettings('atlas', { hw: { memGb: 16 } }, 'later');
    await settle();
    be.saveSettings('atlas', { hw: { memGb: 16 } }, 'now');
    await settle();
    expect(posts(calls).map(c => [c.method, c.path.split('/').at(-1), c.body])).toEqual([
      ['PUT', 'memory', { ramGb: 16 }], ['PUT', 'memory', { ramGb: 16 }], ['POST', 'lifecycle', { action: 'restart' }],
    ]);
  });

  it('updates one VM on its own engine, and everything per engine', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    on('POST', `/api/engines/${E2}/api/v1/vms/harbor/update`, () => {
      on('GET', `/api/engines/${E2}/api/v1/jobs`, { json: [job('u1', 'harbor', 'running')] });
      return { status: 202, json: { jobId: 'u1', hostUpdateId: null, hostJobId: null } };
    });
    on('GET', `/api/engines/${E2}/api/v1/jobs/u1`, { json: job('u1', 'harbor', 'running') });
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/update`, { status: 202, json: { jobId: null, hostUpdateId: 'x', hostJobId: 'h1' } });
    on('POST', `/api/engines/${E2}/api/v1/me/vms/reprovision`, { status: 202, json: { vms: ['harbor'] } });
    await be.start();
    be.updateVm('harbor');
    await settle();
    expect(activeJob(be.snapshot(), 'harbor')).toMatchObject({ kind: 'update', from: 'ddddddd', to: '3.1.0' });
    on('POST', '/api/update/everything', { status: 202, json: { running: true, parts: [
      { part: 'engine', engine: E1, name: 'orchard', state: 'running', detail: null }, { part: 'vms', engine: E1, name: 'orchard', state: 'pending', detail: null },
      { part: 'device', engine: null, name: 'This device', state: 'pending', detail: null },
    ] } });
    be.updateEverything();
    await settle();
    // "Only this VM" goes to the VM's engine; Update goes to the agent, which runs each part's own updater.
    expect(posts(calls).map(c => c.path)).toEqual([`/api/engines/${E2}/api/v1/vms/harbor/update`, '/api/update/everything']);
    expect(be.snapshot().everything).toEqual({ running: true, parts: [
      { part: 'engine', name: 'orchard', state: 'running', detail: null }, { part: 'vms', name: 'Your VMs on orchard', state: 'pending', detail: null },
      { part: 'device', name: 'This device', state: 'pending', detail: null },
    ] });
    // The agent announces each part's change; the page reads the run again.
    on('GET', '/api/update/everything', { json: { running: false, parts: [{ part: 'engine', engine: E1, name: 'orchard', state: 'failed', detail: 'The host update failed.' }] } });
    be.onEvent('update', { seq: 9, type: 'update', engine: null, data: null });
    await settle();
    expect(be.snapshot().everything!.parts[0]).toEqual({ part: 'engine', name: 'orchard', state: 'failed', detail: 'The host update failed.' });
  });

  it('approves a pairing by the code the new device shows', async () => {
    const { be, calls, on, settle } = world();
    on('GET', '/api/pairing/requests/p-123', { json: { pairingId: 'p-123', name: 'tablet', words: ['one', 'two'], publicKey: 'PK', expiresAt: '' } });
    on('POST', '/api/pairing/requests/p-123/approve', { status: 204 });
    await be.start();
    be.startPairing();
    expect(be.snapshot().pairing).toEqual({ state: 'code' });
    be.enterPairingCode(' p-123 ');
    await settle();
    expect(be.snapshot().pairing).toEqual({ state: 'confirm', words: ['one', 'two'], deviceName: 'tablet' });
    be.confirmPairing(true);
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([['/api/pairing/requests/p-123/approve', { publicKey: 'PK' }]]);
    expect(be.snapshot().pairing).toEqual({ state: 'done', deviceName: 'tablet' });
  });

  it('revokes a device and lists which engines confirmed', async () => {
    const { be, on, settle } = world();
    on('DELETE', '/api/devices/d2', { json: { deviceId: 'd2', engines: [{ id: E1, name: 'orchard', confirmed: true }] } });
    await be.start();
    be.revokeDevice('d1');
    be.revokeDevice('d2');
    await settle();
    expect(be.snapshot().revocation).toEqual({ deviceId: 'd2', deviceName: 'phone', hosts: [{ name: 'orchard', confirmed: true }], rotate: [] });
  });

  it('runs the first run: setup shows the recovery code once', async () => {
    const { be, calls, on, settle } = world({ registered: false, engines: [] });
    on('GET', '/api/engines/local', { json: { url: 'https://127.0.0.1:7462', pin: 'P1', status: 'ok', known: null } });
    on('POST', '/api/engines/probe', { json: { url: 'https://127.0.0.1:7462', pin: 'P1', status: 'ok', known: null } });
    on('POST', '/api/engines', { json: { id: E1, name: 'pc', url: 'https://127.0.0.1:7462', pin: 'P1', engineId: null, enrolled: false, signedIn: true, store: false } });
    on('POST', '/api/device/setup', { json: { recoveryCode: 'ABCD-EFGH-JKMN', issue: 1 } });
    await be.start();
    expect(be.snapshot().firstRun).toMatchObject({ step: 'start', localAddress: 'https://127.0.0.1:7462' });
    expect(be.snapshot().vms).toEqual([]);
    // The engine on this machine needs no confirmed fingerprint: the agent's own probe of it is taken.
    be.firstRunStart({ how: 'setup', address: 'https://127.0.0.1:7462', deviceName: 'laptop', token: 'user-token' });
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([
      ['/api/engines', { address: 'https://127.0.0.1:7462', pin: 'P1', userToken: 'user-token', windowsSignIn: false }],
      ['/api/device/setup', { engine: E1, deviceName: 'laptop' }],
    ]);
    expect(be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['ABCD', 'EFGH', 'JKMN'], account: 'acct1' });
    on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true, recoveryUnconfirmed: false }, engines: [], pairing: null } });
    on('POST', '/api/device/recovery-code/kept', { status: 204 });
    be.firstRunNext();
    await settle();
    expect(posts(calls).at(-1)).toMatchObject({ path: '/api/device/recovery-code/kept', body: { issue: 1 } });
    expect(be.snapshot().firstRun).toBeNull();
    expect(JSON.stringify(be.snapshot())).not.toContain('EFGH');
  });

  it('shows a newly issued code after a reload before the first run\'s code was confirmed, and keeps it until the agent took the confirm', async () => {
    const { be, calls, on, settle } = world({ unconfirmed: true });
    on('POST', '/api/device/recovery-code/again', { json: { recoveryCode: 'WXYZ-QRST', issue: 2 } });
    await be.start();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/device/recovery-code/again']);
    expect(be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['WXYZ', 'QRST'], account: 'acct1' });
    // The normal screens wait.
    expect(calls.some(c => c.path.startsWith('/api/engines/'))).toBe(false);
    // An event in between issues no second code.
    be.onEvent('agent', { seq: 1, type: 'agent', engine: null, data: null });
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/device/recovery-code/again']);
    // The confirm fails: the code stays with the error.
    on('POST', '/api/device/recovery-code/kept', { status: 500, json: { title: 'The agent could not save it' } });
    be.firstRunNext();
    await settle();
    expect(be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['WXYZ', 'QRST'], problem: 'The agent could not save it' });
    on('POST', '/api/device/recovery-code/kept', { status: 204 });
    on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true, recoveryUnconfirmed: false }, engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'AB', engineId: 'x', enrolled: true, signedIn: false, store: true }], pairing: null } });
    be.firstRunNext();
    await settle();
    await settle();
    expect(be.snapshot().firstRun).toBeNull();
    expect(be.snapshot().vms.map(v => v.name)).toContain('atlas');
    expect(JSON.stringify(be.snapshot())).not.toContain('QRST');
    expect(calls.every(c => !c.query.includes('WXYZ'))).toBe(true);
  });

  it('keeps the first run until the recovery code was shown, when the agent reports the new identity first', async () => {
    const { be, on, settle } = world({ registered: false, engines: [] });
    on('POST', '/api/engines/probe', { json: { url: 'https://orchard.test:8443', pin: 'P', status: 'ok', known: null } });
    on('POST', '/api/engines', { json: { id: E1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'P', engineId: null, enrolled: false, signedIn: true, store: false } });
    on('POST', '/api/device/setup', () => {
      on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true }, engines: [], pairing: null } });
      be.onEvent('agent', { seq: 1, type: 'agent', engine: null, data: null });
      return { json: { recoveryCode: 'ABCD-EFGH', issue: 1 } };
    });
    await be.start();
    be.firstRunStart({ how: 'setup', address: 'https://orchard.test:8443', deviceName: 'laptop', token: 't', pin: 'P' });
    await settle();
    await settle();
    expect(be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['ABCD', 'EFGH'] });
  });

  it('pairs as a new device and closes the first run when the pairing is done', async () => {
    const { be, calls, on, settle } = world({ registered: false, engines: [] });
    on('POST', '/api/engines/probe', { json: { url: 'https://orchard.test:8443', pin: 'P', status: 'ok', known: E1 } });
    on('POST', '/api/pairing', { json: { pairingId: 'p-9', state: 'waiting', words: ['a', 'b'], expiresAt: '2026-11-13T10:10:00Z', storeUrl: '', storePin: '', problem: null } });
    await be.start();
    be.firstRunStart({ how: 'pair', address: 'https://orchard.test:8443', deviceName: 'tablet' });
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/engines/probe', '/api/pairing']);
    expect(be.snapshot().firstRun).toMatchObject({ step: 'pairing', pairing: { code: 'p-9', words: ['a', 'b'] } });
    be.onEvent('pairing', { seq: 3, type: 'pairing', engine: null, data: { state: 'done' } });
    expect(be.snapshot().firstRun).toBeNull();
    await settle();
  });

  it('creates a VM on a pasted host with its token and settings in one request through the agent; the engine provisions it', async () => {
    const { be, calls, on, settle } = world();
    on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'P2', status: 'ok', known: null } });
    on('POST', '/api/engines', { json: { id: E2, name: 'lab', url: 'https://lab.test:8443', pin: 'P2', engineId: null, enrolled: true, signedIn: true, store: false } });
    on('GET', `/api/engines/${E2}/api/v1/vm-defaults`, { json: { hostLogicalCpus: 8, maximumCpus: 8, recommendedCpus: 8, maximumRamGb: 16, recommendedRamGb: 8, recommendedDiskGb: 64 } });
    on('POST', `/api/engines/${E2}/vms`, { status: 202, json: { jobId: 'c1', nested: false, nestedFromHostDefault: true, ignoredOptions: [], hostsWithoutCredential: [] } });
    on('GET', `/api/engines/${E2}/api/v1/whoami`, { json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: 'user' } });
    on('GET', `/api/engines/${E2}/api/v1/vms`, { json: [vmResponse('quill', 'alex')] });
    on('GET', `/api/engines/${E2}/api/v1/jobs/c1`, { json: job('c1', 'quill', 'running', 'create-vm') });
    on('POST', `/api/engines/${E2}/vms/quill/jobs`, { status: 202, json: { jobId: 'p1', kind: 'provision', replayed: false } });
    on('GET', `/api/engines/${E2}/api/v1/jobs/p1`, { json: job('p1', 'quill', 'running') });
    await be.start();
    expect(be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'windows' })).toBeNull();
    expect(be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'token', token: 'tok', pin: 'p2' })).toBe('quill');
    await settle();
    expect(activeJob(be.snapshot(), 'quill')).toMatchObject({ kind: 'create', state: 'running' });
    on('GET', `/api/engines/${E2}/api/v1/jobs/c1`, { json: job('c1', 'quill', 'succeeded', 'create-vm') });
    be.onEvent('job.ended', { seq: 4, type: 'job.ended', engine: E2, data: { id: 'c1', vm: 'quill' } });
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([
      ['/api/engines/probe', { address: 'https://lab.test:8443' }],
      ['/api/engines', { address: 'https://lab.test:8443', pin: 'p2', userToken: 'tok', windowsSignIn: false }],
      [`/api/engines/${E2}/vms`, { name: 'quill', ramGb: 8, diskGb: 64, desired: { projects: [], settings: {} } }],
    ]);
    expect(be.snapshot().notice?.text).toBe('Created quill');
  });

  it('adds a user and hands out the first token once', async () => {
    const { be, calls, on, settle } = world();
    on('POST', `/api/engines/${E1}/api/v1/users`, { status: 201, json: { name: 'kai', role: 'user', maxVms: 1, allowHostForwards: false, created: '' } });
    on('POST', `/api/engines/${E1}/api/v1/users/kai/tokens`, { status: 201, json: { id: 't1', label: 'first client', token: 'cst_secret', created: '' } });
    await be.start();
    be.addUser(E1, { name: 'kai', role: 'user', maxVms: 1, hostForwards: 'refused' });
    await settle();
    expect(posts(calls)[0].body).toEqual({ name: 'kai', role: 'user', maxVms: 1, allowHostForwards: false });
    expect(be.snapshot().handout).toEqual({ hostId: E1, user: 'kai', address: 'https://orchard.test:8443', token: 'cst_secret' });
    be.closeHandout();
    expect(be.snapshot().handout).toBeNull();
  });
});

describe('ApiBackend host administration', () => {
  it('reads update rows, sharing and Windows guests as the engine reports them', async () => {
    const { be, on } = world();
    const p = `/api/engines/${E1}/api/v1`;
    const row = (state: string) => ({ updateId: state, commit: 'e'.repeat(40), state, phase: null, phases: [], started: '2026-11-10T10:00:00Z', finished: null, error: null, blockingJobs: [] });
    on('GET', `${p}/host/updates/status`, { json: { supportsAutoApply: true, installed: { commit: 'c'.repeat(40), packageVersion: '3.1.0', installedAt: null, source: 'x' },
      current: row('succeeded'), history: [row('succeeded'), row('rolledBack'), row('applyFailed'), row('draining')], recoveryRecord: null, latestKnown: null, version: '3.1.0', channel: 'main', auto: false, available: null, availableError: null, databaseCopy: null } });
    on('GET', `${p}/vms`, { json: [vmResponse('atlas', 'alex'), vmResponse('kid', 'kai', { kind: 'child', parent: 'x', sharing: 'host', shared: false })] });
    on('GET', `${p}/host/windows`, { json: { keys: [{ id: 'k1', product: 'Windows 11', edition: 'Pro', kind: 'kms-client', partialKey: 'ABCDE', used: 1 }], machines: [],
      guests: [{ vmName: 'win', incarnation: 'old', keyId: 'k1', released: true, stage: 'done', activation: 'activated' }, { vmName: 'win', incarnation: 'new', keyId: null, stage: 'installing', activation: 'not-activated' }] } });
    await be.start();
    const h = be.snapshot().hosts[0];
    expect(h.updating).toBe(false);
    expect(h.updates.map(u => u.state)).toEqual(['done', 'rolled back', 'failed', 'running']);
    expect(h.vms.find(v => v.name === 'kid')!.shared).toBe(true);
    expect(h.winGuests).toEqual([{ name: 'win', stage: 'installing', activation: 'not activated', keyId: null, incarnation: 'new' }]);
    expect(h.winKeys[0]).toMatchObject({ kind: 'KMS', held: false });
  });

  it('deletes a primary with children only when the engine lists the children the admin confirmed', async () => {
    const { be, calls, on, settle } = world();
    const p = `/api/engines/${E1}/api/v1`;
    on('GET', `${p}/vms`, { json: [vmResponse('atlas', 'alex'), vmResponse('kid', 'alex', { kind: 'child', parent: 'atlas' })] });
    on('DELETE', `${p}/vms/atlas`, body => body
      ? { status: 202, json: { jobId: 'd1' } }
      : { status: 409, json: { title: 'cascade-confirmation-required', status: 409, code: 'cascade-confirmation-required', children: [{ name: 'kid' }], cascadeToken: 'tok' } });
    await be.start();
    be.hostVmAction(E1, 'atlas', 'delete');
    await settle();
    expect(calls.filter(c => c.method === 'DELETE').map(c => c.body)).toEqual([undefined, { cascade: { token: 'tok' } }]);
    on('DELETE', `${p}/vms/atlas`, body => body ? { status: 202, json: { jobId: 'd1' } }
      : { status: 409, json: { title: 'x', status: 409, code: 'cascade-confirmation-required', children: [{ name: 'kid' }, { name: 'other' }], cascadeToken: 'tok' } });
    const before = calls.filter(c => c.method === 'DELETE').length;
    be.hostVmAction(E1, 'atlas', 'delete');
    await settle();
    expect(calls.filter(c => c.method === 'DELETE').length).toBe(before + 1);
    expect(be.snapshot().notice?.text).toContain('changed');
  });
});

describe('SseParser', () => {
  it('splits events across chunks', () => {
    const got: [string, string, string | null][] = [];
    const p = new SseParser((t, d, id) => got.push([t, d, id]));
    p.feed(': connected\n\nid: 5\nevent: job.progress\ndata: {"a"');
    p.feed(':1}\n\nevent: reset\r\ndata: {"seq":9}\r\n\r\n');
    expect(got).toEqual([['job.progress', '{"a":1}', '5'], ['reset', '{"seq":9}', null]]);
  });
});

describe('ApiBackend key vault', () => {
  const secret = (name: string, extra: Record<string, unknown> = {}) => ({
    name, description: `note of ${name}`, hasUsername: false, autoApprove: false, readable: true, updatedAt: Date.parse('2026-11-13T10:00:00Z'), updatedBy: 'laptop',
    provenance: { kind: 'device', vm: null, engine: null, device: 'd1', requestId: null }, leases: [], ...extra,
  });
  const approval = (engine: string, id: string, vm: string, extra: Record<string, unknown> = {}) => ({
    engine, engineName: engine === E1 ? 'orchard' : 'lab', id, vm, op: 'request', title: 'Key vault — access request', message: 'The VM asks', action: 'Approve',
    names: ['db'], reason: 'run the migration', source: `root@${vm}`, createdAt: 1, deadline: 2, ...extra,
  });

  function vaultWorld(engines?: { id: string; name: string; url: string }[]) {
    const w = world(engines ? { engines } : {});
    w.on('GET', '/api/vault/secrets', { json: [secret('db', { leases: [{ engine: E1, engineName: 'orchard', vm: 'atlas', name: 'db', usesLeft: null, expiresAt: Date.now() + 3 * 3600e3 }] }), secret('npm', { autoApprove: true })] });
    w.on('GET', '/api/vault/history', { json: [{ name: 'db', action: 'added', revision: 1, at: '2026-11-13T10:00:00Z', actor: 'device:d1', provenance: { kind: 'device', vm: null, engine: null, device: 'd1', requestId: null } }] });
    w.on('GET', '/api/vault/approvals', { json: { approvals: [], unreachable: [] } });
    w.on('GET', '/api/vault/entry-requests', { json: { requests: [], unreachable: [] } });
    for (const e of engines ?? [{ id: E1 }]) w.on('GET', `/api/vault/engines/${e.id}/mode`, { json: { engine: e.id, mode: 'locked', holdsKey: false, records: true, unlockedVms: [] } });
    return w;
  }

  it('lists secrets, leases, history and each host\'s mode, never a value', async () => {
    const { be } = vaultWorld();
    await be.start();
    const s = be.snapshot();
    expect(s.secrets.map(x => [x.name, x.note, x.usedBy, x.auto])).toEqual([['db', 'note of db', ['atlas'], false], ['npm', 'note of npm', [], true]]);
    expect(s.leases).toEqual([expect.objectContaining({ secret: 'db', vmName: 'atlas · orchard', left: '3 h' })]);
    expect(s.history).toEqual([{ at: '2026-11-13 10:00', vmName: 'laptop', secret: 'db', event: 'Added' }]);
    expect(s.vaultModes).toEqual([{ hostId: E1, hostName: 'orchard', mode: 'locked' }]);
  });

  it('adds, replaces, reveals, deletes and switches auto-approve through the agent; a value stays out of the state', async () => {
    const { be, calls, on, settle } = vaultWorld();
    on('POST', '/api/vault/secrets', { json: secret('api') });
    on('PATCH', '/api/vault/secrets/db', { json: secret('db') });
    on('POST', '/api/vault/secrets/db/reveal', { json: { name: 'db', username: '', value: 'hunter2-value' } });
    on('DELETE', '/api/vault/secrets/npm', { status: 204 });
    on('PUT', '/api/vault/secrets/db/auto-approve', { status: 204 });
    await be.start();
    be.addSecret(' api ', 'the api', 'typed-value-1');
    be.replaceSecret('db', 'typed-value-2');
    expect(await be.revealSecret('db')).toBe('hunter2-value');
    be.deleteSecret('npm');
    be.setAutoApprove('db', true);
    await settle();
    const sent = posts(calls).map(c => [c.method, c.path, c.body]);
    expect(sent.length).toBe(5);
    expect(sent).toEqual(expect.arrayContaining([
      ['POST', '/api/vault/secrets', { name: 'api', description: 'the api', value: 'typed-value-1', autoApprove: false }],
      ['PATCH', '/api/vault/secrets/db', { value: 'typed-value-2' }],
      ['POST', '/api/vault/secrets/db/reveal', undefined],
      ['DELETE', '/api/vault/secrets/npm', undefined],
      ['PUT', '/api/vault/secrets/db/auto-approve', { on: true }],
    ]));
    const all = JSON.stringify(be.snapshot());
    for (const v of ['hunter2-value', 'typed-value-1', 'typed-value-2']) expect(all).not.toContain(v);
  });

  it('shows each request with the VM and host its engine names, the agent\'s words apart, and answers it at that engine', async () => {
    const two = [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }];
    const { be, calls, on, settle } = vaultWorld(two);
    on('GET', '/api/vault/approvals', { json: { approvals: [approval(E2, 'a1', 'harbor'), approval(E1, 'a1', 'atlas', { action: 'Allow once' })], unreachable: [] } });
    on('GET', '/api/vault/entry-requests', { json: { requests: [{ engine: E1, engineName: 'orchard', id: 'e1', vm: 'atlas', name: 'stripe', description: 'Stripe test key', reason: 'for the checkout tests', source: 'root@atlas', createdAt: 3, expiresAt: 4 }], unreachable: [] } });
    on('POST', `/api/vault/approvals/${E2}/a1`, { status: 204 });
    await be.start();
    const r = be.snapshot().vaultRequests;
    expect(r.map(x => [x.id, x.kind, x.vmName, x.hostName, x.vmId, x.reason, x.action])).toEqual([
      [`${E2}/a1`, 'lease', 'harbor', 'lab', 'harbor', 'run the migration', 'Approve'],
      [`${E1}/a1`, 'lease', 'atlas', 'orchard', 'atlas', 'run the migration', 'Allow once'],
      [`${E1}/e1`, 'enter', 'atlas', 'orchard', 'atlas', 'Stripe test key — for the checkout tests', undefined],
    ]);
    // The same id on two engines: the answer goes to the engine of the card that was clicked.
    be.answerLease(`${E2}/a1`, true);
    expect(be.snapshot().vaultRequests.map(x => x.id)).toEqual([`${E1}/a1`, `${E1}/e1`]);
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([[`/api/vault/approvals/${E2}/a1`, { decision: 'allow' }]]);
    // A read that still lists it does not bring the answered card back.
    expect(be.snapshot().vaultRequests.map(x => x.id)).toEqual([`${E1}/a1`, `${E1}/e1`]);
  });

  it('brings a card back when its answer failed', async () => {
    const { be, on, settle } = vaultWorld();
    on('GET', '/api/vault/approvals', { json: { approvals: [approval(E1, 'a1', 'atlas')], unreachable: [] } });
    on('POST', `/api/vault/approvals/${E1}/a1`, { status: 502, json: { title: 'engine-unreachable', status: 502, code: 'engine-unreachable' } });
    await be.start();
    be.answerLease(`${E1}/a1`, false);
    expect(be.snapshot().vaultRequests).toEqual([]);
    await settle();
    expect(be.snapshot().vaultRequests.map(x => x.id)).toEqual([`${E1}/a1`]);
    expect(be.snapshot().notice?.text).toBe('engine-unreachable');
  });

  it('saves an entered value at the engine that asked, overwrites nothing, and declines', async () => {
    const { be, calls, on, settle } = vaultWorld();
    const req = (id: string) => ({ engine: E1, engineName: 'orchard', id, vm: 'atlas', name: 'stripe', description: 'd', reason: '', source: '', createdAt: 1, expiresAt: 2 });
    on('GET', '/api/vault/entry-requests', { json: { requests: [req('e1'), req('e2'), req('e3')], unreachable: [] } });
    on('POST', `/api/vault/entry-requests/${E1}/e1`, { status: 204 });
    on('POST', `/api/vault/entry-requests/${E1}/e2`, { status: 409, json: { title: 'exists', status: 409, code: 'exists' } });
    on('POST', `/api/vault/entry-requests/${E1}/e3/decline`, { status: 204 });
    await be.start();
    be.enterSecret(`${E1}/e1`, 'pasted-value-1');
    await settle();
    expect(be.snapshot().notice?.text).toBe('Saved stripe');
    be.enterSecret(`${E1}/e2`, 'pasted-value-2');
    await settle();
    expect(be.snapshot().notice?.text).toBe('stripe exists already; nothing was overwritten');
    be.enterSecret(`${E1}/e3`, null);
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([
      [`/api/vault/entry-requests/${E1}/e1`, { value: 'pasted-value-1' }],
      [`/api/vault/entry-requests/${E1}/e2`, { value: 'pasted-value-2' }],
      [`/api/vault/entry-requests/${E1}/e3/decline`, undefined],
    ]);
    expect(JSON.stringify(be.snapshot())).not.toContain('pasted-value');
  });

  it('keeps the requests of an engine that did not answer and says which', async () => {
    const two = [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }];
    const { be, on } = vaultWorld(two);
    on('GET', '/api/vault/approvals', { json: { approvals: [approval(E1, 'a1', 'atlas'), approval(E2, 'a2', 'harbor')], unreachable: [] } });
    await be.start();
    on('GET', '/api/vault/approvals', { json: { approvals: [], unreachable: [E2] } });
    be.onEvent('agent', { seq: 1, type: 'agent', engine: null, data: null });
    await new Promise(r => setTimeout(r, 30));
    expect(be.snapshot().vaultRequests.map(x => x.id)).toEqual([`${E2}/a2`]);
    expect(be.snapshot().problems.requests).toBe('Not reachable: lab');
  });

  it('sets a host\'s vault mode and lists what a revoked device could read', async () => {
    const { be, calls, on, settle } = vaultWorld();
    on('PUT', `/api/vault/engines/${E1}/mode`, { json: { engine: E1, mode: 'available', holdsKey: true, records: true, unlockedVms: [] } });
    on('GET', '/api/devices', { json: [{ id: 'd1', name: 'laptop', me: true, revoked: false, added: '2026-11-01T00:00:00Z', acknowledgedBy: [] }, { id: 'd2', name: 'phone', me: false, revoked: true, added: '2026-11-02T00:00:00Z', acknowledgedBy: ['x'] }] });
    on('GET', '/api/devices/d2/secrets', { json: [{ name: 'db', description: '' }, { name: 'npm', description: '' }] });
    await be.start();
    be.setVaultMode(E1, 'available');
    be.openRevocation('d2');
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([[`/api/vault/engines/${E1}/mode`, { mode: 'available' }]]);
    const r = be.snapshot().revocation!;
    expect(r.hosts).toEqual([{ name: 'orchard', confirmed: true }]);
    expect(r.rotate.map(x => [x.secret, x.done])).toEqual([['db', false], ['npm', false]]);
    be.markRotated('db');
    expect(be.snapshot().revocation!.rotate[0].done).toBe(true);
  });
});

describe('ApiBackend profiles', () => {
  const doc = (name: string) => ({ name, repos: [{ url: 'https://example.test/r.git' }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} });
  function profileWorld() {
    const w = world();
    const p = `/api/engines/${E1}/api/v1`;
    w.on('GET', `${p}/profiles`, { json: [{ name: 'web', revision: 4 }, { name: 'tools', revision: 2 }] });
    w.on('GET', `${p}/profiles/web`, { json: { name: 'web', revision: 4, profile: doc('web') } });
    w.on('GET', `${p}/profiles/tools`, { json: { name: 'tools', revision: 2, profile: { ...doc('tools'), extra: 1 } } });
    w.on('GET', `${p}/profile-conflicts`, { json: [{ id: 'c1', profile: 'tools', profileRevision: 2, fields: ['repos'], base: null, ours: null, theirs: null, merged: null }] });
    return { ...w, p };
  }

  it('reads profiles from the store\'s engine, keeps the document and marks an open conflict', async () => {
    const { be } = profileWorld();
    await be.start();
    const s = be.snapshot();
    expect(s.projects.map(x => [x.name, x.revision, x.conflict, x.repos.length])).toEqual([['tools', 2, true, 1], ['web', 4, false, 1]]);
    expect(s.projects[0].doc).toMatchObject({ extra: 1 });
    expect(s.sync.remote).toBe('orchard');
  });

  it('saves with the revision the editor read; the engine\'s answer decides what the page says', async () => {
    const { be, calls, on, p } = profileWorld();
    await be.start();
    on('PUT', `${p}/profiles/web`, { json: { status: 'written', revision: 5, file: '', conflictId: null, fields: null } });
    expect(await be.saveProfile('web', doc('web'), 3)).toEqual({ ok: true, text: 'Saved' });
    expect(posts(calls).at(-1)).toMatchObject({ method: 'PUT', path: `${p}/profiles/web`, body: { profile: doc('web'), baseRevision: 3 } });
    on('PUT', `${p}/profiles/web`, { json: { status: 'conflict', revision: null, file: null, conflictId: 'c2', fields: ['repos'] } });
    expect(await be.saveProfile('web', doc('web'), 4)).toEqual({ ok: false, changed: true, text: 'web changed meanwhile; an agent is resolving this' });
    on('PUT', `${p}/profiles/web`, { status: 409, json: { title: 'refused-by-store', status: 409, code: 'refused-by-store' } });
    on('GET', `${p}/profiles`, { json: [{ name: 'web', revision: 6 }] });
    on('GET', `${p}/profiles/web`, { json: { name: 'web', revision: 6, profile: doc('web') } });
    expect(await be.saveProfile('web', doc('web'), 4)).toEqual({ ok: false, changed: true, text: 'web changed meanwhile; this is the current version' });
    expect(be.snapshot().projects.find(x => x.name === 'web')!.revision).toBe(6);
    on('PUT', `${p}/profiles/web`, { status: 400, json: { title: 'invalid-profile', status: 400, code: 'invalid-profile', detail: 'repos[0].url is missing' } });
    expect(await be.saveProfile('web', doc('web'), 6)).toEqual({ ok: false, changed: false, text: 'repos[0].url is missing' });
  });

  it('keeps a failing profile route to the projects page', async () => {
    const { be, on, p } = profileWorld();
    on('GET', `${p}/profiles`, { status: 403, json: { title: 'devices-only', status: 403, code: 'devices-only' } });
    await be.start();
    expect(be.snapshot().problems.projects).toBe('devices-only');
    expect(be.snapshot().vms.length).toBe(1);
  });
});

describe('ApiBackend events and jobs', () => {
  it('re-reads only the VM\'s backups after a backup event, and the engine after a VM change', async () => {
    const { be, calls, settle } = world();
    await be.start();
    const before = calls.length;
    be.onEvent('backup.added', { seq: 1, type: 'backup.added', engine: E1, data: { id: 'b1', vm: 'atlas' } });
    await settle();
    expect(calls.slice(before).map(c => c.path)).toEqual([`/api/engines/${E1}/api/v1/vms/atlas/backups`]);
    const mid = calls.length;
    be.onEvent('vm.power', { seq: 2, type: 'vm.power', engine: E1, data: { id: 'atlas', vm: 'atlas' } });
    await settle();
    expect(calls.slice(mid).map(c => c.path)).toContain(`/api/engines/${E1}/api/v1/vms`);
  });

  it('never shows a job as running again after it ended', async () => {
    const { be, on, settle } = world();
    on('POST', `/api/engines/${E1}/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'running') });
    await be.start();
    be.reprovision('atlas');
    await settle();
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'failed') });
    be.onEvent('job.ended', { seq: 3, type: 'job.ended', engine: E1, data: { id: 'j1', vm: 'atlas' } });
    await settle();
    // A late progress read and a stale list still say running.
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'running') });
    on('GET', `/api/engines/${E1}/api/v1/jobs`, { json: [job('j1', 'atlas', 'running')] });
    be.onEvent('job.progress', { seq: 2, type: 'job.progress', engine: E1, data: { id: 'j1', vm: 'atlas' } });
    be.onEvent('engine.reset', { seq: 4, type: 'engine.reset', engine: E1, data: null });
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ state: 'failed' });
  });

  it('starts a reinstall with the fresh-image switch through the agent, shows its failed step, and retries it through the agent', async () => {
    const { be, calls, on, settle } = world();
    on('POST', `/api/engines/${E1}/vms/atlas/jobs`, { status: 202, json: { jobId: 'r1', kind: 'reinstall', replayed: false } });
    on('GET', `/api/engines/${E1}/api/v1/jobs/r1`, { json: job('r1', 'atlas', 'running', 'reinstall', [{ at: '', text: 'backing up', step: 'backup', state: 'running', percent: null }]) });
    await be.start();
    be.reinstall('atlas', true);
    await settle();
    expect(be.snapshot().notice).toBeNull();
    expect(posts(calls)[0].body).toMatchObject({ kind: 'reinstall', freshImage: true });
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ kind: 'reinstall', state: 'running', step: 'Backing up', stepIndex: 0, stepCount: 10 });
    on('GET', `/api/engines/${E1}/api/v1/jobs/r1`, { json: { ...job('r1', 'atlas', 'failed', 'reinstall', [{ at: '', text: 'apt locked', step: 'provision', state: 'failed', percent: null }]), result: { failedStep: 'create' }, error: 'driver-failed' } });
    be.onEvent('job.ended', { seq: 5, type: 'job.ended', engine: E1, data: { id: 'r1', vm: 'atlas' } });
    await settle();
    const j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ kind: 'reinstall', state: 'failed', step: 'Creating', error: 'driver-failed', log: ['apt locked'] });
    on('POST', `/api/engines/${E1}/jobs/r1/retry`, { status: 202, json: { jobId: 'r2', kind: 'reinstall', replayed: false } });
    on('GET', `/api/engines/${E1}/api/v1/jobs/r2`, { json: { ...job('r2', 'atlas', 'running', 'reinstall'), created: '2026-11-13T11:00:00Z' } });
    on('GET', `/api/engines/${E1}/api/v1/jobs`, { json: [{ ...job('r2', 'atlas', 'running', 'reinstall'), created: '2026-11-13T11:00:00Z' }] });
    be.retryJob(j.id);
    await settle();
    const p = posts(calls);
    expect(p.map(c => c.path)).toEqual([`/api/engines/${E1}/vms/atlas/jobs`, `/api/engines/${E1}/jobs/r1/retry`]);
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ kind: 'reinstall', state: 'running' });
  });

  it('restores a backup through the agent, which holds the key, and retries a failed restore the same way', async () => {
    const { be, calls, on, settle } = world();
    const b = 'c'.repeat(32);
    let n = 0;
    on('POST', `/api/engines/${E1}/vms/atlas/backups/${b}/restore`, () => ({ json: { jobId: `x${++n}`, replayed: false } }));
    on('GET', `/api/engines/${E1}/api/v1/jobs/x1`, { json: job('x1', 'atlas', 'running', 'restore') });
    on('GET', `/api/engines/${E1}/api/v1/jobs`, { json: [job('x1', 'atlas', 'running', 'restore')] });
    await be.start();
    be.restoreBackup('atlas', 'f'.repeat(32));
    be.restoreBackup('atlas', b);
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ kind: 'restore', state: 'running', step: 'Restoring' });
    on('GET', `/api/engines/${E1}/api/v1/jobs/x1`, { json: { ...job('x1', 'atlas', 'failed', 'restore'), result: { backupId: b } } });
    be.onEvent('job.ended', { seq: 6, type: 'job.ended', engine: E1, data: { id: 'x1', vm: 'atlas' } });
    await settle();
    on('GET', `/api/engines/${E1}/api/v1/jobs/x2`, { json: { ...job('x2', 'atlas', 'succeeded', 'restore'), created: '2026-11-13T11:00:00Z' } });
    be.retryJob(activeJob(be.snapshot(), 'atlas')!.id);
    await settle();
    const p = posts(calls);
    // Only the backup the VM has; no restore route of the engine is called without the key.
    expect(p.map(c => c.path)).toEqual([`/api/engines/${E1}/vms/atlas/backups/${b}/restore`, `/api/engines/${E1}/vms/atlas/backups/${b}/restore`]);
    expect((p[0].body as { requestId: string }).requestId).not.toBe((p[1].body as { requestId: string }).requestId);
    expect(be.snapshot().notice?.text).toBe('Restored atlas');
  });
});

describe('ApiBackend device, connections and vault', () => {
  const conn = { engine: E1, vm: 'atlas', alias: 'atlas', state: 'running', address: { host: 'atlas.test', port: 22, kind: 'forwarded' }, linked: true,
    forwards: [
      { id: 'f1', label: 'vite', target: 'client', status: 'up', vmPort: 5173, localPort: 5173, url: 'http://127.0.0.1:5173', message: '', closable: true, child: null },
      { id: 'f2', label: 'web', target: 'lan', status: 'up', vmPort: 8000, localPort: null, url: null, message: '', closable: false, child: null },
    ],
    mic: { enabled: true, capturing: false, error: null }, sshConfig: 'written' };

  it('opens T3 Code, VS Code and SSH through the agent for the VM\'s own engine', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    for (const [e, vm] of [[E1, 'atlas'], [E2, 'harbor']]) on('POST', `/api/connections/${e}/${vm}/open`, b => ({ json: { what: (b as { what: string }).what, alias: vm } }));
    await be.start();
    be.open('harbor', 't3');
    be.open('atlas', 'ssh');
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([[`/api/connections/${E2}/harbor/open`, { what: 't3' }], [`/api/connections/${E1}/atlas/open`, { what: 'ssh' }]]);
  });

  it('shows this device\'s forwards, microphone and alias, and closes only a forward it opened', async () => {
    const { be, calls, on, settle } = world();
    on('GET', '/api/connections', { json: [conn] });
    on('DELETE', `/api/connections/${E1}/atlas/forwards/f1`, { status: 204 });
    on('PUT', `/api/connections/${E1}/atlas/mic`, { json: { enabled: false, capturing: false, error: null } });
    on('DELETE', `/api/connections/${E1}/atlas/ssh-config`, { status: 204 });
    await be.start();
    const atlas = be.snapshot().vms.find(v => v.name === 'atlas')!;
    expect(atlas.forwards.map(f => [f.id, f.port, f.to, f.closable])).toEqual([['f1', 5173, 'This device', true], ['f2', 8000, 'LAN', false]]);
    expect(atlas.sharing.mic).toBe(true);
    expect(atlas.link).toEqual({ alias: 'atlas', sshConfig: 'written', share: null });
    be.closeForward('atlas', 'f2');
    be.closeForward('atlas', 'f1');
    be.setSharing('atlas', 'mic', false);
    be.setSshAlias('atlas', false);
    await settle();
    expect(posts(calls).map(c => [c.method, c.path, c.body]).sort()).toEqual([
      ['DELETE', `/api/connections/${E1}/atlas/forwards/f1`, undefined], ['DELETE', `/api/connections/${E1}/atlas/ssh-config`, undefined], ['PUT', `/api/connections/${E1}/atlas/mic`, { on: false }],
    ]);
  });

  it('keeps a failing connections read to the Connections page', async () => {
    const { be, on } = world();
    on('GET', '/api/connections', { status: 500, json: { title: 'links down', status: 500 } });
    await be.start();
    const atlas = be.snapshot().vms.find(v => v.name === 'atlas')!;
    expect(atlas.problems).toEqual({ connection: 'links down' });
    expect(be.snapshot().hosts.every(h => !h.problem)).toBe(true);
  });

  it('reads this device\'s channel and updates, and saves them through the agent', async () => {
    const { be, calls, on, settle } = world();
    on('GET', '/api/update', { json: { version: '3.1.0', channel: 'nightly', available: '3.3.0', state: 'idle', error: null, installed: true, checkedAt: null } });
    on('GET', '/api/device/settings', { json: { channel: 'nightly', autoUpdate: true, startWithOs: true } });
    on('GET', `/api/engines/${E1}/api/v1/me/updates`, { json: { autoReprovision: true } });
    on('PUT', '/api/device/settings', b => ({ json: { channel: 'stable', autoUpdate: true, startWithOs: false, ...(b as object) } }));
    on('PUT', `/api/engines/${E1}/api/v1/me/updates`, { json: { autoReprovision: false } });
    await be.start();
    const s = be.snapshot();
    expect(s.release).toBe('3.3.0');
    expect(s.prefs).toMatchObject({ channel: 'nightly', autoUpdate: true, startWithOs: true });
    be.setPrefs({ channel: 'stable' });
    be.setPrefs({ startWithOs: false });
    be.setPrefs({ autoUpdate: false });
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([
      ['/api/device/settings', { channel: 'stable' }], ['/api/device/settings', { startWithOs: false }],
      ['/api/device/settings', { autoUpdate: false }], [`/api/engines/${E1}/api/v1/me/updates`, { autoReprovision: false }],
    ]);
  });

  it('allows an approval for the span chosen, or the one the VM asked for, and takes a lease back at its engine', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    const item = (id: string, engine: string) => ({ engine, engineName: engine === E1 ? 'orchard' : 'lab', id, vm: 'atlas', op: 'request', title: '', message: '', action: 'Approve', names: ['tok'], reason: 'r', source: '', createdAt: 0, deadline: 0, uses: null, ttlSeconds: 7200 });
    on('GET', '/api/vault/approvals', { json: { approvals: [item('a1', E1), item('a2', E2)], unreachable: [] } });
    on('GET', '/api/vault/entry-requests', { json: { requests: [], unreachable: [] } });
    on('GET', '/api/vault/secrets', { json: [{ name: 'tok', description: '', hasUsername: false, autoApprove: false, readable: true, updatedAt: 0, updatedBy: '', provenance: null,
      leases: [{ engine: E2, engineName: 'lab', id: 'L9', vm: 'harbor', name: 'tok', usesLeft: null, expiresAt: Date.now() + 3600e3 }] }] });
    on('POST', `/api/vault/approvals/${E1}/a1`, { status: 204 });
    on('POST', `/api/vault/approvals/${E2}/a2`, { status: 204 });
    on('DELETE', `/api/vault/leases/${E2}/L9`, { status: 204 });
    await be.start();
    const reqs = be.snapshot().vaultRequests;
    expect(reqs.map(r => [r.id, r.span, r.ttlSeconds])).toEqual([[`${E1}/a1`, '2 h', 7200], [`${E2}/a2`, '2 h', 7200]]);
    be.answerLease(`${E1}/a1`, true, 900);
    be.answerLease(`${E2}/a2`, true);
    be.takeBack(be.snapshot().leases[0].id);
    await settle();
    expect(posts(calls).map(c => [c.method, c.path, c.body]).sort()).toEqual([
      ['DELETE', `/api/vault/leases/${E2}/L9`, undefined],
      ['POST', `/api/vault/approvals/${E1}/a1`, { decision: 'allow', ttlSeconds: 900 }],
      ['POST', `/api/vault/approvals/${E2}/a2`, { decision: 'allow' }],
    ]);
  });

  it('reads vault requests when the engine announces one, with no timer', async () => {
    const { be, calls, on, settle } = world();
    on('GET', '/api/vault/approvals', { json: { approvals: [], unreachable: [] } });
    on('GET', '/api/vault/entry-requests', { json: { requests: [], unreachable: [] } });
    await be.start();
    expect(be.snapshot().vaultRequests).toEqual([]);
    on('GET', '/api/vault/entry-requests', { json: { requests: [{ engine: E1, engineName: 'orchard', id: 'e1', vm: 'atlas', name: 'api-key', description: 'Key', reason: '', createdAt: 0, expiresAt: 0 }], unreachable: [] } });
    const before = calls.filter(c => c.path === '/api/vault/entry-requests').length;
    be.onEvent('vault.entry-request.added', { seq: 7, type: 'vault.entry-request.added', engine: E1, data: { id: 'e1' } });
    await settle();
    expect(calls.filter(c => c.path === '/api/vault/entry-requests').length).toBe(before + 1);
    expect(be.snapshot().vaultRequests.map(r => r.secret)).toEqual(['api-key']);
  });

  it('switches LAN access only on a solo host, and retries a failed provision on the host page', async () => {
    const { be, calls, on, settle } = world();
    on('GET', `/api/engines/${E1}/api/v1/host/lan-access`, { json: { enabled: false } });
    on('PUT', `/api/engines/${E1}/api/v1/host/lan-access`, { json: { enabled: true } });
    on('GET', `/api/engines/${E1}/api/v1/jobs`, { json: [job('h1', 'kai-box', 'failed'), job('h2', 'kai-box', 'failed', 'delete-vm')] });
    on('POST', `/api/engines/${E1}/jobs/h1/retry`, { status: 202, json: { jobId: 'h3', kind: 'provision', replayed: false } });
    await be.start();
    const host = be.snapshot().hosts[0];
    expect(host.lan).toBe(false);
    expect(host.jobs.map(j => [j.id, j.retry])).toEqual([['h2', false], ['h1', true]]);
    be.setLanAccess(E1, true);
    be.retryHostJob(E1, 'h2');
    be.retryHostJob(E1, 'h1');
    await settle();
    expect(posts(calls).map(c => [c.method, c.path, c.body])).toEqual([
      ['PUT', `/api/engines/${E1}/api/v1/host/lan-access`, { enabled: true }], ['POST', `/api/engines/${E1}/jobs/h1/retry`, {}],
    ]);
  });
});

describe('ApiBackend host fingerprint', () => {
  it('adds a remote host only with the fingerprint the user confirmed', async () => {
    const { be, calls, on, settle } = world();
    on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'AA11', status: 'ok', known: null } });
    await be.start();
    expect(await be.probeHost(' lab.test:8443 ')).toEqual({ url: 'https://lab.test:8443', pin: 'AA11', known: false });
    be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'token', token: 'tok' });
    await settle();
    expect(be.snapshot().notice?.text).toMatch(/fingerprint/);
    be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'token', token: 'tok', pin: 'BB22' });
    await settle();
    expect(be.snapshot().notice?.text).toMatch(/different certificate/);
    expect(calls.some(c => c.method === 'POST' && c.path === '/api/engines')).toBe(false);
  });
});

describe('ApiBackend lost jobs', () => {
  it('does not bring back a job the agent reported lost when an older read arrives', async () => {
    const { be, on, settle } = world();
    on('POST', `/api/engines/${E1}/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
    on('GET', `/api/engines/${E1}/api/v1/jobs/j1`, { json: job('j1', 'atlas', 'running') });
    await be.start();
    be.reprovision('atlas');
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ state: 'running' });
    be.onEvent('job.lost', { seq: 6, type: 'job.lost', engine: E1, data: { id: 'j1' } });
    be.onEvent('job.progress', { seq: 5, type: 'job.progress', engine: E1, data: { id: 'j1', vm: 'atlas' } });
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toBeNull();
  });
});

describe('ApiBackend repair agent', () => {
  const P = `/api/engines/${E1}/api/v1`;
  const failed = (id = 'f1', kind = 'provision') => ({ ...job(id, 'atlas', 'failed', kind), result: { fixPrompt: ['x'] } });
  const session = (o: Record<string, unknown> = {}) => ({
    id: 's1', vm: 'atlas', jobId: 'f1', jobKind: 'provision', state: 'working', outcome: null, automatic: false, started: '2026-11-13T10:01:00Z', ended: null,
    agent: 'claude', via: 'direct', retryJobId: null, retryRefused: null, lines: [], ...o,
  });
  const line = (seq: number, kind: string, text: string | null, outcome: string | null = null) => ({ seq, at: '2026-11-13T10:01:00Z', kind, text, outcome });

  it('offers the start on a failed job, starts it, follows its output, sends one reply while it waits, and stops it', async () => {
    const { be, calls, on, settle } = world();
    on('GET', `${P}/jobs`, { json: [failed()] });
    await be.start();
    await settle();
    let j = activeJob(be.snapshot(), 'atlas')!;
    // The VM was asked once for a session that ran before the page opened: none (404).
    expect(calls.filter(c => c.path === `${P}/vms/atlas/repair`).length).toBe(1);
    expect(j).toMatchObject({ state: 'failed', repair: null, canRepair: true });
    on('POST', `${P}/vms/atlas/repair`, { status: 202, json: session({ lines: [line(1, 'start', null), line(2, 'tool', 'Bash: tail log')] }) });
    be.startRepair(j.id);
    await settle();
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.canRepair).toBe(false);
    expect(j.repair).toMatchObject({ agent: 'Claude Code', state: 'working', messages: [{ from: 'tool', text: 'Bash: tail log' }], outcome: null });
    // A reply while it works is not sent.
    be.replyRepair(j.id, 'too early');
    await settle();
    expect(posts(calls).some(c => c.path.endsWith('/reply'))).toBe(false);
    // Output arrives as an event: only the lines after the last one read.
    on('GET', `${P}/vms/atlas/repair`, { json: session({ state: 'waiting', lines: [line(3, 'text', 'Retry, or switch the mirror?'), line(4, 'turn', null, 'question')] }) });
    be.onEvent('repair.output', { seq: 9, type: 'repair.output', engine: E1, data: { id: 's1', vm: 'atlas' } });
    await settle();
    expect(calls.filter(c => c.path === `${P}/vms/atlas/repair`).at(-1)!.query).toBe('?after=2');
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair!.state).toBe('asking');
    expect(j.repair!.messages.map(m => m.from)).toEqual(['tool', 'agent']);
    on('POST', `${P}/vms/atlas/repair/s1/reply`, { status: 204 });
    on('GET', `${P}/vms/atlas/repair`, { json: session({ lines: [line(5, 'reply', 'retry it')] }) });
    be.replyRepair(j.id, 'retry it');
    be.replyRepair(j.id, 'twice');
    await settle();
    expect(posts(calls).filter(c => c.path.endsWith('/reply')).map(c => [c.path, c.body])).toEqual([[`${P}/vms/atlas/repair/s1/reply`, { text: 'retry it' }]]);
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair).toMatchObject({ state: 'working' });
    expect(j.repair!.messages.at(-1)).toEqual({ from: 'user', text: 'retry it' });
    on('DELETE', `${P}/vms/atlas/repair/s1`, { status: 204 });
    on('GET', `${P}/vms/atlas/repair`, { json: session({ state: 'ended', outcome: 'stopped', lines: [] }) });
    be.stopRepair(j.id);
    await settle();
    expect(calls.filter(c => c.method === 'DELETE').map(c => c.path)).toEqual([`${P}/vms/atlas/repair/s1`]);
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ canRepair: true, repair: { state: 'stopped', outcome: 'Stopped' } });
  });

  it('shows "fixed" only when the session ended fixed, then on the job the engine retried', async () => {
    const { be, on, settle } = world();
    on('GET', `${P}/jobs`, { json: [failed()] });
    // The host started it itself before the page opened: it is read and followed.
    on('GET', `${P}/vms/atlas/repair`, { json: session({ automatic: true, lines: [line(1, 'text', 'Done, the cause is fixed.'), line(2, 'turn', null, 'fixed')] }) });
    await be.start();
    await settle();
    let j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair!.state).toBe('working');
    expect(j.canRepair).toBe(false);
    on('GET', `${P}/vms/atlas/repair`, { json: session({ state: 'ended', outcome: 'fixed', retryJobId: 'p2', lines: [line(3, 'end', null, 'fixed'), line(4, 'retry', 'The provision runs again (job p2).')] }) });
    be.onEvent('repair.ended', { seq: 10, type: 'repair.ended', engine: E1, data: { id: 's1', vm: 'atlas' } });
    await settle();
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair).toMatchObject({ state: 'done', outcome: 'Fixed; the job runs again' });
    expect(j.canRepair).toBe(false);
    on('GET', `${P}/jobs/p2`, { json: { ...job('p2', 'atlas', 'running'), created: '2026-11-13T11:00:00Z' } });
    on('GET', `${P}/jobs`, { json: [failed(), { ...job('p2', 'atlas', 'running'), created: '2026-11-13T11:00:00Z' }] });
    be.onEvent('job.started', { seq: 11, type: 'job.started', engine: E1, data: { id: 'p2', vm: 'atlas' } });
    await settle();
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ state: 'running', repair: { state: 'done' } });
  });

  it('keeps each session with its own VM and engine', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    on('GET', `${P}/jobs`, { json: [failed()] });
    on('GET', `/api/engines/${E2}/api/v1/jobs`, { json: [{ ...job('f1', 'harbor', 'failed'), result: {} }] });
    // An answer that names another VM is not shown under atlas.
    on('GET', `${P}/vms/atlas/repair`, { json: session({ vm: 'harbor', state: 'waiting', lines: [line(1, 'text', 'not yours')] }) });
    on('GET', `/api/engines/${E2}/api/v1/vms/harbor/repair`, { json: session({ vm: 'harbor', state: 'waiting', lines: [line(1, 'text', 'harbor asks')] }) });
    await be.start();
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ repair: null, canRepair: true });
    const h = activeJob(be.snapshot(), 'harbor')!;
    expect(h.repair!.messages).toEqual([{ from: 'agent', text: 'harbor asks' }]);
    on('POST', `/api/engines/${E2}/api/v1/vms/harbor/repair/s1/reply`, { status: 204 });
    be.replyRepair(h.id, 'go on');
    await settle();
    expect(posts(calls).filter(c => c.path.endsWith('/reply')).map(c => c.path)).toEqual([`/api/engines/${E2}/api/v1/vms/harbor/repair/s1/reply`]);
    // An event of the other engine reads that engine's VM only.
    const before = calls.length;
    be.onEvent('repair.output', { seq: 12, type: 'repair.output', engine: E2, data: { id: 's1', vm: 'harbor' } });
    await settle();
    expect(calls.slice(before).map(c => c.path)).toEqual([`/api/engines/${E2}/api/v1/vms/harbor/repair`]);
  });

  it('forgets sessions when the engine restarts, and keeps an ended session ended against a late start answer', async () => {
    const { be, on, settle } = world();
    on('GET', `${P}/jobs`, { json: [failed()] });
    on('GET', `${P}/vms/atlas/repair`, { json: session({ state: 'waiting', lines: [line(1, 'text', 'Which?')] }) });
    await be.start();
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.state).toBe('asking');
    on('GET', `${P}/vms/atlas/repair`, { status: 404, json: { title: 'none', status: 404, code: 'no-repair' } });
    be.onEvent('engine.reset', { seq: 20, type: 'engine.reset', engine: E1, data: null });
    await settle();
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ repair: null, canRepair: true });
    // An answer that still says working (a start's, sent before the end) arrives after the end was read.
    const ended = session({ state: 'ended', outcome: 'error', lines: [line(1, 'end', 'agent failed', 'error')] });
    on('GET', `${P}/vms/atlas/repair/s1`, { json: ended });
    be.onEvent('repair.ended', { seq: 21, type: 'repair.ended', engine: E1, data: { id: 's1', vm: 'atlas' } });
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')!.repair).toMatchObject({ state: 'gave-up', outcome: 'Not fixed' });
    on('POST', `${P}/vms/atlas/repair`, { status: 202, json: session({ state: 'working', lines: [] }) });
    be.startRepair(activeJob(be.snapshot(), 'atlas')!.id);
    await settle();
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.state).toBe('gave-up');
  });

  it('follows the session the host started meanwhile instead of starting a second', async () => {
    const { be, calls, on, settle } = world();
    on('GET', `${P}/jobs`, { json: [failed()] });
    on('POST', `${P}/vms/atlas/repair`, { status: 409, json: { title: 'running', status: 409, code: 'repair-running', sessionId: 's9' } });
    on('GET', `${P}/vms/atlas/repair/s9`, { json: session({ id: 's9', lines: [line(1, 'text', 'Looking')] }) });
    await be.start();
    await settle();
    be.startRepair(activeJob(be.snapshot(), 'atlas')!.id);
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual([`${P}/vms/atlas/repair`]);
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.messages).toEqual([{ from: 'agent', text: 'Looking' }]);
    expect(be.snapshot().notice).toBeNull();
  });

  it('switches repair.auto on a host the user administers, and reads it from the host configuration', async () => {
    const { be, calls, on, settle } = world({ engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }, { id: E2, name: 'lab', url: 'https://lab.test:8443' }] });
    on('GET', `${P}/host/config`, { json: { repair: { auto: true, source: 'default', updatedAt: null } } });
    on('PUT', `${P}/host/config`, { status: 200, json: {} });
    await be.start();
    expect(be.snapshot().hosts.find(h => h.id === E1)!.repairAuto).toBe(true);
    expect(be.snapshot().hosts.find(h => h.id === E2)!.repairAuto).toBeUndefined();
    be.setRepairAuto(E2, false);
    be.setRepairAuto(E1, false);
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([[`${P}/host/config`, { repair: { auto: false } }]]);
  });
});

describe('ApiBackend Windows sign-in', () => {
  const windows = (w: ReturnType<typeof world>) => w.on('GET', '/api/device/features', { json: { platform: 'windows', notifications: true, microphone: true, share: true, startWithOs: true, shell: true } });

  it('adds a remote host with Windows sign-in only where the device runs Windows', async () => {
    const w = world();
    windows(w);
    w.on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'P2', status: 'ok', known: null } });
    w.on('POST', '/api/engines', { json: { id: E2, name: 'lab', url: 'https://lab.test:8443', pin: 'P2', engineId: null, enrolled: true, signedIn: true, store: false } });
    w.on('GET', `/api/engines/${E2}/api/v1/vm-defaults`, { json: { recommendedRamGb: 8, recommendedDiskGb: 64 } });
    w.on('POST', `/api/engines/${E2}/api/v1/vms`, { status: 202, json: { jobId: 'c1' } });
    await w.be.start();
    expect(w.be.snapshot().na).not.toContain('windows-sign-in');
    expect(w.be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'windows', token: 'ignored', pin: 'P2' })).toBe('quill');
    await w.settle();
    expect(posts(w.calls).find(c => c.path === '/api/engines')!.body).toEqual({ address: 'https://lab.test:8443', pin: 'P2', userToken: null, windowsSignIn: true });
  });

  it('never sends Windows sign-in from a device that is not Windows, in a create or a first run', async () => {
    const w = world({ registered: false, engines: [] });
    w.on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'P2', status: 'ok', known: null } });
    await w.be.start();
    expect(w.be.snapshot().na).toContain('windows-sign-in');
    w.be.firstRunStart({ how: 'setup', address: 'https://lab.test:8443', deviceName: 'laptop', signIn: 'windows', pin: 'P2' });
    await w.settle();
    expect(posts(w.calls)).toEqual([]);
    expect(w.be.snapshot().firstRun?.problem).toBe('Windows sign-in works on Windows only');
    expect(w.be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://lab.test:8443', signIn: 'windows', pin: 'P2' })).toBeNull();
    await w.settle();
    expect(posts(w.calls)).toEqual([]);
  });

  it('sets up the first device with Windows sign-in on Windows', async () => {
    const w = world({ registered: false, engines: [] });
    windows(w);
    w.on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'P2', status: 'ok', known: null } });
    w.on('POST', '/api/engines', { json: { id: E2, name: 'lab', url: 'https://lab.test:8443', pin: 'P2', engineId: null, enrolled: false, signedIn: true, store: false } });
    w.on('POST', '/api/device/setup', { json: { recoveryCode: 'ABCD-EFGH', issue: 1 } });
    await w.be.start();
    w.be.firstRunStart({ how: 'setup', address: 'https://lab.test:8443', deviceName: 'laptop', signIn: 'windows', token: 'not sent', pin: 'P2' });
    await w.settle();
    expect(posts(w.calls).map(c => [c.path, c.body])).toEqual([
      ['/api/engines/probe', { address: 'https://lab.test:8443' }],
      ['/api/engines', { address: 'https://lab.test:8443', pin: 'P2', userToken: null, windowsSignIn: true }],
      ['/api/device/setup', { engine: E2, deviceName: 'laptop' }],
    ]);
  });
});

describe('ApiBackend one-time upgrade', () => {
  type Steps = Record<'engine' | 'recovery' | 'close' | 'convert', string>;
  const view = (st: Partial<Steps>, o: Record<string, unknown> = {}) => ({
    legacy: true, stage: 'new',
    steps: (['engine', 'recovery', 'close', 'convert'] as const).map(id => ({ id, state: st[id] ?? 'todo', message: null })),
    found: { installFolder: 'C:\\old', otherInstallFolders: [], localVms: ['atlas'], remoteVms: ['harbor'], hosts: [], profiles: ['webshop'], unmergedChanges: 0, vault: true, backupKeys: 1, upstream: false, channel: 'stable' },
    storeEngine: E1, localEngine: E1, items: [], notes: [], oldWriters: [], running: false, error: null, oldHosts: [], ...o,
  });

  it('shows nothing of it where the agent has no upgrade or the install is converted', async () => {
    const a = world();
    await a.be.start();
    expect(a.be.snapshot().upgrade).toBeNull();
    const b = world();
    b.on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done', convert: 'done' }, { stage: 'converted' }) });
    await b.be.start();
    expect(b.be.snapshot().upgrade).toBeNull();
    expect(b.be.snapshot().vms.length).toBeGreaterThan(0);
  });

  it('walks the agent\'s steps in their order; the code leaves the state when kept; the convert waits for the old programs', async () => {
    const { be, calls, on, settle } = world({ registered: false });
    on('GET', '/api/upgrade', { json: view({}) });
    await be.start();
    // The dialog stands in for the first run of an unregistered device.
    expect(be.snapshot().firstRun).toBeNull();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'engine', running: false, error: null, adminPrompt: true, found: { vms: ['atlas', 'harbor'], profiles: 1, vault: true } });
    on('POST', '/api/upgrade/engine', () => {
      on('GET', '/api/upgrade', { json: view({ engine: 'done' }) });
      return { status: 202, json: view({ engine: 'running' }, { running: true }) };
    });
    be.upgradeNext();
    await settle();
    expect(be.snapshot().upgrade?.step).toBe('recovery');
    // No name: nothing is sent.
    be.upgradeNext({ deviceName: ' ' });
    await settle();
    expect(be.snapshot().upgrade?.error).toBe('Name this device');
    expect(posts(calls).map(c => c.path)).toEqual(['/api/upgrade/engine']);
    on('POST', '/api/device/setup', () => {
      on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true, recoveryUnconfirmed: true }, engines: [], pairing: null } });
      on('GET', '/api/upgrade', { json: view({ engine: 'done' }) });
      return { json: { recoveryCode: 'ABCD-EFGH-JKMN', issue: 1 } };
    });
    on('POST', '/api/device/recovery-code/kept', () => {
      on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'todo' }, { oldWriters: ['VS Code'] }) });
      return { status: 204 };
    });
    be.upgradeNext({ deviceName: 'laptop' });
    await settle();
    expect(posts(calls).at(-1)).toMatchObject({ path: '/api/device/setup', body: { engine: E1, deviceName: 'laptop' } });
    // The code stays on screen until the user says it is kept; an event issues no new one.
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: ['ABCD', 'EFGH', 'JKMN'] });
    be.onEvent('upgrade', { seq: 1, type: 'upgrade', engine: null, data: null });
    await settle();
    expect(be.snapshot().upgrade?.step).toBe('recovery');
    be.upgradeNext();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'close', code: null, writers: ['VS Code'] });
    expect(JSON.stringify(be.snapshot())).not.toContain('EFGH');
    // At the close step nothing starts the conversion.
    be.upgradeNext();
    await settle();
    expect(posts(calls).some(c => c.path === '/api/upgrade/convert')).toBe(false);
    // VS Code closed, then opened again before Move over: the agent's list decides, nothing is sent.
    on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done' }) });
    be.onEvent('upgrade', { seq: 2, type: 'upgrade', engine: null, data: null });
    await settle();
    expect(be.snapshot().upgrade?.step).toBe('convert');
    on('GET', '/api/upgrade/writers', { json: { open: ['VS Code'] } });
    be.upgradeNext();
    await settle();
    expect(posts(calls).some(c => c.path === '/api/upgrade/convert')).toBe(false);
    expect(be.snapshot().upgrade?.error).toBe('Close these first: VS Code');
    on('GET', '/api/upgrade/writers', { json: { open: [] } });
    on('POST', '/api/upgrade/convert', () => {
      on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done', convert: 'running' }, { running: true, stage: 'converting' }) });
      return { status: 202, json: view({ convert: 'running' }, { running: true }) };
    });
    be.upgradeNext();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'convert', running: true, error: null });
    be.onEvent('upgrade', { seq: 3, type: 'upgrade', engine: null, data: { id: 'profiles', label: 'Projects', state: 'done', message: '1 copied' } });
    await settle();
    expect(be.snapshot().upgrade?.items).toEqual([{ name: 'Projects', state: 'done', count: '1 copied', pct: 0 }]);
    // Converted: the normal screens load.
    on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done', convert: 'done' }, { stage: 'converted' }) });
    on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true }, engines: [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'AB', engineId: 'x', enrolled: true, signedIn: true, store: true }], pairing: null } });
    be.onEvent('upgrade', { seq: 4, type: 'upgrade', engine: null, data: null });
    await settle();
    await settle();
    expect(be.snapshot().upgrade).toBeNull();
    expect(be.snapshot().vms.map(v => v.name)).toContain('atlas');
    expect(posts(calls).map(c => c.path)).toEqual(['/api/upgrade/engine', '/api/device/setup', '/api/device/recovery-code/kept', '/api/upgrade/convert']);
    expect(calls.every(c => !c.query.includes('ABCD'))).toBe(true);
  });

  it('after a reload before the code was confirmed, shows a newly issued code; the conversion waits for the confirm', async () => {
    const { be, calls, on, settle } = world({ unconfirmed: true });
    on('GET', '/api/upgrade', { json: view({ engine: 'done' }) });
    on('POST', '/api/device/recovery-code/again', { json: { recoveryCode: 'WXYZ-QRST', issue: 2 } });
    await be.start();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: ['WXYZ', 'QRST'] });
    expect(posts(calls).map(c => c.path)).toEqual(['/api/device/recovery-code/again']);
    be.onEvent('upgrade', { seq: 1, type: 'upgrade', engine: null, data: null });
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/device/recovery-code/again']);
    // The confirm fails: the code and the step stay, with the error.
    on('POST', '/api/device/recovery-code/kept', { status: 500, json: { title: 'The agent could not save it' } });
    be.upgradeNext();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: ['WXYZ', 'QRST'], error: 'The agent could not save it' });
    on('POST', '/api/device/recovery-code/kept', () => {
      on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done' }, { oldHosts: ['old.example'] }) });
      return { status: 204 };
    });
    be.upgradeNext();
    await settle();
    // The shared host still on the old stack is named before the conversion.
    expect(be.snapshot().upgrade).toMatchObject({ step: 'convert', code: null, oldHosts: ['old.example'] });
    expect(JSON.stringify(be.snapshot())).not.toContain('QRST');
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([['/api/device/recovery-code/again', undefined], ['/api/device/recovery-code/kept', { issue: 2 }], ['/api/device/recovery-code/kept', { issue: 2 }]]);
  });

  it('shows the current code when another window replaced the one on screen, and never confirms the replaced one', async () => {
    const { be, calls, on, settle } = world({ unconfirmed: true });
    on('GET', '/api/upgrade', { json: view({ engine: 'done' }) });
    on('POST', '/api/device/recovery-code/again', { json: { recoveryCode: 'WXYZ-QRST', issue: 2 } });
    await be.start();
    await settle();
    on('POST', '/api/device/recovery-code/kept', { status: 409, json: { title: 'A newer recovery code was issued since; this one no longer works.', code: 'code-replaced' } });
    on('POST', '/api/device/recovery-code/again', { json: { recoveryCode: 'MNPQ-RSTV', issue: 4 } });
    be.upgradeNext();
    await settle();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: ['MNPQ', 'RSTV'] });
    on('POST', '/api/device/recovery-code/kept', () => {
      on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done' }) });
      return { status: 204 };
    });
    be.upgradeNext();
    await settle();
    expect(posts(calls).filter(c => c.path.endsWith('/kept')).map(c => c.body)).toEqual([{ issue: 2 }, { issue: 4 }]);
    expect(be.snapshot().upgrade).toMatchObject({ step: 'close', code: null });
  });

  it('issues no code at the recovery step before the setup', async () => {
    const { be, calls, on, settle } = world({ registered: false });
    on('GET', '/api/upgrade', { json: view({ engine: 'done' }) });
    await be.start();
    await settle();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: null });
    expect(posts(calls)).toEqual([]);
  });

  it('shows the conversion\'s notes once after it, with one button on to the normal screens', async () => {
    const { be, calls, on, settle } = world();
    const done = { engine: 'done', recovery: 'done', close: 'done', convert: 'done' };
    on('GET', '/api/upgrade', { json: view(done, { stage: 'converted', notes: ['The VM far was not reached; it is moved on the next run.'], oldHosts: ['old.example'] }) });
    await be.start();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'done', notes: ['The VM far was not reached; it is moved on the next run.'], oldHosts: [] });
    expect(calls.some(c => c.path.startsWith('/api/engines/'))).toBe(false);
    on('POST', '/api/upgrade/notes/read', () => {
      on('GET', '/api/upgrade', { json: view(done, { stage: 'converted' }) });
      return { json: view(done, { stage: 'converted' }) };
    });
    be.upgradeNext();
    await settle();
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/upgrade/notes/read']);
    expect(be.snapshot().upgrade).toBeNull();
    expect(be.snapshot().vms.map(v => v.name)).toContain('atlas');
  });

  it('resumes at the step the agent reports after a reload, and runs nothing while a step runs', async () => {
    const { be, calls, on } = world();
    on('GET', '/api/upgrade', { json: view({ engine: 'done', recovery: 'done', close: 'done', convert: 'running' }, { running: true, stage: 'converting' }) });
    await be.start();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'convert', running: true, code: null });
    be.upgradeNext();
    expect(posts(calls)).toEqual([]);
    // Engines are not read while the dialog holds the screen.
    expect(calls.some(c => c.path.startsWith('/api/engines/'))).toBe(false);
  });

  it('shows a failed step\'s error in place and retries that step', async () => {
    const { be, calls, on, settle } = world({ registered: false, engines: [] });
    on('GET', '/api/upgrade', { json: view({}, { error: 'The engine installer ended with exit code 1; see its window for why, then retry.' }) });
    on('POST', '/api/upgrade/engine', { status: 202, json: view({ engine: 'running' }, { running: true }) });
    await be.start();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'engine', error: 'The engine installer ended with exit code 1; see its window for why, then retry.' });
    be.upgradeNext({ deviceName: 'laptop' });
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual(['/api/upgrade/engine']);
  });
});

describe('ApiBackend engine on this PC', () => {
  const E9 = 'cccccccccccc';
  const host = (w: ReturnType<typeof world>, engineHost = true) =>
    w.on('GET', '/api/device/features', { json: { platform: 'windows', notifications: true, microphone: true, share: true, startWithOs: true, shell: true, engineHost } });
  const setupView = (state: string, o: Record<string, unknown> = {}) => ({ json: { available: true, state, message: null, engine: null, ...o } });
  const pc = { id: E9, name: 'This PC', url: 'https://127.0.0.1:7462', pin: 'P9', engineId: null, enrolled: false, signedIn: true, store: false };

  it('offers This PC on a device that can host an engine and has none; it sets the engine up, then the device against exactly it', async () => {
    const w = world({ registered: false, engines: [] });
    host(w);
    w.on('GET', '/api/engines/local', { status: 204 });
    w.on('GET', '/api/engines/local/setup', setupView('idle'));
    w.on('POST', '/api/engines/local/setup', () => {
      w.on('GET', '/api/engines/local/setup', setupView('running'));
      return { status: 202, ...setupView('running') };
    });
    w.on('POST', '/api/device/setup', { json: { recoveryCode: 'ABCD-EFGH', issue: 1 } });
    await w.be.start();
    expect(w.be.snapshot().firstRun).toMatchObject({ step: 'start', thisPc: 'setup', restart: null, localAddress: null });
    w.be.firstRunStart({ how: 'setup', where: 'this-pc', address: 'ignored', deviceName: ' laptop ', token: 'ignored' });
    await w.settle();
    // While it runs nothing else is sent; its event ends the wait.
    expect(posts(w.calls).map(c => c.path)).toEqual(['/api/engines/local/setup']);
    expect(w.be.snapshot().firstRun?.busy).toBe(true);
    w.on('GET', '/api/state', { json: { device: { id: 'd1', name: '', account: null, registered: false, storeEngine: null, protection: 'file', protectionWeak: true }, engines: [pc], pairing: null } });
    w.on('GET', '/api/engines/local/setup', setupView('done', { engine: E9 }));
    w.be.onEvent('engine-setup', { seq: 1, type: 'engine-setup', engine: null, data: null });
    await w.settle();
    await w.settle();
    expect(posts(w.calls).map(c => [c.path, c.body])).toEqual([['/api/engines/local/setup', undefined], ['/api/device/setup', { engine: E9, deviceName: 'laptop' }]]);
    expect(w.be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['ABCD', 'EFGH'], busy: false });
    expect(posts(w.calls).some(c => c.path === '/api/engines' || c.path === '/api/engines/probe')).toBe(false);
  });

  it('when Windows must restart: says so, restarts only on the click, and later leaves the step to continue', async () => {
    const w = world({ registered: false, engines: [] });
    host(w);
    w.on('GET', '/api/engines/local', { status: 204 });
    w.on('GET', '/api/engines/local/setup', setupView('idle'));
    w.on('POST', '/api/engines/local/setup', () => {
      w.on('GET', '/api/engines/local/setup', setupView('restart', { message: 'Windows must restart to turn on Hyper-V.' }));
      return { status: 202, ...setupView('running') };
    });
    w.on('POST', '/api/device/restart', { status: 202 });
    await w.be.start();
    w.be.firstRunStart({ how: 'setup', where: 'this-pc', address: '', deviceName: 'laptop' });
    await w.settle();
    expect(w.be.snapshot().firstRun).toMatchObject({ step: 'start', busy: false, problem: null, restart: 'Windows must restart to turn on Hyper-V.' });
    expect(posts(w.calls).map(c => c.path)).toEqual(['/api/engines/local/setup']);
    w.be.restartForEngine(false);
    expect(w.be.snapshot().firstRun?.restart).toBeNull();
    expect(posts(w.calls).map(c => c.path)).toEqual(['/api/engines/local/setup']);
    // Running it again (after the restart, or not) asks again; Restart now is the agent's restart.
    w.be.firstRunStart({ how: 'setup', where: 'this-pc', address: '', deviceName: 'laptop' });
    await w.settle();
    expect(w.be.snapshot().firstRun?.restart).toBe('Windows must restart to turn on Hyper-V.');
    w.be.restartForEngine(true);
    await w.settle();
    expect(posts(w.calls).map(c => c.path)).toEqual(['/api/engines/local/setup', '/api/engines/local/setup', '/api/device/restart']);
  });

  it('shows the installer\'s reason in place with Retry when the setup failed', async () => {
    const w = world({ registered: false, engines: [] });
    host(w);
    w.on('GET', '/api/engines/local', { status: 204 });
    w.on('GET', '/api/engines/local/setup', setupView('failed', { message: 'Hyper-V could not be enabled on this Windows Home edition.' }));
    w.on('POST', '/api/engines/local/setup', { status: 202, ...setupView('running') });
    await w.be.start();
    w.be.firstRunStart({ how: 'setup', where: 'this-pc', address: '', deviceName: 'laptop' });
    await w.settle();
    expect(w.be.snapshot().firstRun).toMatchObject({ step: 'start', busy: false, restart: null, problem: 'Hyper-V could not be enabled on this Windows Home edition.' });
    expect(posts(w.calls).some(c => c.path === '/api/device/setup')).toBe(false);
  });

  it('uses the engine this agent set up already, without a second install', async () => {
    const w = world({ registered: false, engines: [] });
    host(w);
    w.on('GET', '/api/engines/local', { json: { url: 'https://127.0.0.1:7462', pin: 'P9', status: 'ok', known: E9 } });
    w.on('POST', '/api/device/setup', { json: { recoveryCode: 'ABCD-EFGH', issue: 1 } });
    await w.be.start();
    expect(w.be.snapshot().firstRun).toMatchObject({ thisPc: 'known', localAddress: 'https://127.0.0.1:7462' });
    w.be.firstRunStart({ how: 'setup', where: 'this-pc', address: '', deviceName: 'laptop' });
    await w.settle();
    expect(posts(w.calls).map(c => [c.path, c.body])).toEqual([['/api/device/setup', { engine: E9, deviceName: 'laptop' }]]);
  });

  it('keeps today\'s fields where another engine runs on this PC, or where the device cannot host one', async () => {
    const a = world({ registered: false, engines: [] });
    host(a);
    a.on('GET', '/api/engines/local', { json: { url: 'https://127.0.0.1:7462', pin: 'P1', status: 'ok', known: null } });
    await a.be.start();
    expect(a.be.snapshot().firstRun).toMatchObject({ thisPc: null, localAddress: 'https://127.0.0.1:7462' });
    const b = world({ registered: false, engines: [] });
    host(b, false);
    b.on('GET', '/api/engines/local', { status: 204 });
    await b.be.start();
    expect(b.be.snapshot().firstRun).toMatchObject({ thisPc: null });
    const c = world({ registered: false, engines: [] });
    c.on('GET', '/api/engines/local', { status: 204 });
    await c.be.start();
    expect(c.be.snapshot().firstRun?.thisPc).toBeNull();
    // A this-pc request from such a device sends nothing.
    c.be.firstRunStart({ how: 'setup', where: 'this-pc', address: '', deviceName: 'laptop' });
    await c.settle();
    expect(posts(c.calls)).toEqual([]);
    expect(c.be.snapshot().firstRun?.problem).toBe('This device cannot run an engine');
  });

  it('shows the upgrade\'s restart in its engine step, with Restart now and later', async () => {
    const w = world();
    const steps = (engine: string, message: string | null = null) => ({ json: {
      legacy: true, stage: 'new', steps: [{ id: 'engine', state: engine, message }, ...(['recovery', 'close', 'convert'] as const).map(id => ({ id, state: 'todo', message: null }))],
      found: { installFolder: 'C:\\old', otherInstallFolders: [], localVms: ['atlas'], remoteVms: [], hosts: [], profiles: [], unmergedChanges: 0, vault: false, backupKeys: 0, upstream: false, channel: 'stable' },
      storeEngine: null, localEngine: null, items: [], notes: [], oldWriters: [], running: false, error: null, oldHosts: [] } });
    w.on('GET', '/api/upgrade', steps('todo'));
    w.on('POST', '/api/upgrade/engine', () => {
      w.on('GET', '/api/upgrade', steps('restart', 'Windows must restart to turn on Hyper-V.'));
      return { status: 202, json: {} };
    });
    w.on('POST', '/api/device/restart', { status: 202 });
    await w.be.start();
    expect(w.be.snapshot().upgrade).toMatchObject({ step: 'engine', restart: null });
    w.be.upgradeNext();
    await w.settle();
    expect(w.be.snapshot().upgrade).toMatchObject({ step: 'engine', error: null, restart: 'Windows must restart to turn on Hyper-V.' });
    w.be.restartForEngine(false);
    expect(w.be.snapshot().upgrade?.restart).toBeNull();
    w.be.upgradeNext();
    await w.settle();
    expect(w.be.snapshot().upgrade?.restart).toBe('Windows must restart to turn on Hyper-V.');
    w.be.restartForEngine(true);
    await w.settle();
    expect(posts(w.calls).map(c => c.path)).toEqual(['/api/upgrade/engine', '/api/upgrade/engine', '/api/device/restart']);
  });
});
