import type { components as E } from '../generated/engine';
import type { components as A } from '../generated/agent';
import type { Backup, Device, Host, HostJob, HostUpdate, HostUser, HostVm, Job, JobKind, Lease, Power, Profile, Secret, VaultEvent, VaultRequest, Vm, WinGuest, WinKey } from '../types';

/* From what the agent and the engines report to the pieces of `State`. Pure functions, no I/O: each takes
   the responses it needs and leaves out what they do not say. */

export type VmResponse = E['schemas']['VmResponse'];
export type JobResponse = E['schemas']['JobResponse'];
export type JobProgress = E['schemas']['JobProgressResponse'];
export type ProvisionStatus = E['schemas']['ProvisionStatusResponse'];
export type VmUpdateStatus = E['schemas']['VmUpdateStatusResponse'];
export type BackupSummary = E['schemas']['BackupSummaryResponse'];
export type BackupSettings = E['schemas']['BackupSettingsResponse'];
export type WhoAmI = E['schemas']['WhoAmIResponse'];
export type Health = E['schemas']['HealthResponse'];
export type UserDetail = E['schemas']['UserDetailResponse'];
export type TokenSummary = E['schemas']['UserTokenSummaryResponse'];
export type AuditEntry = E['schemas']['AuditResponse'];
export type MediaItem = E['schemas']['MediaItemResponse'];
export type UsageSummary = E['schemas']['TokenUsageSummary'];
export type HttpsStatus = E['schemas']['HttpsStatusResponse'];
export type HostUpdateStatus = E['schemas']['HostUpdateStatusResponse'];
export type EngineView = A['schemas']['EngineView'];
export type DeviceItem = A['schemas']['DeviceListItem'];
export type SecretView = A['schemas']['SecretView'];
export type SecretHistoryItem = A['schemas']['SecretHistoryItem'];
export type ApprovalItem = A['schemas']['VaultApprovalItem'];
export type EntryRequestItem = A['schemas']['VaultEntryRequestItem'];
export type ProfileResponse = E['schemas']['ProfileResponse'];

/** `2026-11-13T19:18:40.85+00:00` → `2026-11-13 19:18` (UTC, as the engine stores it). */
export function when(iso: string | null | undefined): string {
  if (!iso) return '–';
  const d = new Date(iso);
  if (isNaN(d.getTime()) || d.getUTCFullYear() < 2000) return '–';
  return d.toISOString().slice(0, 16).replace('T', ' ');
}

export function bytes(n: number | null | undefined): string {
  if (n === null || n === undefined) return '–';
  if (n >= 1e9) return `${(n / 1e9).toFixed(1)} GB`;
  if (n >= 1e6) return `${Math.round(n / 1e6)} MB`;
  return `${Math.round(n / 1e3)} kB`;
}

const GB = 1024 ** 3;
const gb = (n: number | null | undefined) => (n === null || n === undefined ? null : Math.round((n / GB) * 10) / 10);

export function power(state: VmResponse['state']): Power {
  return state === 'running' ? 'on' : 'off';
}

export const vmKey = (engine: string, name: string) => `${engine}/${name}`;
export const jobKey = (engine: string, id: string) => `${engine}|${id}`;
export function splitJobKey(key: string): [string, string] {
  const i = key.indexOf('|');
  return [key.slice(0, i), key.slice(i + 1)];
}

/** Applied commit, short, as the VM's version; the engine reports no version name per VM. */
export const shortCommit = (c: string | null | undefined) => (c ? c.slice(0, 7) : '–');

export function hostVersion(h: Health | null): string {
  return h?.packageVersion ?? (h?.commit ? shortCommit(h.commit) : '–');
}

export interface VmParts {
  status?: ProvisionStatus | null;
  update?: VmUpdateStatus | null;
  backups?: BackupSummary[] | null;
  backupSettings?: BackupSettings | null;
  problems?: Vm['problems'];
}

