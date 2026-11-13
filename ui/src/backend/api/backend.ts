import createClient, { type Client } from 'openapi-fetch';
import type { paths as AgentPaths } from '../generated/agent';
import type { paths as EnginePaths } from '../generated/engine';
import type {
  Backend, CreateVmRequest, Feature, FirstRunRequest, Host, HostProbe, HostUser, HostVm, Job, JobKind, Open, Prefs, ProfileSave, SettingsPatch, State, VaultMode, VaultRequest, Vm, WinKey,
} from '../types';
import { followEvents, type AgentEventData } from './events';
import {
  updateRunning, allowanceOf, codeGroups, hostVersion, jobKey, problemText, splitJobKey, toCert, toConfig, toDevice, toHostJob, toHostUser, toHostVm, toJob,
  toMedia, toStatus, toUpdates, toUsage, toVm, toWindows, vmKey, toSecret, toLeases, toVaultEvent, toApproval, toEntryRequest, toProfile, splitRequestKey, VM_JOB_KINDS,
  type DeviceItem, type ProfileResponse, type AuditEntry, type EngineView, type Health, type HostUpdateStatus, type HttpsStatus, type JobResponse, type MediaItem, type TokenSummary,
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
  'repair-chat', 'forwards', 'sharing', 'agents', 'open', 'console', 'vm-password', 'vm-services', 'vm-disk', 'vm-boot',
  'restore', 'device-update', 'channel', 'os-prefs', 'windows-sign-in', 'export-code', 'code-reveal', 'vm-token', 'host-retry',
  'take-back', 'project-on', 'approval-span',
];

const NA = 'Not available yet';
const DEFAULT_DISK_GB = 50;
const PREFS_KEY = 'construct.prefs';
const DISMISSED_KEY = 'construct.dismissed';

interface Started { kind: JobKind; from?: string; to?: string; freshImage?: boolean }

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
  /** Jobs this page saw end, by id. */
  ended: Map<string, JobResponse>;
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

const ended = (j: JobResponse) => j.state === 'succeeded' || j.state === 'failed' || j.state === 'cancelled';

/** The engine's job list, but a job this page saw end stays ended and listed (a list read before the end may
    arrive after the event). */
function mergeJobs(seen: Map<string, JobResponse>, read: JobResponse[]): JobResponse[] {
  const out = read.map(j => (!ended(j) && seen.has(j.id) ? seen.get(j.id)! : j));
  for (const [id, j] of seen) if (!out.some(x => x.id === id)) out.push(j);
  return out;
}

const LOOPBACK = /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?\/?$/i;

export interface ApiOptions {
  /** The agent's origin, e.g. `http://127.0.0.1:5123`. */
  base: string;
  /** The agent's token from the URL fragment; sent only as a header. */
  token: string;
  fetch?: typeof fetch;
  /** Milliseconds between reads of what the event stream does not announce (usage, guest reports, host
      administration, profiles, secrets); 0 turns polling off (tests). Default one minute. */
  pollMs?: number;
  /** Milliseconds between reads of the pending vault requests, which have no event. Default 10 s, off with `pollMs: 0`. */
  vaultPollMs?: number;
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
  /** Jobs this UI started, with the kind it shows them as, the version an update goes from and to, and how a reinstall was asked for. */
  private started = new Map<string, Started>();
  /** create-vm jobs after which the VM is provisioned. */
  private provisionAfter = new Set<string>();
  private transit = new Map<string, { power: Vm['power']; until: number }>();
  private approving: { id: string; publicKey: string; name: string } | null = null;
  private refreshing = new Map<string, Promise<void>>();
  private again = new Set<string>();
  private stopEvents: (() => void) | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private vaultTimer: ReturnType<typeof setTimeout> | null = null;
  private fetchImpl: typeof fetch;
  /** The engine that holds the store: profiles are read and written there. */
  private storeEngine: string | null = null;
  private deviceItems: DeviceItem[] = [];
  private registered = false;
  /** Vault requests answered here; a read that was under way does not bring them back. */
  private answeredKeys = new Set<string>();

