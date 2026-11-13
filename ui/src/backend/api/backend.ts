import createClient, { type Client } from 'openapi-fetch';
import type { paths as AgentPaths } from '../generated/agent';
import type { paths as EnginePaths } from '../generated/engine';
import type {
  Backend, CreateVmRequest, Feature, FirstRunRequest, Host, HostUser, HostVm, Job, JobKind, Open, Prefs, Profile, SettingsPatch, State, Vm, WinKey,
} from '../types';
import { followEvents, type AgentEventData } from './events';
import {
  updateRunning, allowanceOf, codeGroups, hostVersion, jobKey, problemText, splitJobKey, toCert, toConfig, toDevice, toHostJob, toHostUser, toHostVm, toJob,
  toMedia, toStatus, toUpdates, toUsage, toVm, toWindows, vmKey,
  type AuditEntry, type EngineView, type Health, type HostUpdateStatus, type HttpsStatus, type JobResponse, type MediaItem, type TokenSummary,
  type UsageSummary, type UserDetail, type VmParts, type VmResponse, type WhoAmI,
} from './map';

/* The real backend: the device agent's local API and, through its proxy, every engine the user works with.
   The state is assembled from one slice per engine plus this device's own parts; an engine or a route that
   fails leaves its slice as it was, with the error on that host or VM, and never blanks the rest. */

type AgentClient = Client<AgentPaths>;
type EngineClient = Client<EnginePaths>;
type Loose = Record<string, unknown>;

/** What this backend cannot serve yet; the screens show these parts as not available. */
export const API_GAPS: Feature[] = [
  'vault', 'repair-chat', 'forwards', 'sharing', 'projects', 'agents', 'open', 'console', 'vm-password', 'vm-services', 'vm-disk', 'vm-boot',
  'reinstall', 'restore', 'device-update', 'channel', 'os-prefs', 'windows-sign-in', 'export-code', 'code-reveal', 'vm-token', 'host-retry',
];

const NA = 'Not available yet';
const DEFAULT_DISK_GB = 50;
const PREFS_KEY = 'construct.prefs';
const DISMISSED_KEY = 'construct.dismissed';

interface AdminData {
  users: HostUser[];
  status: Loose | null;
  updates: HostUpdateStatus | null;
  media: MediaItem[];
  audit: AuditEntry[];
  usage: Host['usage'];
  config: Host['config'];
  https: HttpsStatus | null;
  windows: ReturnType<typeof toWindows>;
}

interface Slice {
  view: EngineView;
  me: WhoAmI | null;
  health: Health | null;
  all: VmResponse[];
  mine: VmResponse[];
  parts: Map<string, VmParts>;
  jobs: JobResponse[];
  admin: AdminData | null;
  autoReprovision: boolean | null;
  usageWindow: Host['usage']['window'];
  problem: string | null;
}

class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly code: string | null, readonly body: Loose | null = null) { super(message); }
}

/** The data of an openapi-fetch call, or an ApiError with the problem's text. */
async function ok<T>(p: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const r = await p;
  if (!r.response.ok) {
    const e = r.error as Loose | undefined;
    throw new ApiError(problemText(e, `${r.response.status} ${r.response.statusText}`.trim()), r.response.status, typeof e?.code === 'string' ? e.code : null, e ?? null);
  }
  return r.data as T;
}
const errText = (e: unknown) => (e instanceof Error ? e.message : problemText(e));

const store = {
  get<T>(k: string, d: T): T { try { const v = localStorage.getItem(k); return v ? (JSON.parse(v) as T) : d; } catch { return d; } },
  set(k: string, v: unknown) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* storage off */ } },
};

const LOOPBACK = /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?\/?$/i;

export interface ApiOptions {
  /** The agent's origin, e.g. `http://127.0.0.1:5123`. */
  base: string;
  /** The agent's token from the URL fragment; sent only as a header. */
  token: string;
  fetch?: typeof fetch;
  /** Milliseconds between refreshes of the engines while nothing runs; 0 turns polling off (tests). */
  pollMs?: number;
  /** false: do not follow `/api/events` (tests drive `onEvent`). */
  events?: boolean;
}

export class ApiBackend implements Backend {
  private s: State;
  private subs = new Set<() => void>();
  private agent: AgentClient;
  private clients = new Map<string, EngineClient>();
  private slices = new Map<string, Slice>();
  private seq = 1;
  private dismissed = new Set<string>(store.get<string[]>(DISMISSED_KEY, []));
  /** Jobs this UI started, with the kind it shows them as and the version an update goes from and to. */
  private started = new Map<string, { kind: JobKind; from?: string; to?: string }>();
  /** create-vm jobs after which the VM is provisioned. */
  private provisionAfter = new Set<string>();
  private transit = new Map<string, { power: Vm['power']; until: number }>();
  private approving: { id: string; publicKey: string; name: string } | null = null;
  private refreshing = new Map<string, Promise<void>>();
  private again = new Set<string>();
  private stopEvents: (() => void) | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private fetchImpl: typeof fetch;

  constructor(private opts: ApiOptions) {
    this.fetchImpl = opts.fetch ?? ((...a: Parameters<typeof fetch>) => fetch(...a));
    const headers = { Authorization: `Bearer ${opts.token}` };
    this.agent = createClient<AgentPaths>({ baseUrl: opts.base, headers, fetch: this.fetchImpl });
    const p = store.get<Partial<Prefs>>(PREFS_KEY, {});
    this.s = {
      device: { name: '' }, version: '', release: null, vms: [], hosts: [], projects: [], sync: { when: '–', remote: '', rev: '' }, jobs: [],
      vaultRequests: [], secrets: [], leases: [], devices: [], history: [], pairing: null, revocation: null, handout: null,
      prefs: { channel: 'stable', theme: p.theme ?? 'auto', scheme: p.scheme ?? 'green', autoUpdate: false, startWithOs: false, notifications: false },
      upgrade: null, notice: null, na: API_GAPS, firstRun: null,
    };
  }