/** One of the user's primaries, with what its engine said about it. `id` is chosen by the caller. */
export function toVm(id: string, engine: string, r: VmResponse, all: VmResponse[], hostVersionText: string, p: VmParts): Vm {
  const applied = p.status?.applied ?? null;
  const ru = r.resourceUsage;
  const updateTo = p.update?.hostUpdate ? p.update.hostUpdate.version : p.update?.newerPayload || p.status?.newerPayload ? hostVersionText : null;
  const idle = r.idlePolicy;
  return {
    id,
    name: r.name,
    hostId: engine,
    power: power(r.state),
    address: r.publicHost,
    sshPort: r.sshForwardPort ?? null,
    os: '',
    installed: when(applied?.installedAt ?? r.created),
    reprovisioned: when(applied?.reprovisionedAt),
    version: shortCommit(applied?.commit),
    changeWaits: p.status?.settingsChanged ? 'Settings' : null,
    settings: {
      hw: { memGb: p.status?.pendingRamGb ?? r.pendingRamGb ?? r.ramGb, cpus: p.status?.pendingCpu ?? r.pendingCpu ?? r.cpu, checkpoints: false, startWithHost: false },
      disk: { diskGb: r.diskGb, ubuntu: '' },
      idle: { afterMin: idle.timeoutMinutes, then: idle.action === 'off' ? 'never' : idle.action },
      services: { t3Web: false, vscodeWeb: false, vscodeTunnel: false, smb: false, mic: false, claudeLive: false, opencodeWatcher: false, t3ProxyUrl: '' },
      identity: { gitName: '', gitEmail: '', keepCredentials: false, historyDays: '30' },
    },
    appliedHw: { memGb: r.ramGb, cpus: r.cpu },
    usage: {
      cpuPct: ru?.cpuUsagePercent ?? null,
      memUsedGb: gb(ru?.memoryDemandBytes ?? ru?.memoryAssignedBytes),
      diskUsedGb: gb(ru?.diskFileBytes),
    },
    memTotalGb: r.ramGb,
    diskTotalGb: r.diskGb,
    agents: [],
    children: all.filter(c => c.kind === 'child' && c.parent === r.name).map(c => ({
      name: c.name, os: '', left: c.lease?.expiresAt ? when(c.lease.expiresAt) : '–', power: power(c.state),
    })),
    projectsOn: [],
    forwards: [],
    sharing: { vscodeWeb: false, smb: false, mic: false },
    backups: (p.backups ?? []).map(toBackup),
    backupSchedule: { everyH: p.backupSettings?.effective.intervalHours ?? 0, keep: p.backupSettings?.effective.keep ?? 0 },
    updateTo,
    problems: p.problems,
  };
}

const TRIGGER: Record<string, Backup['kind']> = { scheduled: 'Scheduled', manual: 'Requested', 'pre-reinstall': 'Before reinstall', slot: 'Slot' };

export function toBackup(b: BackupSummary): Backup {
  return {
    id: b.id,
    at: when(b.createdAt),
    kind: TRIGGER[b.kind === 'slot' ? 'slot' : b.trigger] ?? 'Requested',
    size: bytes(b.sizeBytes),
    files: b.fileCount ?? null,
    pinned: b.pinned,
    protected: b.protected,
  };
}

/** The steps of the provision, reinstall and restore jobs (ProvisionJob.Steps, ReinstallJob.Steps) in the words the UI shows. */
const PROVISION_STEPS: [string, string][] = [
  ['source', 'Source'], ['copy', 'Copying'], ['provision', 'Provisioning'], ['restore', 'Restoring'], ['project-commands', 'Project commands'], ['applied', 'Checking'], ['finish', 'Finishing'],
];
const REINSTALL_STEPS: [string, string][] = [['backup', 'Backing up'], ['delete', 'Deleting'], ['create', 'Creating'], ...PROVISION_STEPS];
const RESTORE_STEPS: [string, string][] = [['restore', 'Restoring']];
const STEPS: Record<string, [string, string][]> = { provision: PROVISION_STEPS, reinstall: REINSTALL_STEPS, restore: RESTORE_STEPS };

const JOB_KINDS: Record<string, JobKind> = { provision: 'reprovision', 'create-vm': 'create', reinstall: 'reinstall', restore: 'restore' };
/** Engine job kinds the VM pages show. */
export const VM_JOB_KINDS = Object.keys(JOB_KINDS);

