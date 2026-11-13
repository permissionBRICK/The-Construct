import { describe, expect, it } from 'vitest';
import { ApiBackend } from '../../src/backend/api/backend';
import { SseParser } from '../../src/backend/api/events';
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

function world(opts: { registered?: boolean; engines?: { id: string; name: string; url: string }[] } = {}) {
  const calls: Call[] = [];
  const routes = new Map<string, Handler>();
  const on = (method: string, path: string, h: Handler | Reply) => routes.set(`${method} ${path}`, typeof h === 'function' ? h : () => h);
  const engines = (opts.engines ?? [{ id: E1, name: 'orchard', url: 'https://orchard.test:8443' }]).map(e => ({ ...e, pin: 'AB', engineId: 'x', enrolled: true, signedIn: false, store: e.id === E1 }));
  on('GET', '/api/health', { json: { status: 'ok', version: '3.1.0', pid: 1, startedAt: '2026-11-13T00:00:00Z' } });
  on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: opts.registered ?? true, storeEngine: E1, protection: 'file', protectionWeak: true }, engines, pairing: null } });
  on('GET', '/api/devices', { json: [{ id: 'd1', name: 'laptop', me: true, revoked: false, added: '2026-11-01T00:00:00Z', acknowledgedBy: [] }, { id: 'd2', name: 'phone', me: false, revoked: false, added: '2026-11-02T00:00:00Z', acknowledgedBy: [] }] });
  for (const e of engines) {
    const p = `/api/engines/${e.id}/api/v1`;
    on('GET', `${p}/whoami`, { json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: e.id === E1 ? 'admin' : 'user', maxVms: 5, allowHostForwards: true } });
    on('GET', `${p}/health`, { json: { status: 'ok', schemaVersion: 1, schemaMinReadableBy: 0, apiFeatures: [], maintenance: null, commit: 'c'.repeat(40), packageVersion: '3.1.0' } });
    on('GET', `${p}/me/updates`, { json: { autoReprovision: false } });
    on('GET', `${p}/jobs`, { json: [] });
    on('GET', `${p}/vms`, { json: e.id === E1 ? [vmResponse('atlas', 'alex'), vmResponse('kai-box', 'kai')] : [vmResponse('harbor', 'alex')] });
    for (const vm of ['atlas', 'harbor']) {
      on('GET', `${p}/vms/${vm}/provision-status`, { json: { settingsChanged: vm === 'atlas', newerPayload: false, hardwareChanged: false, desiredHash: 'h', availableCommit: 'c'.repeat(40), applied: { commit: 'd'.repeat(40), payloadHash: null, desiredHash: 'g', channel: 'main', installedAt: '2026-11-01T10:00:00Z', reprovisionedAt: '2026-11-10T10:00:00Z', readAt: '2026-11-10T10:00:00Z', jobId: 'j0' }, pendingCpu: null, pendingRamGb: null } });
      on('GET', `${p}/vms/${vm}/update`, { json: { newerPayload: vm === 'harbor', availableCommit: 'c'.repeat(40), appliedCommit: 'd'.repeat(40), hostUpdate: null } });
      on('GET', `${p}/vms/${vm}/backups`, { json: { backups: [
        { id: 'b'.repeat(32), createdAt: '2026-11-12T03:00:00Z', trigger: 'scheduled', sizeBytes: 415e6, sha256: null, pinnedUntil: null, kind: 'host', pinned: false, protected: true, fileCount: 48230 },
        { id: 'c'.repeat(32), createdAt: '2026-11-13T03:00:00Z', trigger: 'manual', sizeBytes: 400e6, sha256: null, pinnedUntil: null, kind: 'host', pinned: false, protected: false, fileCount: null },
      ] } });
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
    expect(s.na).toContain('restore');
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
    on('POST', `/api/engines/${E2}/api/v1/vms/harbor/jobs`, body => (++n === 1 ? { status: 409, json: { title: 'desired-state-missing', status: 409, code: 'desired-state-missing' } } : (expect(body).toMatchObject({ kind: 'provision', desired: { projects: [], settings: {} } }), { status: 202, json: { jobId: 'j9', kind: 'provision', replayed: false } })));
    on('GET', `/api/engines/${E2}/api/v1/jobs/j9`, { json: job('j9', 'harbor', 'running') });
    await be.start();
    be.reprovision('harbor');
    await settle();
    const p = posts(calls);
    expect(p.map(c => c.path)).toEqual([`/api/engines/${E2}/api/v1/vms/harbor/jobs`, `/api/engines/${E2}/api/v1/vms/harbor/jobs`]);
    expect(p[0].body).toMatchObject({ kind: 'provision' });
    expect((p[0].body as { desired?: unknown }).desired).toBeUndefined();
    expect((p[0].body as { requestId: string }).requestId).not.toBe((p[1].body as { requestId: string }).requestId);
    const j = activeJob(be.snapshot(), 'harbor')!;
    expect(j).toMatchObject({ kind: 'reprovision', state: 'running' });
  });

  it('follows job progress and the end from the agent\'s events, reading the job again each time', async () => {
    const { be, on, settle } = world();
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
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
    be.restoreBackup('atlas', 'c'.repeat(32));
    expect(be.snapshot().notice?.text).toBe('Not available yet');
    be.takeBack('x');
    be.setProjectOn('atlas', 'webshop', true);
    be.saveSettings('atlas', { services: { smb: true } });
    await settle();
    expect(calls.length).toBe(before);
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
    be.updateEverything();
    await settle();
    expect(posts(calls).map(c => c.path)).toEqual([
      `/api/engines/${E2}/api/v1/vms/harbor/update`, `/api/engines/${E1}/api/v1/vms/atlas/update`, `/api/engines/${E2}/api/v1/me/vms/reprovision`,
    ]);
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
    on('POST', '/api/device/setup', { json: { recoveryCode: 'ABCD-EFGH-JKMN' } });
    await be.start();
    expect(be.snapshot().firstRun).toMatchObject({ step: 'start', localAddress: 'https://127.0.0.1:7462' });
    expect(be.snapshot().vms).toEqual([]);
    // The engine on this machine needs no confirmed fingerprint: the agent's own probe of it is taken.
    be.firstRunStart({ how: 'setup', address: 'https://127.0.0.1:7462', deviceName: 'laptop', token: 'user-token' });
    await settle();
    expect(posts(calls).map(c => [c.path, c.body])).toEqual([
      ['/api/engines', { address: 'https://127.0.0.1:7462', pin: 'P1', userToken: 'user-token' }],
      ['/api/device/setup', { engine: E1, deviceName: 'laptop' }],
    ]);
    expect(be.snapshot().firstRun).toMatchObject({ step: 'code', code: ['ABCD', 'EFGH', 'JKMN'], account: 'acct1' });
    on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true }, engines: [], pairing: null } });
    be.firstRunNext();
    expect(be.snapshot().firstRun).toBeNull();
    expect(JSON.stringify(be.snapshot())).not.toContain('EFGH');
  });

  it('keeps the first run until the recovery code was shown, when the agent reports the new identity first', async () => {
    const { be, on, settle } = world({ registered: false, engines: [] });
    on('POST', '/api/engines/probe', { json: { url: 'https://orchard.test:8443', pin: 'P', status: 'ok', known: null } });
    on('POST', '/api/engines', { json: { id: E1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'P', engineId: null, enrolled: false, signedIn: true, store: false } });
    on('POST', '/api/device/setup', () => {
      on('GET', '/api/state', { json: { device: { id: 'd1', name: 'laptop', account: 'acct1', registered: true, storeEngine: E1, protection: 'file', protectionWeak: true }, engines: [], pairing: null } });
      be.onEvent('agent', { seq: 1, type: 'agent', engine: null, data: null });
      return { json: { recoveryCode: 'ABCD-EFGH' } };
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

  it('creates a VM on a pasted host with its token, then provisions it', async () => {
    const { be, calls, on, settle } = world();
    on('POST', '/api/engines/probe', { json: { url: 'https://lab.test:8443', pin: 'P2', status: 'ok', known: null } });
    on('POST', '/api/engines', { json: { id: E2, name: 'lab', url: 'https://lab.test:8443', pin: 'P2', engineId: null, enrolled: true, signedIn: true, store: false } });
    on('GET', `/api/engines/${E2}/api/v1/vm-defaults`, { json: { hostLogicalCpus: 8, maximumCpus: 8, recommendedCpus: 8, maximumRamGb: 16, recommendedRamGb: 8 } });
    on('POST', `/api/engines/${E2}/api/v1/vms`, { status: 202, json: { jobId: 'c1', nested: false, nestedFromHostDefault: true, ignoredOptions: [] } });
    on('GET', `/api/engines/${E2}/api/v1/whoami`, { json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: 'user' } });
    on('GET', `/api/engines/${E2}/api/v1/vms`, { json: [vmResponse('quill', 'alex')] });
    on('GET', `/api/engines/${E2}/api/v1/jobs/c1`, { json: job('c1', 'quill', 'running', 'create-vm') });
    on('POST', `/api/engines/${E2}/api/v1/vms/quill/jobs`, { status: 202, json: { jobId: 'p1', kind: 'provision', replayed: false } });
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
      ['/api/engines', { address: 'https://lab.test:8443', pin: 'p2', userToken: 'tok' }],
      [`/api/engines/${E2}/api/v1/vms`, { name: 'quill', ramGb: 8, diskGb: 50 }],
      [`/api/engines/${E2}/api/v1/vms/quill/jobs`, expect.objectContaining({ kind: 'provision' })],
    ]);
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
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
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

  it('starts a reinstall with the fresh-image switch, shows its failed step, and retries the same reinstall', async () => {
    const { be, calls, on, settle } = world();
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/jobs`, { status: 202, json: { jobId: 'r1', kind: 'reinstall', replayed: false } });
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
    be.retryJob(j.id);
    await settle();
    const p = posts(calls);
    expect(p.length).toBe(2);
    expect(p[1].body).toMatchObject({ kind: 'reinstall', freshImage: true });
    expect((p[1].body as { requestId: string }).requestId).not.toBe((p[0].body as { requestId: string }).requestId);
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
    on('POST', `/api/engines/${E1}/api/v1/vms/atlas/jobs`, { status: 202, json: { jobId: 'j1', kind: 'provision', replayed: false } });
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