  /** Loads everything and starts following the agent's events. */
  async start(): Promise<void> {
    if (this.opts.events !== false) {
      this.stopEvents = followEvents({
        url: since => `${this.opts.base}/api/events?since=${since}`,
        headers: { Authorization: `Bearer ${this.opts.token}` },
        fetch: this.fetchImpl,
        on: (type, ev) => this.onEvent(type, ev),
        onDenied: () => this.note('This page lost its connection to Construct; open it again from the tray'),
      });
    }
    await this.load();
    this.schedule();
  }

  dispose() {
    this.stopEvents?.();
    if (this.timer) clearTimeout(this.timer);
    this.subs.clear();
  }

  /* --- plumbing --- */

  snapshot(): State { return this.s; }
  subscribe(fn: () => void) { this.subs.add(fn); return () => { this.subs.delete(fn); }; }

  private emit(patch: Partial<State>) {
    this.s = { ...this.s, ...patch };
    for (const f of this.subs) f();
  }

  private note(text: string) { this.emit({ notice: { id: this.seq++, text } }); }

  private engine(id: string): EngineClient {
    let c = this.clients.get(id);
    if (!c) {
      c = createClient<EnginePaths>({ baseUrl: `${this.opts.base}/api/engines/${encodeURIComponent(id)}`, headers: { Authorization: `Bearer ${this.opts.token}` }, fetch: this.fetchImpl });
      this.clients.set(id, c);
    }
    return c;
  }

  private schedule() {
    const ms = this.opts.pollMs ?? 30000;
    if (!ms) return;
    if (this.timer) clearTimeout(this.timer);
    // While a job runs that the agent does not follow (one started by a chain), look more often.
    const busy = this.s.jobs.some(j => j.state === 'running');
    this.timer = setTimeout(() => { void this.refreshAll().finally(() => this.schedule()); }, busy ? Math.min(ms, 5000) : ms);
  }

  /** Runs an action; its error becomes the notice. */
  private async run(what: () => Promise<unknown>, done?: string, after?: string): Promise<boolean> {
    try {
      await what();
      if (done) this.note(done);
      return true;
    } catch (e) {
      this.note(errText(e));
      return false;
    } finally {
      if (after) void this.refreshEngine(after);
    }
  }

  private vmRef(vmId: string): { engine: string; name: string; vm: Vm } | null {
    const vm = this.s.vms.find(v => v.id === vmId);
    return vm ? { engine: vm.hostId, name: vm.name, vm } : null;
  }

  /* --- loading --- */

  private async load() {
    try {
      const [health, st] = await Promise.all([ok(this.agent.GET('/api/health')), ok(this.agent.GET('/api/state'))]);
      this.emit({ version: health.version.split('+')[0], device: { name: st.device.name ?? '' } });
      if (!st.device.registered) {
        if (!this.s.firstRun) {
          let local: string | null = null;
          try {
            const r = await this.agent.GET('/api/engines/local');
            local = r.response.status === 200 && r.data ? r.data.url : null;
          } catch { /* no engine on this machine */ }
          this.emit({ firstRun: { step: st.pairing && st.pairing.state === 'waiting' ? 'pairing' : 'start', localAddress: local, busy: false, problem: null, code: null,
            pairing: st.pairing && st.pairing.state === 'waiting' ? { code: st.pairing.pairingId, words: st.pairing.words, expiresAt: st.pairing.expiresAt } : null } });
        }
        return;
      }
      // The agent reports the new identity before setup answers with the code: the first run stays while a
      // step runs and while the code is on screen.
      if (this.s.firstRun && !this.s.firstRun.busy && this.s.firstRun.step !== 'code') this.emit({ firstRun: null });
      for (const id of [...this.slices.keys()]) if (!st.engines.some(e => e.id === id)) this.slices.delete(id);
      for (const e of st.engines) {
        const sl = this.slices.get(e.id);
        if (sl) sl.view = e;
        else this.slices.set(e.id, { view: e, me: null, health: null, all: [], mine: [], parts: new Map(), jobs: [], admin: null, autoReprovision: null, usageWindow: 'month', problem: null });
      }
      this.compose();
      await Promise.all([this.loadDevices(), ...st.engines.map(e => this.refreshEngine(e.id))]);
    } catch (e) {
      this.note(errText(e));
    }
  }

  private async loadDevices() {
    try {
      const list = await ok(this.agent.GET('/api/devices'));
      this.emit({ devices: list.map(toDevice) });
    } catch { /* the devices table keeps what it had */ }
  }

  private async refreshAll() {
    await Promise.all([this.loadDevices(), ...[...this.slices.keys()].map(id => this.refreshEngine(id))]);
  }

  /** Reads one engine; a second call while one runs makes it read once more afterwards. */
  refreshEngine(id: string): Promise<void> {
    const running = this.refreshing.get(id);
    if (running) { this.again.add(id); return running; }
    const p = this.readEngine(id).finally(() => {
      this.refreshing.delete(id);
      if (this.again.delete(id)) void this.refreshEngine(id);
    });
    this.refreshing.set(id, p);
    return p;
  }

  private async readEngine(id: string) {
    const sl = this.slices.get(id);
    if (!sl) return;
    const c = this.engine(id);
    try {
      sl.me = await ok(c.GET('/api/v1/whoami'));
    } catch (e) {
      sl.problem = errText(e);
      this.compose();
      return;
    }
    const [health, vms, jobs, upd] = await Promise.allSettled([
      ok(c.GET('/api/v1/health')), ok(c.GET('/api/v1/vms', { params: { query: { kind: 'all' } as never } })), ok(c.GET('/api/v1/jobs')), ok(c.GET('/api/v1/me/updates')),
    ]);
    sl.problem = null;
    if (health.status === 'fulfilled') sl.health = health.value;
    if (vms.status === 'fulfilled') sl.all = vms.value; else sl.problem = errText(vms.reason);
    if (jobs.status === 'fulfilled') sl.jobs = jobs.value;
    if (upd.status === 'fulfilled') sl.autoReprovision = upd.value.autoReprovision;
    const me = sl.me.name.toLowerCase();
    sl.mine = sl.all.filter(v => v.kind !== 'child' && v.owner.toLowerCase() === me && !v.deleting);
    await Promise.all(sl.mine.map(async v => {
      const path = { params: { path: { name: v.name } } };
      const [status, update, backups, settings] = await Promise.allSettled([
        ok(c.GET('/api/v1/vms/{name}/provision-status', path)), ok(c.GET('/api/v1/vms/{name}/update', path)),
        ok(c.GET('/api/v1/vms/{name}/backups', path)), ok(c.GET('/api/v1/vms/{name}/backups/settings', path)),
      ]);
      const old = sl.parts.get(v.name) ?? {};
      const problems: Vm['problems'] = {};
      if (status.status === 'rejected') problems.status = errText(status.reason);
      if (backups.status === 'rejected') problems.backups = errText(backups.reason);
      sl.parts.set(v.name, {
        status: status.status === 'fulfilled' ? status.value : old.status,
        update: update.status === 'fulfilled' ? update.value : old.update,
        backups: backups.status === 'fulfilled' ? backups.value.backups : old.backups,
        backupSettings: settings.status === 'fulfilled' ? settings.value : old.backupSettings,
        problems,
      });
    }));
    sl.admin = sl.me.role === 'admin' ? await this.readAdmin(c, sl) : null;
    this.compose();
  }