/** A job of one of the user's VMs, or null for kinds the VM pages do not show. `as` overrides the kind for a
    job this UI started as an update. */
export function toJob(key: string, vmId: string, j: JobResponse, as?: JobKind): Job | null {
  const kind = as ?? JOB_KINDS[j.kind];
  if (!kind) return null;
  const state: Job['state'] = j.state === 'succeeded' ? 'done' : j.state === 'failed' ? 'failed' : j.state === 'cancelled' ? 'stopped' : 'running';
  const withStep = [...j.progress].reverse().find(p => p.step);
  const pctLine = [...j.progress].reverse().find(p => p.percent !== null && p.percent !== undefined);
  const failed = state === 'failed' ? failedStepOf(j.result) : null;
  let step: string, stepIndex: number, stepCount: number, pct: number;
  const steps = STEPS[j.kind];
  if (steps) {
    const name = failed ?? withStep?.step ?? j.phase ?? steps[0][0];
    const at = Math.max(0, steps.findIndex(s => s[0] === name));
    step = steps[at][1];
    stepIndex = at;
    stepCount = steps.length;
    pct = pctLine?.percent ?? Math.round((at / stepCount) * 100);
  } else {
    step = j.phase ?? j.progress.at(-1)?.text ?? '';
    stepIndex = 0;
    stepCount = 1;
    pct = pctLine?.percent ?? 0;
  }
  if (state === 'done') pct = 100;
  return {
    id: key, vmId, kind, state, step, stepIndex, stepCount, pct,
    log: j.progress.map(p => p.text),
    error: j.error ?? null,
    repair: null,
  };
}

/** The step a failed job names in its result (`failedStep`), if any. */
function failedStepOf(result: unknown): string | null {
  const r = result as Record<string, unknown> | null;
  return r && typeof r === 'object' && typeof r.failedStep === 'string' ? r.failedStep : null;
}

export function toHostJob(j: JobResponse): HostJob {
  return {
    id: j.id,
    kind: j.kind,
    vm: j.vmName ?? '–',
    owner: j.initiator ?? '–',
    state: j.state === 'failed' || j.state === 'cancelled' ? 'failed' : j.state === 'succeeded' ? 'done' : 'running',
    phase: j.phase ?? j.state,
    created: when(j.created),
  };
}

export function toHostVm(r: VmResponse): HostVm {
  const ru = r.resourceUsage;
  const mode = r.network?.mode;
  return {
    name: r.name,
    owner: r.owner,
    role: r.kind === 'child' ? 'child' : 'primary',
    parent: r.parent ?? undefined,
    power: power(r.state),
    cpuPct: ru?.cpuUsagePercent ?? 0,
    memGb: gb(ru?.memoryAssignedBytes) ?? 0,
    memMaxGb: r.ramGb,
    diskGb: r.diskGb,
    cpus: r.cpu,
    network: mode === 'relayed' || mode === 'direct' ? mode : 'default',
    address: r.observed?.addresses?.[0]?.address ?? r.publicHost,
    lease: r.lease?.expiresAt ? when(r.lease.expiresAt) : 'no expiry',
    // `shared` is the caller's access to someone else's VM; the VM's own setting is its sharing scope.
    shared: r.sharing === 'host',
    tokensToday: String(r.tokenUsage?.today.tokens ?? 0),
  };
}