  constructor(private opts: ApiOptions) {
    this.fetchImpl = opts.fetch ?? ((...a: Parameters<typeof fetch>) => fetch(...a));
    const headers = { Authorization: `Bearer ${opts.token}` };
    this.agent = createClient<AgentPaths>({ baseUrl: opts.base, headers, fetch: this.fetchImpl });
    const p = store.get<Partial<Prefs>>(PREFS_KEY, {});
    this.s = {
      device: { name: '' }, version: '', release: null, vms: [], hosts: [], projects: [], sync: { when: '–', remote: '', rev: '' }, jobs: [],
      vaultRequests: [], secrets: [], leases: [], devices: [], history: [], vaultModes: [], problems: {}, pairing: null, revocation: null, handout: null,
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
    this.scheduleVault();
  }

  dispose() {
    this.stopEvents?.();
    if (this.timer) clearTimeout(this.timer);
    if (this.vaultTimer) clearTimeout(this.vaultTimer);
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

  /** The slow poll: jobs, VMs, backups and updates arrive as events; this reads what has none. */
  private schedule() {
    const ms = this.opts.pollMs ?? 60000;
    if (!ms) return;
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => { void this.refreshAll().finally(() => this.schedule()); }, ms);
  }

  /** Vault requests have no event; a VM waits on them, so they are read more often. */
  private scheduleVault() {
    const ms = this.opts.pollMs === 0 && this.opts.vaultPollMs === undefined ? 0 : this.opts.vaultPollMs ?? 10000;
    if (!ms) return;
    if (this.vaultTimer) clearTimeout(this.vaultTimer);
    this.vaultTimer = setTimeout(() => { void this.loadRequests().finally(() => this.scheduleVault()); }, ms);
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
      this.registered = st.device.registered;
      this.storeEngine = st.device.storeEngine;
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
        else this.slices.set(e.id, { view: e, me: null, health: null, all: [], mine: [], parts: new Map(), jobs: [], ended: new Map(), admin: null, autoReprovision: null, usageWindow: 'month', problem: null });
      }
      this.compose();
      await Promise.all([this.loadDevices(), ...st.engines.map(e => this.refreshEngine(e.id)), this.loadVault(), this.loadProfiles()]);
      // After the VMs, so a request names the VM the screens know.
      await this.loadRequests();
    } catch (e) {
      this.note(errText(e));
    }
  }

  private async loadDevices() {
    try {
      const list = await ok(this.agent.GET('/api/devices'));
      this.deviceItems = list;
      this.emit({ devices: list.map(toDevice) });
    } catch { /* the devices table keeps what it had */ }
  }

  private async refreshAll() {
    await Promise.all([this.loadDevices(), ...[...this.slices.keys()].map(id => this.refreshEngine(id)), this.loadVault(), this.loadProfiles()]);
  }

  private problem(key: keyof State['problems'], text: string | null) {
    const problems = { ...this.s.problems };
    if (text) problems[key] = text; else delete problems[key];
    this.emit({ problems });
  }

  /** Secrets, their leases and history, and each host's vault mode. Values never come here. */
  private async loadVault() {
    if (!this.registered) return;
    const [secrets, history] = await Promise.allSettled([ok(this.agent.GET('/api/vault/secrets')), ok(this.agent.GET('/api/vault/history'))]);
    if (secrets.status === 'fulfilled') {
      this.emit({ secrets: secrets.value.map(toSecret), leases: toLeases(secrets.value, Date.now()) });
      this.problem('secrets', null);
    } else this.problem('secrets', errText(secrets.reason));
    if (history.status === 'fulfilled') {
      this.emit({ history: history.value.map(h => toVaultEvent(h, this.s.devices)) });
      this.problem('history', null);
    } else this.problem('history', errText(history.reason));
    const modes = await Promise.all([...this.slices.values()].map(async sl => {
      try {
        const m = await ok(this.agent.GET('/api/vault/engines/{engine}/mode', { params: { path: { engine: sl.view.id } } }));
        return { hostId: sl.view.id, hostName: sl.view.name, mode: m.mode === 'available' || m.mode === 'locked' ? m.mode : null } as VaultMode;
      } catch (e) {
        return { hostId: sl.view.id, hostName: sl.view.name, mode: null, problem: errText(e) } as VaultMode;
      }
    }));
    this.emit({ vaultModes: modes });
  }

  /** The VM id the state uses for a VM an engine names. */
  private vmIdOf(engine: string, name: string): string {
    return this.s.vms.find(v => v.hostId === engine && v.name.toLowerCase() === name.toLowerCase())?.id ?? '';
  }

  /** The pending approvals and entry requests of every engine; an engine that does not answer keeps its own. */
  private async loadRequests() {
    if (!this.registered) return;
    const [approvals, entries] = await Promise.allSettled([ok(this.agent.GET('/api/vault/approvals')), ok(this.agent.GET('/api/vault/entry-requests'))]);
    const unreachable = new Set<string>();
    const fresh: VaultRequest[] = [];
    const failed: string[] = [];
    if (approvals.status === 'fulfilled') {
      approvals.value.unreachable.forEach(e => unreachable.add(e));
      fresh.push(...approvals.value.approvals.map(a => toApproval(a, this.vmIdOf(a.engine, a.vm))));
    } else failed.push(errText(approvals.reason));
    if (entries.status === 'fulfilled') {
      entries.value.unreachable.forEach(e => unreachable.add(e));
      fresh.push(...entries.value.requests.map(r => toEntryRequest(r, this.vmIdOf(r.engine, r.vm))));
    } else failed.push(errText(entries.reason));
    // A route that failed, or an engine that did not answer, keeps the requests it had.
    const keep = this.s.vaultRequests.filter(r => {
      const [engine] = splitRequestKey(r.id);
      return unreachable.has(engine) || (r.kind === 'lease' ? approvals.status === 'rejected' : entries.status === 'rejected');
    });
    const ids = new Set(fresh.map(r => r.id));
    this.emit({ vaultRequests: [...keep.filter(r => !ids.has(r.id) && !this.answeredKeys.has(r.id)), ...fresh.filter(r => !this.answeredKeys.has(r.id))] });
    const names = [...unreachable].map(id => this.slices.get(id)?.view.name ?? id);
    this.problem('requests', failed[0] ?? (names.length ? `Not reachable: ${names.join(', ')}` : null));
  }

  /** The engine profiles are read from and written to: the store's, else the first one. */
  private profileEngine(): string | null {
    if (this.storeEngine && this.slices.has(this.storeEngine)) return this.storeEngine;
    return this.slices.keys().next().value ?? null;
  }

  private async loadProfiles() {
    const engine = this.profileEngine();
    if (!engine || !this.registered) return;
    const c = this.engine(engine);
    try {
      const [list, conflicts] = await Promise.all([ok(c.GET('/api/v1/profiles')), ok(c.GET('/api/v1/profile-conflicts')).catch(() => [])]);
      const open = new Set(conflicts.map(x => x.profile));
      const docs = await Promise.all(list.map(async p => {
        try { return await ok(c.GET('/api/v1/profiles/{profile}', { params: { path: { profile: p.name } } })); } catch { return null; }
      }));
      const projects = docs.filter((d): d is ProfileResponse => d !== null).map(d => toProfile(d, open.has(d.name))).sort((a, b) => a.name.localeCompare(b.name));
      this.emit({ projects, sync: { when: new Date().toISOString().slice(11, 16), remote: this.slices.get(engine)?.view.name ?? '', rev: '' } });
      this.problem('projects', null);
    } catch (e) {
      this.problem('projects', errText(e));
    }
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
    if (jobs.status === 'fulfilled') sl.jobs = mergeJobs(sl.ended, jobs.value);
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
        const own = sl.jobs.filter(j => j.vmName?.toLowerCase() === r.name.toLowerCase() && VM_JOB_KINDS.includes(j.kind));
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

  /** One event from `/api/events`. Public for tests. An engine's change names what changed, never the state:
      the thing is read again through the proxy. */
  onEvent(type: string, ev: AgentEventData | null) {
    const data = (ev?.data ?? null) as Loose | null;
    const engine = ev?.engine ?? null;
    const id = typeof data?.id === 'string' ? data.id : null;
    switch (type) {
      case 'agent': case 'reset': void this.load(); return;
      case 'pairing': return this.onPairing(data);
      case 'job.started': case 'job.progress': case 'job.ended':
        if (engine && id && this.slices.has(engine)) void this.readJob(engine, id);
        return;
      case 'backup.added': case 'backup.removed': {
        const vm = typeof data?.vm === 'string' ? data.vm : null;
        if (engine && vm && this.slices.has(engine)) void this.readBackups(engine, vm);
        else if (engine && this.slices.has(engine)) void this.refreshEngine(engine);
        return;
      }
      case 'engine.reset': case 'job.lost': case 'vm.created': case 'vm.changed': case 'vm.power': case 'vm.deleted': case 'update.offered': case 'update.finished':
        if (engine && this.slices.has(engine)) void this.refreshEngine(engine);
        return;
    }
  }

  /** Reads one job after an event about it. A job that ended stays ended whatever an older read says. */
  private async readJob(engine: string, id: string) {
    const sl = this.slices.get(engine);
    if (!sl) return;
    let job: JobResponse;
    try {
      job = await ok(this.engine(engine).GET('/api/v1/jobs/{id}', { params: { path: { id } } }));
    } catch {
      void this.refreshEngine(engine);
      return;
    }
    const known = sl.ended.get(id);
    if (known && !ended(job)) return;
    const i = sl.jobs.findIndex(j => j.id === id);
    if (i >= 0) sl.jobs[i] = job; else sl.jobs.push(job);
    if (ended(job)) sl.ended.set(id, job);
    this.compose();
    if (ended(job) && !known) {
      this.jobEnded(engine, job);
      void this.refreshEngine(engine);
    }
  }

  /** Reads one VM's backups after a backup event. */
  private async readBackups(engine: string, vm: string) {
    const sl = this.slices.get(engine);
    const own = sl?.mine.find(v => v.name.toLowerCase() === vm.toLowerCase());
    if (!sl || !own) return;
    const parts = sl.parts.get(own.name) ?? {};
    try {
      const r = await ok(this.engine(engine).GET('/api/v1/vms/{name}/backups', { params: { path: { name: own.name } } }));
      const { backups: _b, ...problems } = parts.problems ?? {};
      sl.parts.set(own.name, { ...parts, backups: r.backups, problems });
    } catch (e) {
      sl.parts.set(own.name, { ...parts, problems: { ...parts.problems, backups: errText(e) } });
    }
    this.compose();
  }

  private jobEnded(engine: string, job: JobResponse) {
    const key = jobKey(engine, job.id);
    if (!ended(job)) return;
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

  /** Follows a job this UI started; its progress and end arrive as events. */
  private async track(engine: string, jobId: string | null | undefined, as?: Started) {
    if (!jobId) return;
    if (as) this.started.set(jobKey(engine, jobId), as);
    await this.readJob(engine, jobId);
  }

  /** A provision job; the first one of a VM sends the engine's defaults as its settings. */
  private provision(engine: string, name: string, as?: Started) {
    return this.vmJob(engine, name, 'provision', {}, as);
  }

  /** A provision or reinstall job of a primary. Without stored settings (the first job) the engine's defaults go along. */
  private async vmJob(engine: string, name: string, kind: 'provision' | 'reinstall', extra: Loose, as?: Started) {
    const c = this.engine(engine);
    const path = { params: { path: { name } } };
    const post = (desired?: object) => ok(c.POST('/api/v1/vms/{name}/jobs', { ...path, body: { kind, requestId: crypto.randomUUID(), ...extra, ...(desired ? { desired } : {}) } as never }));
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

  /** The screen asked in place first: Wipe, with the fresh-image switch. */
  reinstall(vmId: string, freshImage: boolean) {
    const ref = this.vmRef(vmId);
    if (ref) void this.vmJob(ref.engine, ref.name, 'reinstall', { freshImage }, { kind: 'reinstall', freshImage });
  }

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

  /** The engine has no retry route yet: the same job kind starts again (a reinstall with how it was asked for). */
  retryJob(key: string) {
    const job = this.s.jobs.find(j => j.id === key);
    const ref = job && this.vmRef(job.vmId);
    if (!job || !ref) return;
    if (job.kind === 'restore') { this.note(NA); return; }
    if (job.kind === 'reinstall') {
      const freshImage = this.started.get(key)?.freshImage ?? false;
      void this.vmJob(ref.engine, ref.name, 'reinstall', { freshImage }, { kind: 'reinstall', freshImage });
      return;
    }
    void this.provision(ref.engine, ref.name);
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
  /** No route sets a VM's profiles outside a provision job's whole desired state. */
  setProjectOn(..._: unknown[]) { this.note(NA); }

  /** Through the device route, with the revision the editor read: the engine merges a newer one per field or
      opens a conflict for an agent; it never overwrites it. */
  async saveProfile(name: string, doc: Record<string, unknown>, revision: number | undefined): Promise<ProfileSave> {
    const engine = this.profileEngine();
    if (!engine) return { ok: false, changed: false, text: 'No host to save it on' };
    try {
      const r = await ok(this.engine(engine).PUT('/api/v1/profiles/{profile}', { params: { path: { profile: name } }, body: { profile: doc as never, baseRevision: revision ?? null } }));
      await this.loadProfiles();
      if (r.status === 'conflict') return { ok: false, changed: true, text: `${name} changed meanwhile; an agent is resolving this` };
      return { ok: true, text: r.status === 'queued' ? 'Saved; it reaches the store when the store answers' : 'Saved' };
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.code === 'refused-by-store') {
        await this.loadProfiles();
        return { ok: false, changed: true, text: `${name} changed meanwhile; this is the current version` };
      }
      return { ok: false, changed: false, text: errText(e) };
    }
  }

  addProject(name: string) {
    const n = name.trim();
    if (!n) return;
    if (this.s.projects.some(p => p.name === n)) { this.note(`${n} exists already`); return; }
    void this.saveProfile(n, { name: n }, undefined).then(r => this.note(r.ok ? `Added ${n}` : r.text));
  }

  syncProjects() { void this.loadProfiles(); }
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
      const engine = req.where === 'local' ? await this.localEngine() : await this.addEngine(req.hostAddress ?? '', req.token ?? null, req.pin ?? null);
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
    return this.addEngine(r.data.url, null, r.data.pin, r.data);
  }

  async probeHost(address: string): Promise<HostProbe> {
    const p = await ok(this.agent.POST('/api/engines/probe', { body: { address: address.trim() } }));
    return { url: p.url, pin: p.pin, known: !!p.known };
  }

  /** Adds an engine with the pin the user confirmed (the engine on this machine needs none: `local`), or finds
      it when known. The agent connects only to the certificate the pin names. */
  private async addEngine(address: string, token: string | null, pin: string | null, local?: { url: string; pin: string; known: string | null }): Promise<string> {
    const p = local ?? await ok(this.agent.POST('/api/engines/probe', { body: { address } }));
    if (p.known && !token) return p.known;
    if (!pin) throw new Error('Confirm the host\'s fingerprint first');
    if (pin.toUpperCase() !== p.pin.toUpperCase()) throw new Error('The host now presents a different certificate than the one you confirmed');
    const added = await ok(this.agent.POST('/api/engines', { body: { address: p.url, pin, userToken: token } }));
    if (!this.slices.has(added.id)) {
      this.slices.set(added.id, { view: added, me: null, health: null, all: [], mine: [], parts: new Map(), jobs: [], ended: new Map(), admin: null, autoReprovision: null, usageWindow: 'month', problem: null });
    }
    return added.id;
  }

  /* --- key vault: the agent seals and opens; a value passes through here only on its way, never into the state --- */

  /** Takes a request off the screen at once; it is answered at the engine that holds it. */
  private answer(key: string): { engine: string; id: string; r: VaultRequest } | null {
    const r = this.s.vaultRequests.find(x => x.id === key);
    if (!r) return null;
    this.answeredKeys.add(key);
    this.emit({ vaultRequests: this.s.vaultRequests.filter(x => x.id !== key) });
    const [engine, id] = splitRequestKey(key);
    return { engine, id, r };
  }

  answerLease(key: string, allow: boolean) {
    const a = this.answer(key);
    if (!a) return;
    void this.run(() => ok(this.agent.POST('/api/vault/approvals/{engine}/{id}', { params: { path: { engine: a.engine, id: a.id } }, body: { decision: allow ? 'allow' : 'deny' } })),
      allow ? `Allowed ${a.r.vmName}` : `Denied ${a.r.vmName}`).then(done => this.answeredWith(key, done));
  }

  /** After an answer: a request whose answer failed comes back, if the engine still holds it. */
  private async answeredWith(key: string, done: boolean) {
    if (!done) { this.answeredKeys.delete(key); await this.loadRequests(); }
    await this.loadVault();
  }

  enterSecret(key: string, value: string | null) {
    const a = this.answer(key);
    if (!a) return;
    const path = { params: { path: { engine: a.engine, id: a.id } } };
    if (value === null) {
      void this.run(() => ok(this.agent.POST('/api/vault/entry-requests/{engine}/{id}/decline', path)), 'Declined').then(done => this.answeredWith(key, done));
      return;
    }
    void (async () => {
      let done = true;
      try {
        await ok(this.agent.POST('/api/vault/entry-requests/{engine}/{id}', { ...path, body: { value } }));
        this.note(`Saved ${a.r.secret}`);
      } catch (e) {
        // A name taken meanwhile ends the request at the engine; anything else leaves it to answer again.
        const exists = e instanceof ApiError && e.code === 'exists';
        done = exists;
        this.note(exists ? `${a.r.secret} exists already; nothing was overwritten` : errText(e));
      }
      await this.answeredWith(key, done);
    })();
  }

  addSecret(name: string, note: string, value: string) {
    void this.run(() => ok(this.agent.POST('/api/vault/secrets', { body: { name: name.trim(), description: note, value, autoApprove: false } })), `Added ${name.trim()}`)
      .then(() => this.loadVault());
  }

  replaceSecret(name: string, value: string) {
    void this.run(() => ok(this.agent.PATCH('/api/vault/secrets/{name}', { params: { path: { name } }, body: { value } })), `Replaced ${name}`).then(() => this.loadVault());
  }

  async revealSecret(name: string): Promise<string> {
    return (await ok(this.agent.POST('/api/vault/secrets/{name}/reveal', { params: { path: { name } } }))).value;
  }

  deleteSecret(name: string) {
    void this.run(() => ok(this.agent.DELETE('/api/vault/secrets/{name}', { params: { path: { name } } })), `Deleted ${name}`).then(() => this.loadVault());
  }

  setAutoApprove(name: string, on: boolean) {
    void this.run(() => ok(this.agent.PUT('/api/vault/secrets/{name}/auto-approve', { params: { path: { name } }, body: { on } }))).then(() => this.loadVault());
  }

  setVaultMode(hostId: string, mode: 'available' | 'locked') {
    if (!this.slices.has(hostId)) return;
    void this.run(() => ok(this.agent.PUT('/api/vault/engines/{engine}/mode', { params: { path: { engine: hostId } }, body: { mode } })), 'Saved').then(() => this.loadVault());
  }

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
      await Promise.all([this.loadDevices(), this.loadRotate(deviceId)]);
    }, `${dev.name} revoked`);
  }

  /** A revoked device's page opened from the list: the engines that acknowledged it and the secrets to rotate. */
  openRevocation(deviceId: string) {
    if (this.s.revocation?.deviceId === deviceId) return;
    const dev = this.deviceItems.find(d => d.id === deviceId);
    if (!dev || !dev.revoked) return;
    const hosts = [...this.slices.values()].map(sl => ({ name: sl.view.name, confirmed: !!sl.view.engineId && dev.acknowledgedBy.includes(sl.view.engineId) }));
    this.emit({ revocation: { deviceId, deviceName: dev.name, hosts, rotate: [] } });
    void this.loadRotate(deviceId);
  }

  /** Every secret the device could read, to rotate at its source. */
  private async loadRotate(deviceId: string) {
    try {
      const list = await ok(this.agent.GET('/api/devices/{id}/secrets', { params: { path: { id: deviceId } } }));
      const r = this.s.revocation;
      if (r?.deviceId !== deviceId) return;
      this.emit({ revocation: { ...r, rotate: list.map(x => ({ secret: x.name, last: this.s.secrets.find(y => y.name === x.name)?.last ?? '–', done: false })) } });
    } catch (e) {
      this.note(errText(e));
    }
  }

  /** A mark for this page only: rotating happens at the secret's source. */
  markRotated(secret: string) {
    const r = this.s.revocation;
    if (r) this.emit({ revocation: { ...r, rotate: r.rotate.map(x => (x.secret === secret ? { ...x, done: !x.done } : x)) } });
  }
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
        const token = req.how === 'setup' ? (req.token ?? '').trim() || null : null;
        const address = req.address.trim();
        // The engine on this machine is trusted as found; any other only with the fingerprint the user confirmed.
        let engine: string;
        if (fr.localAddress && address === fr.localAddress) {
          const local = await this.agent.GET('/api/engines/local');
          if (local.response.status !== 200 || !local.data) throw new Error('No Construct engine runs on this PC');
          engine = await this.addEngine(local.data.url, token, local.data.pin, local.data);
        } else engine = await this.addEngine(address, token, req.pin ?? null);
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