  private async readAdmin(c: EngineClient, sl: Slice): Promise<AdminData> {
    const prev = sl.admin;
    const [users, status, updates, media, audit, usage, config, https, windows] = await Promise.allSettled([
      ok(c.GET('/api/v1/users')),
      ok(c.GET('/api/v1/host/status')) as Promise<unknown>,
      ok(c.GET('/api/v1/host/updates/status')),
      ok(c.GET('/api/v1/media')),
      ok(c.GET('/api/v1/audit', { params: { query: { limit: 100 } } })),
      ok(c.GET('/api/v1/host/usage', { params: { query: { window: sl.usageWindow } } })),
      ok(c.GET('/api/v1/host/config')) as Promise<unknown>,
      ok(c.GET('/api/v1/host/https')),
      ok(c.GET('/api/v1/host/windows')) as Promise<unknown>,
    ]);
    let hostUsers = prev?.users ?? [];
    if (users.status === 'fulfilled') {
      hostUsers = await Promise.all((users.value as UserDetail[]).map(async u => {
        let tokens: TokenSummary[] = [];
        try { tokens = await ok(c.GET('/api/v1/users/{name}/tokens', { params: { path: { name: u.name } } })); } catch { /* listed without tokens */ }
        return toHostUser(u, tokens);
      }));
    }
    const failed = [users, status, updates, media, audit, usage, config, https, windows].find(r => r.status === 'rejected') as PromiseRejectedResult | undefined;
    if (failed && !sl.problem) sl.problem = errText(failed.reason);
    return {
      users: hostUsers,
      status: status.status === 'fulfilled' ? (status.value as Loose) : prev?.status ?? null,
      updates: updates.status === 'fulfilled' ? updates.value : prev?.updates ?? null,
      media: media.status === 'fulfilled' ? media.value : prev?.media ?? [],
      audit: audit.status === 'fulfilled' ? audit.value : prev?.audit ?? [],
      usage: usage.status === 'fulfilled' ? toUsage(usage.value as UsageSummary, sl.usageWindow) : prev?.usage ?? { window: sl.usageWindow, byUser: [], byVm: [] },
      config: config.status === 'fulfilled' ? toConfig(config.value as Loose) : prev?.config ?? [],
      https: https.status === 'fulfilled' ? https.value : prev?.https ?? null,
      windows: windows.status === 'fulfilled' ? toWindows(windows.value as Loose) : prev?.windows ?? { winKeys: [], winGuests: [] },
    };
  }

  /** Builds the state from the slices. */
  private compose() {
    const vms: Vm[] = [];
    const jobs: (Job & { at: string })[] = [];
    const hosts: Host[] = [];
    const names = new Map<string, number>();
    for (const sl of this.slices.values()) for (const v of sl.mine) names.set(v.name, (names.get(v.name) ?? 0) + 1);
    const now = Date.now();
    for (const [engine, sl] of this.slices) {
      const hv = hostVersion(sl.health);
      for (const r of sl.mine) {
        const id = names.get(r.name) === 1 ? r.name : vmKey(engine, r.name);
        const v = toVm(id, engine, r, sl.all, hv, sl.parts.get(r.name) ?? {});
        const t = this.transit.get(id);
        if (t && t.until > now && v.power !== (t.power === 'starting' ? 'on' : 'off')) v.power = t.power;
        else this.transit.delete(id);
        vms.push(v);
        // The newest VM job of this VM decides what its pages show.
        const own = sl.jobs.filter(j => j.vmName?.toLowerCase() === r.name.toLowerCase() && (j.kind === 'provision' || j.kind === 'create-vm'));
        const newest = own.sort((a, b) => a.created.localeCompare(b.created)).at(-1);
        if (newest) {
          const key = jobKey(engine, newest.id);
          const st = this.started.get(key);
          const job = toJob(key, id, newest, st?.kind);
          if (job && !this.dismissed.has(key)) jobs.push({ ...job, from: st?.from, to: st?.to, at: newest.created });
        }
      }
      hosts.push(this.host(engine, sl));
    }
    jobs.sort((a, b) => a.at.localeCompare(b.at));
    const auto = [...this.slices.values()].map(s => s.autoReprovision).filter(x => x !== null);
    this.emit({
      vms, hosts, jobs: jobs.map(({ at: _at, ...j }) => j),
      prefs: { ...this.s.prefs, autoUpdate: auto.length > 0 && auto.every(Boolean) },
    });
  }