export function toHostUser(u: UserDetail, tokens: TokenSummary[]): HostUser {
  const a = u.allowance, e = u.effective;
  const lifetime = e.maxChildLifetimeSeconds;
  return {
    name: u.name,
    role: u.role,
    enabled: u.enabled,
    maxVms: u.maxVms,
    hostForwards: u.allowHostForwards ? 'allowed' : 'refused',
    httpsForwards: !!u.allowHttpsForwards,
    nested: u.allowNested === true ? 'on' : u.allowNested === false ? 'off' : 'inherit',
    allowance: {
      childVms: a.allowChildCreation ?? e.allowChildCreation,
      maxChildren: a.maxRetainedChildren ?? e.maxRetainedChildren,
      cpu: a.cpuBudget ?? null,
      ramGb: a.ramBudgetBytes === null || a.ramBudgetBytes === undefined ? null : Math.round(a.ramBudgetBytes / GB),
      diskGb: a.storageBudgetBytes === null || a.storageBudgetBytes === undefined ? null : Math.round(a.storageBudgetBytes / GB),
      maxLifetime: lifetime === null || lifetime === undefined ? '' : `${Math.round(lifetime / 3600)}h`,
      neverLifetime: a.allowNeverLifetime ?? e.allowNeverLifetime,
      sharing: a.allowSharing ?? e.allowSharing,
    },
    primaries: u.vms.primaries,
    children: u.vms.children,
    tokensMonth: String(u.usageTokensMonth ?? 0),
    tokens: tokens.map(t => ({ id: t.id, label: t.label, created: when(t.created), lastUsed: when(t.lastUsed) })),
  };
}

/** The stored allowance from the edited user; durations as `<n>h` or `<n>d`, empty for the host default. */
export function allowanceOf(u: HostUser): E['schemas']['UserAllowance'] {
  const m = /^(\d+)\s*([hd])$/.exec(u.allowance.maxLifetime.trim());
  return {
    allowChildCreation: u.allowance.childVms,
    maxRetainedChildren: u.allowance.maxChildren,
    cpuBudget: u.allowance.cpu,
    ramBudgetBytes: u.allowance.ramGb === null ? null : u.allowance.ramGb * GB,
    storageBudgetBytes: u.allowance.diskGb === null ? null : u.allowance.diskGb * GB,
    maxChildLifetimeSeconds: m ? +m[1] * (m[2] === 'd' ? 86400 : 3600) : null,
    allowNeverLifetime: u.allowance.neverLifetime,
    allowSharing: u.allowance.sharing,
  };
}

export function toMedia(m: MediaItem): Host['media'][number] {
  return { name: m.name, owner: m.owner, role: m.role, state: m.state, size: bytes(m.sizeBytes), refs: m.references, created: when(m.created) };
}

export function toUsage(u: UsageSummary, window: Host['usage']['window']): Host['usage'] {
  const n = (x: number) => Math.round(x).toLocaleString('en-US');
  const usd = (x: number) => `$${x.toFixed(2)}`;
  return {
    window,
    byUser: u.byUser.map(x => ({ user: x.user, vms: x.vms, tokens: n(x.tokens), cost: usd(x.costUsd) })),
    byVm: u.byVm.map(x => ({ vm: x.vm, owner: x.user, tokens: n(x.tokens), cost: usd(x.costUsd) })),
  };
}

export function toCert(h: HttpsStatus, now: Date): Host['cert'] {
  const c = h.certificate;
  if (!c) return null;
  return {
    subject: c.subject,
    validUntil: when(c.notAfter).slice(0, 10),
    daysLeft: Math.floor((new Date(c.notAfter).getTime() - now.getTime()) / 86400000),
    sha256: c.sha256,
    issuer: c.issuer,
  };
}

type Loose = Record<string, unknown>;
const str = (v: unknown) => (typeof v === 'string' ? v : v === null || v === undefined ? '' : String(v));

/** `GET /host/config`: one section per key; `source` and `updatedAt` are the engine's, not the section's. */
export function toConfig(cfg: Loose): Host['config'] {
  return Object.entries(cfg).filter(([, v]) => v && typeof v === 'object').map(([name, v]) => {
    const { source: _s, updatedAt, ...rest } = v as Loose;
    return { name, json: JSON.stringify(rest, null, 2), updated: updatedAt ? when(str(updatedAt)) : 'default' };
  });
}