  private host(engine: string, sl: Slice): Host {
    const a = sl.admin;
    const admin = sl.me?.role === 'admin';
    const st = toStatus(a?.status ?? null);
    const u = a?.updates ?? null;
    return {
      id: engine,
      name: sl.view.name,
      kind: '',
      version: hostVersion(sl.health),
      channel: u?.channel ?? '–',
      admin,
      address: sl.view.url,
      signIn: 'token',
      vmCount: admin ? sl.all.filter(v => v.kind !== 'child').length : sl.mine.length,
      health: st.health,
      capacity: st.capacity,
      users: a?.users ?? [],
      vms: admin ? sl.all.map(toHostVm) : [],
      media: (a?.media ?? []).map(toMedia),
      isos: [],
      winKeys: a?.windows.winKeys ?? [],
      winGuests: a?.windows.winGuests ?? [],
      jobs: admin ? sl.jobs.map(toHostJob).reverse() : [],
      audit: (a?.audit ?? []).map(x => ({ at: x.at.slice(0, 16).replace('T', ' '), actor: x.actor, action: x.action, target: x.target })),
      usage: a?.usage ?? { window: sl.usageWindow, byUser: [], byVm: [] },
      config: a?.config ?? [],
      cert: a?.https ? toCert(a.https, new Date()) : null,
      updates: u ? toUpdates(u) : [],
      updating: updateRunning(u?.current),
      updateTo: admin ? (u?.available && u.available.commit !== u.installed.commit ? u.available.version : null) : null,
      problem: sl.problem,
    };
  }

  /* --- events --- */

  /** One event from `/api/events`. Public for tests. */
  onEvent(type: string, ev: AgentEventData | null) {
    const data = (ev?.data ?? null) as Loose | null;
    const engine = ev?.engine ?? null;
    switch (type) {
      case 'agent': case 'reset': void this.load(); return;
      case 'pairing': return this.onPairing(data);
      case 'job.progress': case 'job.phase': case 'job.state': case 'job.lost': {
        const sl = engine ? this.slices.get(engine) : undefined;
        const id = typeof data?.jobId === 'string' ? data.jobId : null;
        if (!sl || !engine || !id) return;
        const i = sl.jobs.findIndex(j => j.id === id);
        if (type === 'job.progress' && i >= 0 && data?.line && typeof data.line === 'object') {
          sl.jobs[i] = { ...sl.jobs[i], state: sl.jobs[i].state === 'queued' ? 'running' : sl.jobs[i].state, progress: [...sl.jobs[i].progress, data.line as JobResponse['progress'][number]] };
        } else if (type === 'job.phase' && i >= 0) {
          sl.jobs[i] = { ...sl.jobs[i], phase: typeof data?.phase === 'string' ? data.phase : sl.jobs[i].phase };
        } else if (type === 'job.state' && data?.job && typeof data.job === 'object') {
          const job = data.job as JobResponse;
          if (i >= 0) sl.jobs[i] = job; else sl.jobs.push(job);
          this.jobEnded(engine, job);
          void this.refreshEngine(engine);
        } else if (type === 'job.lost' || i < 0) {
          void this.refreshEngine(engine);
        }
        this.compose();
        return;
      }
    }
  }

  private jobEnded(engine: string, job: JobResponse) {
    const key = jobKey(engine, job.id);
    if (job.state !== 'succeeded' && job.state !== 'failed' && job.state !== 'cancelled') return;
    const verb = { reprovision: 'Reprovisioned', reinstall: 'Reinstalled', update: 'Updated', restore: 'Restored', create: 'Created' }[this.started.get(key)?.kind ?? (job.kind === 'create-vm' ? 'create' : 'reprovision')];
    if (job.state === 'succeeded' && job.vmName) this.note(`${verb} ${job.vmName}`);
    if (this.provisionAfter.delete(key) && job.state === 'succeeded' && job.vmName) void this.provision(engine, job.vmName);
  }

  private onPairing(p: Loose | null) {
    const fr = this.s.firstRun;
    if (!fr || !p) return;
    if (p.state === 'done') { this.emit({ firstRun: null }); void this.load(); return; }
    if (p.state === 'failed') this.emit({ firstRun: { ...fr, step: 'start', pairing: null, busy: false, problem: typeof p.problem === 'string' ? p.problem : 'The pairing did not complete' } });
  }

  /* --- jobs --- */

  private async track(engine: string, jobId: string | null | undefined, as?: { kind: JobKind; from?: string; to?: string }) {
    if (!jobId) return;
    if (as) this.started.set(jobKey(engine, jobId), as);
    const sl = this.slices.get(engine);
    try {
      const job = await ok(this.engine(engine).GET('/api/v1/jobs/{id}', { params: { path: { id: jobId } } }));
      if (sl) { const i = sl.jobs.findIndex(j => j.id === jobId); if (i >= 0) sl.jobs[i] = job; else sl.jobs.push(job); }
      this.compose();
      this.schedule();
    } catch { void this.refreshEngine(engine); }
  }

  /** A provision job; the first one of a VM sends the engine's defaults as its settings. */
  private async provision(engine: string, name: string, as?: { kind: JobKind; from?: string; to?: string }) {
    const c = this.engine(engine);
    const path = { params: { path: { name } } };
    const post = (desired?: object) => ok(c.POST('/api/v1/vms/{name}/jobs', { ...path, body: { kind: 'provision', requestId: crypto.randomUUID(), ...(desired ? { desired } : {}) } as never }));
    await this.run(async () => {
      let r;
      try { r = await post(); } catch (e) {
        if (!(e instanceof ApiError) || e.code !== 'desired-state-missing') throw e;
        r = await post({ projects: [], settings: {} });
      }
      await this.track(engine, r.jobId, as);
    });
  }

  open(_vmId: string, _what: Open) { this.note(NA); }
  openForward(..._: unknown[]) { this.note(NA); }
  openChildConsole(..._: unknown[]) { this.note(NA); }
  setVmPassword(..._: unknown[]) { this.note(NA); }