/** `GET /host/status` is not typed in the contract; read what it has. */
export function toStatus(st: Loose | null): Pick<Host, 'health' | 'capacity'> {
  const h = (st?.health ?? {}) as Loose;
  const cap = (st?.capacity ?? {}) as Loose;
  const ram = (cap.ram ?? {}) as Loose, cpu = (cap.cpu ?? {}) as Loose;
  const vols = (Array.isArray(cap.volumes) ? cap.volumes : []) as Loose[];
  const num = (v: unknown) => (typeof v === 'number' ? v : 0);
  const tb = (n: number) => Math.round((n / 1024 ** 4) * 100) / 100;
  const total = vols.reduce((s, v) => s + num(v.totalBytes), 0), free = vols.reduce((s, v) => s + num(v.freeBytes), 0);
  return {
    health: { hypervisor: str(h.hypervisor) || '–', database: str(h.database) || '–', media: str(h.media) || '–', inventory: str(h.inventory) || '–' },
    capacity: {
      mode: st?.capacityMode === 'enforce' ? 'enforce' : 'observe',
      ram: [gb(num(ram.committedBytes)) ?? 0, gb(num(ram.totalBytes)) ?? 0],
      cpu: [num(cpu.active), num(cpu.logical)],
      disk: [tb(total - free), tb(total)],
    },
  };
}

/** Host update states (HostUpdateState, camelCase) while an update is under way. */
const UPDATE_RUNNING = new Set(['checking', 'staged', 'draining', 'handedOff', 'applying']);
export const updateRunning = (row: unknown) => !!row && UPDATE_RUNNING.has(str((row as Loose).state));

/** The status route's history rows: `{updateId, commit, state, phase, started, finished, error}`. */
export function toUpdates(u: HostUpdateStatus): HostUpdate[] {
  return (u.history as Loose[]).map(x => {
    const st = str(x.state);
    return {
      at: when(str(x.started)),
      from: '–',
      to: shortCommit(str(x.commit)),
      state: st === 'succeeded' ? 'done' : st.startsWith('rolledBack') ? 'rolled back' : UPDATE_RUNNING.has(st) ? 'running' : 'failed',
      by: '–',
    };
  });
}

/** `GET /host/windows` is not typed in the contract; the records are WindowsKeyInfo and WindowsGuestStatus. */
export function toWindows(w: Loose | null): { winKeys: WinKey[]; winGuests: (WinGuest & { incarnation: string })[] } {
  const keys = (Array.isArray(w?.keys) ? w!.keys : []) as Loose[];
  // A released guest is an earlier incarnation of a VM that was deleted; it holds no key and takes no action.
  const guests = ((Array.isArray(w?.guests) ? w!.guests : []) as Loose[]).filter(g => g.released !== true);
  const held = new Set(guests.map(g => str(g.keyId)).filter(Boolean));
  const kind = (k: string): WinKey['kind'] => (/mak/i.test(k) ? 'MAK' : /kms/i.test(k) ? 'KMS' : 'Retail');
  return {
    winKeys: keys.map(k => ({ id: str(k.id), product: str(k.product), edition: str(k.edition), kind: kind(str(k.kind)), partial: str(k.partialKey), used: str(k.used), held: held.has(str(k.id)) })),
    winGuests: guests.map(g => ({
      name: str(g.vmName),
      stage: str(g.stage),
      activation: g.activation === 'activated' ? 'activated' : g.activation === 'grace' ? 'grace' : 'not activated',
      keyId: str(g.keyId) || null,
      incarnation: str(g.incarnation),
    })),
  };
}

export function toDevice(d: DeviceItem): Device {
  return { id: d.id, name: d.name, kind: '–', added: when(d.added).slice(0, 10), seen: '–', me: d.me, revoked: d.revoked };
}

/** `{type, title, status, detail, code}` from the engine, `{title, status, code}` from the agent. */
export function problemText(e: unknown, fallback = 'Failed'): string {
  if (e && typeof e === 'object') {
    const p = e as Loose;
    if (typeof p.detail === 'string' && p.detail) return p.detail;
    if (typeof p.title === 'string' && p.title) return p.title;
    if (typeof p.message === 'string' && p.message) return p.message;
  }
  return fallback;
}

/** `ABCD-EFGH-...` or `ABCD EFGH ...` → the groups. */
export const codeGroups = (code: string) => code.split(/[\s-]+/).filter(Boolean);

/* --- the key vault --- */