  power(vmId: string, on: boolean) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    this.transit.set(vmId, { power: on ? 'starting' : 'stopping', until: Date.now() + 120000 });
    this.compose();
    void this.run(async () => {
      const r = await ok(this.engine(ref.engine).POST('/api/v1/vms/{name}/lifecycle', { params: { path: { name: ref.name } }, body: { action: on ? 'start' : 'shutdown' } as never }));
      void r;
    }, undefined, ref.engine).then(okay => { if (!okay) { this.transit.delete(vmId); this.compose(); } });
  }

  restart(vmId: string) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    void this.run(() => ok(this.engine(ref.engine).POST('/api/v1/vms/{name}/lifecycle', { params: { path: { name: ref.name } }, body: { action: 'restart' } as never })), `Restarting ${ref.name}`, ref.engine);
  }

  reprovision(vmId: string) {
    const ref = this.vmRef(vmId);
    if (ref) void this.provision(ref.engine, ref.name);
  }

  reinstall(..._: unknown[]) { this.note(NA); }

  updateEverything() {
    void (async () => {
      for (const [engine, sl] of this.slices) {
        const h = this.s.hosts.find(x => x.id === engine);
        const c = this.engine(engine);
        const mine = this.s.vms.filter(v => v.hostId === engine);
        if (h?.admin && h.updateTo && mine.length) {
          // The host updates first and reprovisions the VM named here after it succeeded. The engine takes
          // one follower per request, so the user's other VMs on this host update with the next click.
          await this.run(() => ok(c.POST('/api/v1/vms/{name}/update', { params: { path: { name: mine[0].name } } })), `Updating ${h.name}, then ${mine[0].name}`, engine);
        } else if (h?.admin && h.updateTo) {
          await this.run(() => ok(c.POST('/api/v1/host/updates/run')), `Updating ${h.name}`, engine);
        } else if (this.s.vms.some(v => v.hostId === engine && v.updateTo)) {
          await this.run(() => ok(c.POST('/api/v1/me/vms/reprovision')), `Updating your VMs on ${sl.view.name}`, engine);
        }
      }
    })();
  }

  updateVm(vmId: string) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    void this.run(async () => {
      const r = await ok(this.engine(ref.engine).POST('/api/v1/vms/{name}/update', { params: { path: { name: ref.name } } }));
      if (r.jobId) await this.track(ref.engine, r.jobId, { kind: 'update', from: ref.vm.version, to: ref.vm.updateTo ?? undefined });
      else this.note(`Updating ${this.s.hosts.find(h => h.id === ref.engine)?.name ?? 'the host'} first`);
    }, undefined, ref.engine);
  }

  stopJob(key: string) {
    const [engine, id] = splitJobKey(key);
    void this.run(() => ok(this.engine(engine).POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } })), 'Stopping', engine);
  }

  retryJob(key: string) {
    const job = this.s.jobs.find(j => j.id === key);
    const ref = job && this.vmRef(job.vmId);
    if (ref) void this.provision(ref.engine, ref.name);
  }

  replyRepair(..._: unknown[]) { this.note(NA); }

  dismissJob(key: string) {
    this.dismissed.add(key);
    store.set(DISMISSED_KEY, [...this.dismissed].slice(-200));
    this.compose();
  }

  saveSettings(vmId: string, patch: SettingsPatch, when?: 'now' | 'later') {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    const c = this.engine(ref.engine);
    const path = { params: { path: { name: ref.name } } };
    if (patch.services || patch.identity || patch.disk || patch.hw?.checkpoints !== undefined || patch.hw?.startWithHost !== undefined) { this.note(NA); return; }
    void this.run(async () => {
      if (patch.hw?.memGb !== undefined) await ok(c.PUT('/api/v1/vms/{name}/memory', { ...path, body: { ramGb: patch.hw.memGb } }));
      if (patch.hw?.cpus !== undefined) await ok(c.PUT('/api/v1/vms/{name}/cpu', { ...path, body: { cpus: patch.hw.cpus } }));
      if (patch.idle) {
        const idle = { ...ref.vm.settings.idle, ...patch.idle };
        await ok(c.PUT('/api/v1/vms/{name}/idle-policy', { ...path, body: { timeoutMinutes: idle.afterMin, action: idle.then === 'never' ? 'off' : idle.then } }));
      }
      if (patch.hw && when === 'now') await ok(c.POST('/api/v1/vms/{name}/lifecycle', { ...path, body: { action: 'restart' } as never }));
    }, patch.hw ? (when === 'now' ? `Restarting ${ref.name}` : 'Saved, applies at the next restart') : 'Saved', ref.engine);
  }

  updateAgents(..._: unknown[]) { this.note(NA); }
  setProjectOn(..._: unknown[]) { this.note(NA); }
  saveProfile(_p: Profile) { this.note(NA); }
  addProject(..._: unknown[]) { this.note(NA); }
  syncProjects(..._: unknown[]) { this.note(NA); }
  closeForward(..._: unknown[]) { this.note(NA); }
  setSharing(..._: unknown[]) { this.note(NA); }

  backupNow(vmId: string) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    void this.run(() => ok(this.engine(ref.engine).POST('/api/v1/vms/{name}/backups/request', { params: { path: { name: ref.name } }, body: { trigger: 'manual' } })),
      'Backup requested; the VM sends it at its next check-in', ref.engine);
  }

  restoreBackup(..._: unknown[]) { this.note(NA); }

  deleteBackup(vmId: string, backupId: string) {
    const ref = this.vmRef(vmId);
    const b = ref?.vm.backups.find(x => x.id === backupId);
    if (!ref || !b) return;
    // The screen asked already; the protected backup is deleted only with the engine's confirm flag.
    void this.run(() => ok(this.engine(ref.engine).DELETE('/api/v1/vms/{name}/backups/{id}', {
      params: { path: { name: ref.name, id: backupId }, query: b.protected ? { confirm: true } : {} },
    })), 'Backup deleted', ref.engine);
  }

  pinBackup(vmId: string, backupId: string, pinned: boolean) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    const c = this.engine(ref.engine);
    const params = { params: { path: { name: ref.name, id: backupId } } };
    void this.run(() => ok(pinned ? c.POST('/api/v1/vms/{name}/backups/{id}/pin', params) : c.DELETE('/api/v1/vms/{name}/backups/{id}/pin', params)), undefined, ref.engine);
  }

  setBackupSchedule(vmId: string, schedule: Partial<Vm['backupSchedule']>) {
    const ref = this.vmRef(vmId);
    if (!ref) return;
    const next = { ...ref.vm.backupSchedule, ...schedule };
    void this.run(() => ok(this.engine(ref.engine).PUT('/api/v1/vms/{name}/backups/settings', {
      params: { path: { name: ref.name } }, body: { intervalHours: next.everyH, keep: next.keep } as never,
    })), 'Saved', ref.engine);
  }

  createVm(req: CreateVmRequest): string | null {
    const name = req.name.trim();
    if (!name) return null;
    if (req.where === 'remote' && req.signIn !== 'token') { this.note(NA); return null; }
    void this.run(async () => {
      const engine = req.where === 'local' ? await this.localEngine() : await this.addEngine(req.hostAddress ?? '', req.token ?? null);
      // The engine takes the defaults for CPU and the rest; RAM comes from its recommendation, the disk from
      // today's default (Create-AgentVM.ps1), as the engine has none.
      const c = this.engine(engine);
      const d = await ok(c.GET('/api/v1/vm-defaults'));
      const r = await ok(c.POST('/api/v1/vms', { body: { name, ramGb: d.recommendedRamGb, diskGb: DEFAULT_DISK_GB } as never }));
      this.provisionAfter.add(jobKey(engine, r.jobId));
      await this.refreshEngine(engine);
      await this.track(engine, r.jobId, { kind: 'create' });
    });
    return name;
  }

  /** The engine on this machine: one already known on loopback, else the one the agent finds there. */
  private async localEngine(): Promise<string> {
    for (const [id, sl] of this.slices) if (LOOPBACK.test(sl.view.url)) return id;
    const r = await this.agent.GET('/api/engines/local');
    if (r.response.status !== 200 || !r.data) throw new Error('No Construct engine runs on this PC');
    return this.addEngine(r.data.url, null, r.data);
  }

  /** Adds an engine by address with the pin it shows now (trust on first use), or finds it when known. */
  private async addEngine(address: string, token: string | null, probed?: { url: string; pin: string; known: string | null }): Promise<string> {
    const p = probed ?? await ok(this.agent.POST('/api/engines/probe', { body: { address } }));
    if (p.known && !token) return p.known;
    const added = await ok(this.agent.POST('/api/engines', { body: { address: p.url, pin: p.pin, userToken: token } }));
    if (!this.slices.has(added.id)) {
      this.slices.set(added.id, { view: added, me: null, health: null, all: [], mine: [], parts: new Map(), jobs: [], admin: null, autoReprovision: null, usageWindow: 'month', problem: null });
    }
    return added.id;
  }

  /* --- key vault (not on records yet) --- */

  answerLease(..._: unknown[]) { this.note(NA); }
  enterSecret(..._: unknown[]) { this.note(NA); }
  addSecret(..._: unknown[]) { this.note(NA); }
  deleteSecret(..._: unknown[]) { this.note(NA); }
  setAutoApprove(..._: unknown[]) { this.note(NA); }
  takeBack(..._: unknown[]) { this.note(NA); }

  /* --- devices: pairing approves a new device by the code it shows --- */

  startPairing() { this.approving = null; this.emit({ pairing: { state: 'code' } }); }

  enterPairingCode(code: string) {
    const id = code.trim();
    if (!id) return;
    void (async () => {
      try {
        const r = await ok(this.agent.GET('/api/pairing/requests/{id}', { params: { path: { id } } }));
        this.approving = { id: r.pairingId, publicKey: r.publicKey, name: r.name };
        this.emit({ pairing: { state: 'confirm', words: r.words, deviceName: r.name } });
      } catch (e) {
        this.emit({ pairing: { state: 'code', problem: errText(e) } });
      }
    })();
  }

  confirmPairing(accept: boolean) {
    const a = this.approving;
    this.approving = null;
    if (!accept || !a) { this.emit({ pairing: null }); return; }
    void this.run(async () => {
      await ok(this.agent.POST('/api/pairing/requests/{id}/approve', { params: { path: { id: a.id } }, body: { publicKey: a.publicKey } }));
      this.emit({ pairing: { state: 'done', deviceName: a.name } });
      await this.loadDevices();
    });
  }

  cancelPairing() { this.approving = null; this.emit({ pairing: null }); }

  revokeDevice(deviceId: string) {
    const dev = this.s.devices.find(d => d.id === deviceId);
    if (!dev || dev.me) return;
    void this.run(async () => {
      const r = await ok(this.agent.DELETE('/api/devices/{id}', { params: { path: { id: deviceId } } }));
      this.emit({ revocation: { deviceId, deviceName: dev.name, hosts: r.engines.map(e => ({ name: e.name, confirmed: e.confirmed })), rotate: [] } });
      await this.loadDevices();
    }, `${dev.name} revoked`);
  }

  markRotated() { /* the vault is not on records yet */ }
  closeRevocation() { this.emit({ revocation: null }); }

  /* --- hosts --- */

  private hostAct(hostId: string, fn: (c: EngineClient) => Promise<unknown>, done?: string) {
    if (!this.slices.has(hostId)) return;
    void this.run(() => fn(this.engine(hostId)), done, hostId);
  }

  updateHost(hostId: string) {
    this.hostAct(hostId, c => ok(c.POST('/api/v1/host/updates/run')), `Updating ${this.slices.get(hostId)?.view.name ?? ''}`);
  }

  updateHostVms(hostId: string) {
    this.hostAct(hostId, c => ok(c.POST('/api/v1/host/vms/reprovision')), 'Reprovisioning every VM on this host, one after the other');
  }

  addUser(hostId: string, u: { name: string; role: 'admin' | 'user'; maxVms: number; hostForwards: HostUser['hostForwards'] }) {
    const name = u.name.trim();
    const sl = this.slices.get(hostId);
    if (!name || !sl) return;
    void this.run(async () => {
      const c = this.engine(hostId);
      await ok(c.POST('/api/v1/users', { body: { name, role: u.role, maxVms: u.maxVms, allowHostForwards: u.hostForwards === 'default' ? null : u.hostForwards === 'allowed' } }));
      const t = await ok(c.POST('/api/v1/users/{name}/tokens', { params: { path: { name } }, body: { label: 'first client' } }));
      this.emit({ handout: { hostId, user: name, address: sl.view.url, token: t.token } });
    }, `Added ${name}`, hostId);
  }

  saveUser(hostId: string, u: HostUser) {
    this.hostAct(hostId, async c => {
      const path = { params: { path: { name: u.name } } };
      await ok(c.PUT('/api/v1/users/{name}', { ...path, body: {
        role: u.role, enabled: u.enabled, maxVms: u.maxVms, allowHostForwards: u.hostForwards === 'default' ? null : u.hostForwards === 'allowed',
        allowHttpsForwards: u.httpsForwards, allowNested: u.nested === 'inherit' ? null : u.nested === 'on',
      } }));
      await ok(c.PUT('/api/v1/users/{name}/allowance', { ...path, body: allowanceOf(u) }));
    }, 'Saved');
  }

  removeUser(hostId: string, name: string) {
    this.hostAct(hostId, c => ok(c.DELETE('/api/v1/users/{name}', { params: { path: { name } } })), `Removed ${name}`);
  }

  issueToken(hostId: string, user: string, label: string) {
    const sl = this.slices.get(hostId);
    if (!sl) return;
    this.hostAct(hostId, async c => {
      const t = await ok(c.POST('/api/v1/users/{name}/tokens', { params: { path: { name: user } }, body: { label: label.trim() || 'token' } }));
      this.emit({ handout: { hostId, user, address: sl.view.url, token: t.token } });
    });
  }

  revokeToken(hostId: string, user: string, tokenId: string) {
    this.hostAct(hostId, c => ok(c.DELETE('/api/v1/users/{name}/tokens/{id}', { params: { path: { name: user, id: tokenId } } })), 'Token revoked');
  }

  closeHandout() { this.emit({ handout: null }); }

  hostVmAction(hostId: string, vm: string, action: 'start' | 'restart' | 'shutdown' | 'delete' | 'console' | 'rotate-token' | 'toggle-shared' | 'renew-lease') {
    const path = { params: { path: { name: vm } } };
    const hv = this.s.hosts.find(h => h.id === hostId)?.vms.find(v => v.name === vm);
    switch (action) {
      case 'console': case 'rotate-token': this.note(NA); return;
      case 'start': case 'restart': case 'shutdown':
        this.hostAct(hostId, c => ok(c.POST('/api/v1/vms/{name}/lifecycle', { ...path, body: { action, ...(action === 'start' && hv?.role === 'child' ? { lifetime: '12h' } : {}) } as never })),
          action === 'start' ? `Starting ${vm}` : action === 'restart' ? `Restarting ${vm}` : `Shutting down ${vm}`);
        return;
      case 'delete': {
        // A primary with children: the screen listed them and the admin typed the name. The engine answers with
        // its own list and a token; the delete goes ahead only when that list is the one the admin confirmed.
        const kids = new Set(this.s.hosts.find(h => h.id === hostId)?.vms.filter(v => v.parent === vm).map(v => v.name) ?? []);
        this.hostAct(hostId, async c => {
          try { await ok(c.DELETE('/api/v1/vms/{name}', path)); } catch (e) {
            if (!(e instanceof ApiError) || e.code !== 'cascade-confirmation-required' || typeof e.body?.cascadeToken !== 'string') throw e;
            const listed = (Array.isArray(e.body.children) ? e.body.children : []) as unknown[];
            const names = listed.map(x => (typeof x === 'string' ? x : String((x as Loose)?.name ?? (x as Loose)?.vmName ?? '')));
            if (names.length !== kids.size || names.some(n => !kids.has(n))) throw new Error(`The child VMs of ${vm} changed; look again`);
            await ok(c.DELETE('/api/v1/vms/{name}', { ...path, body: { cascade: { token: e.body.cascadeToken } } } as never));
          }
        }, `Deleting ${vm}`);
        return;
      }
      case 'toggle-shared': this.hostAct(hostId, c => ok(c.PUT('/api/v1/vms/{name}/sharing', { ...path, body: { scope: hv?.shared ? 'private' : 'host' } as never })), 'Saved'); return;
      case 'renew-lease': this.hostAct(hostId, c => ok(c.POST('/api/v1/vms/{name}/lease', { ...path, body: {} as never })), 'Lifetime restarted'); return;
    }
  }

  saveHostVm(hostId: string, vm: string, patch: Partial<Pick<HostVm, 'cpus' | 'memMaxGb' | 'network'>>) {
    const hv = this.s.hosts.find(h => h.id === hostId)?.vms.find(v => v.name === vm);
    if (!hv) return;
    this.hostAct(hostId, async c => {
      const path = { params: { path: { name: vm } } };
      if (hv.role === 'child') {
        if ((patch.cpus !== undefined && patch.cpus !== hv.cpus) || (patch.memMaxGb !== undefined && patch.memMaxGb !== hv.memMaxGb))
          await ok(c.PUT('/api/v1/vms/{name}/hardware', { ...path, body: { cpus: patch.cpus ?? hv.cpus, ramMb: (patch.memMaxGb ?? hv.memMaxGb) * 1024 } as never }));
      } else {
        if (patch.cpus !== undefined && patch.cpus !== hv.cpus) await ok(c.PUT('/api/v1/vms/{name}/cpu', { ...path, body: { cpus: patch.cpus } }));
        if (patch.memMaxGb !== undefined && patch.memMaxGb !== hv.memMaxGb) await ok(c.PUT('/api/v1/vms/{name}/memory', { ...path, body: { ramGb: patch.memMaxGb } }));
      }
      if (hv.role === 'primary' && patch.network !== undefined && patch.network !== hv.network) await ok(c.PUT('/api/v1/vms/{name}/network', { ...path, body: { mode: patch.network === 'default' ? null : patch.network } as never }));
    }, 'Saved, applies at the next restart');
  }

  deleteMedia(hostId: string, name: string) {
    const m = this.slices.get(hostId)?.admin?.media.find(x => x.name === name);
    if (m) this.hostAct(hostId, c => ok(c.DELETE('/api/v1/media/{id}', { params: { path: { id: m.id } } })), `Deleted ${name}`);
  }

  cleanupMedia(hostId: string) { this.hostAct(hostId, c => ok(c.POST('/api/v1/media/cleanup')), 'Cleanup started'); }

  acquireWindows(hostId: string, product: string, edition: string, language: string) {
    this.hostAct(hostId, c => ok(c.POST('/api/v1/media/acquire-windows', { body: { windows: product, edition, lang: language } as never })), 'Download started');
  }

  addWinKey(hostId: string, key: Omit<WinKey, 'id' | 'partial' | 'used' | 'held'> & { key: string }) {
    this.hostAct(hostId, c => ok(c.POST('/api/v1/host/windows/keys', { body: { product: key.product, edition: key.edition, kind: key.kind, key: key.key.trim() } as never })), 'Key added');
  }

  removeWinKey(hostId: string, id: string) {
    this.hostAct(hostId, c => ok(c.DELETE('/api/v1/host/windows/keys/{id}', { params: { path: { id } } } as never)));
  }

  private guest(hostId: string, name: string) { return this.slices.get(hostId)?.admin?.windows.winGuests.find(g => g.name === name); }

  assignWinKey(hostId: string, guest: string, keyId: string) {
    const g = this.guest(hostId, guest);
    if (g) this.hostAct(hostId, c => ok(c.POST('/api/v1/host/windows/guests/{name}/assign', { params: { path: { name: guest } }, body: { incarnation: g.incarnation, keyId } } as never)), `Key assigned to ${guest}`);
  }

  activateWindows(hostId: string, guest: string) {
    const g = this.guest(hostId, guest);
    if (g) this.hostAct(hostId, c => ok(c.POST('/api/v1/host/windows/guests/{name}/reactivate', { params: { path: { name: guest } }, body: { incarnation: g.incarnation, operationId: crypto.randomUUID() } } as never)), `Activating ${guest}`);
  }

  cancelHostJob(hostId: string, jobId: string) {
    this.hostAct(hostId, c => ok(c.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id: jobId } } })), 'Cancelling');
  }

  retryHostJob(..._: unknown[]) { this.note(NA); }

  setUsageWindow(hostId: string, window: Host['usage']['window']) {
    const sl = this.slices.get(hostId);
    if (!sl) return;
    sl.usageWindow = window;
    void this.refreshEngine(hostId);
  }

  saveConfig(hostId: string, section: string, json: string): string | null {
    let value: unknown;
    try { value = JSON.parse(json); } catch (e) { return (e as Error).message; }
    this.hostAct(hostId, c => ok(c.PUT('/api/v1/host/config', { body: { [section]: value } as never })), `Saved ${section}`);
    return null;
  }

  installCertificate(hostId: string, pem: string) {
    const key = /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----/.exec(pem)?.[0] ?? null;
    const certs = pem.match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g);
    if (!certs) { this.note('That is not a PEM certificate'); return; }
    this.hostAct(hostId, c => ok(c.PUT('/api/v1/host/https/certificate', { body: { certificatePem: certs.join('\n'), privateKeyPem: key, pfx: null, password: null } })), 'Certificate installed');
  }

  removeCertificate(hostId: string) { this.hostAct(hostId, c => ok(c.DELETE('/api/v1/host/https/certificate')), 'Certificate removed'); }

  /* --- this device --- */

  setPrefs(patch: Partial<Prefs>) {
    const prefs = { ...this.s.prefs, ...patch };
    this.emit({ prefs });
    store.set(PREFS_KEY, { theme: prefs.theme, scheme: prefs.scheme });
    if (patch.autoUpdate !== undefined) {
      for (const id of this.slices.keys()) void this.run(() => ok(this.engine(id).PUT('/api/v1/me/updates', { body: { autoReprovision: patch.autoUpdate! } })), undefined, id);
    }
    if (patch.channel !== undefined || patch.startWithOs !== undefined || patch.notifications !== undefined) this.note(NA);
  }

  /** A new recovery code; the old one stops working. The agent never shows a code twice. */
  async showRecoveryCode(): Promise<string[]> {
    const r = await ok(this.agent.POST('/api/device/recovery-code'));
    return codeGroups(r.recoveryCode);
  }

  exportRecoveryCode(..._: unknown[]) { this.note(NA); }
  openLog(..._: unknown[]) { this.note(NA); }
  upgradeNext() { /* the one-time upgrade from the old stack is not part of this backend */ }

  /* --- first run --- */

  firstRunStart(req: FirstRunRequest) {
    const fr = this.s.firstRun;
    if (!fr || fr.busy) return;
    const set = (p: Partial<NonNullable<State['firstRun']>>) => this.emit({ firstRun: { ...(this.s.firstRun ?? fr), ...p } });
    set({ busy: true, problem: null });
    void (async () => {
      try {
        const engine = await this.addEngine(req.address.trim(), req.how === 'setup' ? (req.token ?? '').trim() || null : null);
        const deviceName = req.deviceName.trim();
        if (req.how === 'setup') {
          const r = await ok(this.agent.POST('/api/device/setup', { body: { engine, deviceName } }));
          let account: string | null = null;
          try { account = (await ok(this.agent.GET('/api/state'))).device.account ?? null; } catch { /* shown without it */ }
          set({ busy: false, step: 'code', code: codeGroups(r.recoveryCode), account });
        } else if (req.how === 'pair') {
          const p = await ok(this.agent.POST('/api/pairing', { body: { engine, deviceName } }));
          set({ busy: false, step: 'pairing', pairing: { code: p.pairingId, words: p.words, expiresAt: p.expiresAt } });
        } else {
          await ok(this.agent.POST('/api/device/recover', { body: { engine, account: (req.account ?? '').trim(), recoveryCode: (req.code ?? '').trim(), deviceName } }));
          this.emit({ firstRun: null });
          await this.load();
        }
      } catch (e) {
        set({ busy: false, problem: errText(e) });
      }
    })();
  }

  firstRunNext() {
    const fr = this.s.firstRun;
    if (!fr) return;
    if (fr.step === 'code') {
      // The code leaves the state here; it is not kept anywhere.
      this.emit({ firstRun: null });
      void this.load();
    } else if (fr.step === 'pairing') {
      void this.agent.DELETE('/api/pairing').catch(() => {});
      this.emit({ firstRun: { ...fr, step: 'start', pairing: null } });
    }
  }
}