/** A request's key in the state: the engine that holds it and its id there. */
export const requestKey = (engine: string, id: string) => `${engine}/${id}`;
export function splitRequestKey(key: string): [string, string] {
  const i = key.indexOf('/');
  return [key.slice(0, i), key.slice(i + 1)];
}

/** Unix milliseconds → `2026-11-13 19:18`. */
const whenMs = (ms: number) => when(new Date(ms).toISOString());

/** Time left until `ms`, in the largest unit: `5 h`, `12 min`. */
export function left(ms: number, now: number): string {
  const s = Math.max(0, Math.round((ms - now) / 1000));
  if (s >= 2 * 86400) return `${Math.round(s / 86400)} d`;
  if (s >= 2 * 3600) return `${Math.round(s / 3600)} h`;
  return `${Math.max(1, Math.round(s / 60))} min`;
}

export function toSecret(v: SecretView): Secret {
  return { name: v.name, note: v.description, usedBy: [...new Set(v.leases.map(l => l.vm))], last: whenMs(v.updatedAt), auto: v.autoApprove };
}

/** Leases have no id the agent can end, so they carry engine, VM and secret. */
export function toLeases(views: SecretView[], now: number): Lease[] {
  return views.flatMap(v => v.leases.map(l => ({
    id: `${l.engine}/${l.vm}/${l.name}`, secret: l.name, vmName: `${l.vm} · ${l.engineName}`, reason: '',
    left: l.usesLeft !== null && l.usesLeft !== undefined ? `${l.usesLeft} uses` : left(l.expiresAt, now),
  })));
}

const ACTIONS: Record<string, string> = { added: 'Added', changed: 'Changed', deleted: 'Deleted' };

export function toVaultEvent(h: SecretHistoryItem, devices: Device[]): VaultEvent {
  const p = h.provenance;
  const by = p?.vm ?? (p?.device ? devices.find(d => d.id === p.device)?.name ?? p.device : null) ?? h.actor;
  return { at: when(h.at), vmName: by, secret: h.name, event: ACTIONS[h.action] ?? h.action };
}

/** An approval: the engine's own words (the action) apart from the agent's (the reason). */
export function toApproval(a: ApprovalItem, vmId: string): VaultRequest {
  return {
    id: requestKey(a.engine, a.id), kind: 'lease', secret: a.names.join(', '), vmId, vmName: a.vm, hostName: a.engineName,
    reason: a.reason, span: '', action: a.action,
  };
}

/** A request to enter a secret: the description and the reason are the agent's. */
export function toEntryRequest(r: EntryRequestItem, vmId: string): VaultRequest {
  const words = [r.description, r.reason].filter(x => x && x.trim()).join(' — ');
  return { id: requestKey(r.engine, r.id), kind: 'enter', secret: r.name, vmId, vmName: r.vm, hostName: r.engineName, reason: words, span: '' };
}

/* --- profiles --- */

const arr = (v: unknown) => (Array.isArray(v) ? v : []);

/** A profile document as the screens show it; `doc` keeps the document itself for the editor. */
export function toProfile(r: ProfileResponse, conflict: boolean): Profile {
  const d = (r.profile ?? {}) as Loose;
  const sdks = d.sdks && typeof d.sdks === 'object' && !Array.isArray(d.sdks) ? d.sdks as Loose : {};
  return {
    name: r.name,
    revision: r.revision,
    conflict,
    doc: d,
    repos: arr(d.repos).map(x => ({ url: str((x as Loose)?.url), directory: str((x as Loose)?.directory) })),
    sdks: Object.fromEntries(Object.entries(sdks).map(([k, v]) => [k, Array.isArray(v) ? v.join(' ') : str(v)])),
    mcp: arr(d.mcp).map(x => {
      const m = (x ?? {}) as Loose;
      return { name: str(m.name), type: str(m.type), command: str(m.command ?? m.url), args: arr(m.args).map(str) };
    }),
    hostPackages: arr(d.hostPackages).map(str),
    provisionCommands: arr(d.provisionCommands).map(str),
    forwards: arr(d.forwards).map(x => ({ port: Number((x as Loose)?.port) || 0, label: str((x as Loose)?.label) })),
  };
}
