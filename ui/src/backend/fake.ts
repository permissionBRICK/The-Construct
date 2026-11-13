import type { Backend, CreateVmRequest, Host, HostProbe, HostUser, Job, JobKind, Prefs, Profile, ProfileSave, SettingsPatch, State, Vm } from './types';
import { NEXT_RELEASE, PAIR_WORDS, RECOVERY_CODE, REINSTALL_STEPS, REPROVISION_STEPS, sampleState } from './sample';
import { activeJob, newer, vmUpdateTarget } from '../model/vm';
import { applies, applyPatch } from '../model/settings';
import { updateParts } from '../model/update';

/* An in-memory backend with invented data. It simulates jobs with progress, a failure with the repair agent's
   chat, vault requests, an agent asking for a new secret, an update of everything, pairing and revoking.

   Scenes put it into one state at start, for links and screenshots. With `timers: false` nothing moves on its
   own, so a scene stays exactly as it is. */

export type Scene =
  | 'calm' | 'update' | 'changed' | 'lease' | 'enter' | 'job' | 'failed' | 'failed-reinstall' | 'repair' | 'updating' | 'restart'
  | 'pairing' | 'pair-confirm' | 'revoked' | 'handout' | 'creating'
  | 'upgrade-intro' | 'upgrade-code' | 'upgrade-convert' | 'upgrade-done';

export const SCENES: Scene[] = ['calm', 'update', 'changed', 'lease', 'enter', 'job', 'failed', 'failed-reinstall', 'repair', 'updating', 'restart', 'pairing', 'pair-confirm', 'revoked', 'handout', 'creating', 'upgrade-intro', 'upgrade-code', 'upgrade-convert', 'upgrade-done'];

/** Things the preview frame can make happen, beside the user's own actions. */
export type Demo = 'lease' | 'enter' | 'release' | 'fail-next' | 'change';

export interface FakeOptions {
  scene?: Scene;
  /** false: nothing advances by itself (screenshots, unit tests that step by hand). Default true. */
  timers?: boolean;
  /** Milliseconds per simulation tick. Default 600. */
  tick?: number;
}

type Draft = State;

export class FakeBackend implements Backend {
  private s: State;
  private subs = new Set<() => void>();
  private timers: boolean;
  private tickMs: number;
  private seq = 1;
  private failNext = false;
  private pending: ReturnType<typeof setTimeout>[] = [];

  constructor(opts: FakeOptions = {}) {
    this.s = sampleState();
    this.timers = opts.timers ?? true;
    this.tickMs = opts.tick ?? 600;
    if (opts.scene) this.applyScene(opts.scene);
  }

  /* --- plumbing --- */

  snapshot(): State { return this.s; }
  subscribe(fn: () => void) { this.subs.add(fn); return () => { this.subs.delete(fn); }; }

  private set(fn: (d: Draft) => void, notice?: string) {
    const d = structuredClone(this.s);
    fn(d);
    if (notice) d.notice = { id: this.seq++, text: notice };
    this.s = d;
    for (const f of this.subs) f();
  }

  private after(steps: number, fn: () => void) {
    if (!this.timers) return;
    this.pending.push(setTimeout(fn, steps * this.tickMs));
  }

  /** Stops every running simulation timer. */
  dispose() { this.pending.forEach(clearTimeout); this.pending = []; this.subs.clear(); }

  private vm(d: Draft, id: string): Vm {
    const v = d.vms.find(x => x.id === id);
    if (!v) throw new Error('no VM ' + id);
    return v;
  }
  private host(d: Draft, id: string): Host {
    const h = d.hosts.find(x => x.id === id);
    if (!h) throw new Error('no host ' + id);
    return h;
  }
  private job(d: Draft, id: string): Job | undefined { return d.jobs.find(j => j.id === id); }
  private vmName(id: string) { return this.s.vms.find(v => v.id === id)?.name ?? id; }

  /* --- scenes --- */

  private applyScene(scene: Scene) {
    const t = this.timers;
    this.timers = false;   // a scene is a still picture; timers start with the next user action
    const d = this.s;
    switch (scene) {
      case 'calm': break;
      case 'update': d.release = NEXT_RELEASE; break;
      case 'changed': this.vm(d, 'atlas').changeWaits = 'webshop'; break;
      case 'lease': d.vaultRequests.push({ id: 'r1', kind: 'lease', secret: 'forge-token', vmId: 'atlas', vmName: 'atlas', hostName: 'This PC', reason: 'Push the release', span: '2 h' }); break;
      case 'enter': d.vaultRequests.push({ id: 'r2', kind: 'enter', secret: 'payments-sandbox', vmId: 'atlas', vmName: 'atlas', hostName: 'This PC', reason: 'API key of the payments sandbox', span: '' }); break;
      case 'job': d.jobs.push(this.newJob('atlas', 'reprovision', 3, 62)); break;
      case 'failed': case 'repair': {
        const j = this.newJob('atlas', 'reprovision', 3, 48);
        this.failJob(j);
        if (scene === 'failed') j.repair!.messages = j.repair!.messages.slice(0, 1);
        else { j.repair!.messages.push(...REPAIR_SCRIPT.slice(1, 4).map(text => ({ from: 'agent' as const, text }))); j.repair!.state = 'asking'; }
        d.jobs.push(j);
        break;
      }
      case 'failed-reinstall': {
        const j = this.newJob('atlas', 'reinstall', 2, 30);
        this.failJob(j);
        j.repair = null;
        d.jobs.push(j);
        break;
      }
      case 'updating': {
        d.release = NEXT_RELEASE;
        const j = this.newJob('atlas', 'update', 1, 35);
        j.from = '2.4.0'; j.to = NEXT_RELEASE;
        d.jobs.push(j);
        break;
      }
      case 'restart': this.vm(d, 'atlas').settings.hw.memGb = 32; break;
      case 'pairing': d.pairing = { state: 'showing', words: PAIR_WORDS, expiresInS: 582 }; break;
      case 'pair-confirm': d.pairing = { state: 'confirm', words: PAIR_WORDS, deviceName: 'Tablet' }; break;
      case 'revoked': {
        const dev = d.devices.find(x => x.id === 'd2')!;
        dev.revoked = true;
        d.revocation = { deviceId: 'd2', deviceName: dev.name, hosts: d.hosts.map((h, i) => ({ name: h.name, confirmed: i !== 1 })), rotate: ROTATE.map(r => ({ ...r, done: false })) };
        d.revocation.rotate[0].done = true;
        break;
      }
      case 'handout': d.handout = { hostId: 'orchard', user: 'kai', address: 'https://orchard.example.net:8443', token: 'cst_9QmT4vXk2LrP8sWb6NyHd3JcFz' }; break;
      case 'creating': {
        d.vms.push(newVm('quill', 'pc', 'quill.example.net'));
        const j = this.newJob('quill', 'create', 2, 30);
        d.jobs.push(j);
        break;
      }
      case 'upgrade-intro': d.upgrade = { step: 'intro', code: RECOVERY_CODE, items: UPGRADE_ITEMS() }; break;
      case 'upgrade-code': d.upgrade = { step: 'code', code: RECOVERY_CODE, items: UPGRADE_ITEMS() }; break;
      case 'upgrade-convert': d.upgrade = { step: 'convert', code: RECOVERY_CODE, items: UPGRADE_ITEMS(3) }; break;
      case 'upgrade-done': d.upgrade = { step: 'done', code: RECOVERY_CODE, items: UPGRADE_ITEMS(6) }; break;
    }
    this.timers = t;
  }

  /** Preview frame triggers. */
  demo(what: Demo) {
    switch (what) {
      case 'lease': return this.set(d => { d.vaultRequests.push({ id: 'r' + this.seq++, kind: 'lease', secret: 'forge-token', vmId: 'atlas', vmName: 'atlas', hostName: 'This PC', reason: 'Push the release', span: '2 h' }); });
      case 'enter': return this.set(d => { d.vaultRequests.push({ id: 'r' + this.seq++, kind: 'enter', secret: 'payments-sandbox', vmId: 'harbor', vmName: 'harbor', hostName: 'orchard', reason: 'API key of the payments sandbox', span: '' }); });
      case 'release': return this.set(d => { d.release = NEXT_RELEASE; });
      case 'fail-next': this.failNext = true; return this.set(() => {}, 'The next job fails');
      case 'change': return this.set(d => { this.vm(d, 'atlas').changeWaits = 'webshop'; });
    }
  }

  /* --- jobs --- */

  private newJob(vmId: string, kind: JobKind, stepIndex = 0, pct = 0): Job {
    const steps = kind === 'reinstall' ? REINSTALL_STEPS : kind === 'update' ? UPDATE_STEPS : kind === 'create' ? CREATE_STEPS : kind === 'restore' ? RESTORE_STEPS : REPROVISION_STEPS;
    return {
      id: 'j' + this.seq++, vmId, kind, state: 'running', step: steps[stepIndex], stepIndex, stepCount: steps.length, pct,
      log: steps.slice(0, stepIndex + 1).map((s, i) => (i < stepIndex ? `ok   ${s}` : `run  ${s}`)), error: null, repair: null,
    };
  }

  private failJob(j: Job) {
    j.state = 'failed';
    j.error = 'apt is locked by another process';
    j.log.push(`fail ${j.step}: E: Could not get lock /var/lib/dpkg/lock-frontend`);
    j.repair = { agent: 'Claude Code', state: 'working', messages: [{ from: 'agent', text: REPAIR_SCRIPT[0] }], t3Thread: true };
  }

  private startJob(vmId: string, kind: JobKind, extra?: Partial<Job>): string | null {
    if (activeJob(this.s, vmId)?.state === 'running') return null;
    const j = { ...this.newJob(vmId, kind), ...extra };
    const fail = this.failNext;
    this.failNext = false;
    this.set(d => {
      d.jobs = d.jobs.filter(x => !(x.vmId === vmId && x.state === 'failed'));
      d.jobs.push(j);
    });
    this.advance(j.id, fail);
    return j.id;
  }

  /* Jobs that run one after the other (Update everything, Update all VMs on a host). A queued job waits
     while its VM is busy with another job. */
  private queue: { vmId: string; kind: JobKind; extra?: Partial<Job> }[] = [];
  private queuedRunning: string | null = null;
  private drain() {
    const cur = this.queuedRunning && this.s.jobs.find(j => j.id === this.queuedRunning);
    if (cur && cur.state === 'running') return;
    this.queuedRunning = null;
    const i = this.queue.findIndex(q => activeJob(this.s, q.vmId)?.state !== 'running');
    if (i < 0) return;
    const [q] = this.queue.splice(i, 1);
    this.queuedRunning = this.startJob(q.vmId, q.kind, q.extra);
  }

  private advance(jobId: string, fail: boolean) {
    this.after(1, () => {
      const cur = this.s.jobs.find(x => x.id === jobId);
      if (!cur || cur.state !== 'running') return;
      const per = 100 / cur.stepCount;
      this.set(d => {
        const j = this.job(d, jobId)!;
        j.pct = Math.min(100, Math.round(j.pct + per / 2));
        const idx = Math.min(j.stepCount - 1, Math.floor(j.pct / per));
        if (fail && idx >= 3 && j.kind !== 'update') { this.failJob(j); return; }
        if (idx !== j.stepIndex) {
          j.log[j.log.length - 1] = `ok   ${j.step}`;
          j.stepIndex = idx;
          j.step = stepsOf(j.kind)[idx];
          j.log.push(`run  ${j.step}`);
          if (j.kind === 'update') this.updateStep(d, j);
        }
        if (j.pct >= 100) this.finishJob(d, j);
      });
      const after = this.s.jobs.find(x => x.id === jobId);
      if (after?.state === 'running') this.advance(jobId, fail);
      else if (after?.state === 'failed') { this.repairScript(jobId); this.drain(); }
      else if (after?.state === 'done') this.onDone(after);
    });
  }

  private updateStep(d: Draft, j: Job) {
    // device first, then hosts, then the VM
    if (j.step === 'This device' && j.to && j.scope !== 'vm') d.version = j.to;
    if (j.step === 'Host') { const v = this.vm(d, j.vmId); const h = this.host(d, v.hostId); if (h.admin && j.to && newer(j.to, h.version)) h.version = j.to; }
  }

  private finishJob(d: Draft, j: Job) {
    j.state = 'done';
    j.pct = 100;
    j.log[j.log.length - 1] = `ok   ${j.step}`;
    const v = this.vm(d, j.vmId);
    v.reprovisioned = 'just now';
    v.changeWaits = null;
    if (j.kind === 'update' && j.to) v.version = j.to;
    if (j.kind === 'create') { v.power = 'on'; const hv = d.hosts.find(h => h.id === v.hostId)?.vms.find(x => x.name === v.name); if (hv) hv.power = 'on'; }
    if (d.release && !updateParts(d).some(p => p.to)) d.release = null;
  }

  private onDone(j: Job) {
    const verb = { reprovision: 'Reprovisioned', reinstall: 'Reinstalled', update: 'Updated', restore: 'Restored', create: 'Created' }[j.kind];
    this.set(() => {}, `${verb} ${this.vmName(j.vmId)}`);
    this.drain();
  }

  private repairScript(jobId: string) {
    const say = (i: number, then?: () => void) => this.after(3, () => {
      this.set(d => {
        const j = this.job(d, jobId);
        if (!j?.repair || j.state !== 'failed') return;
        j.repair.messages.push({ from: 'agent', text: REPAIR_SCRIPT[i] });
        if (i === 3) j.repair.state = 'asking';
      });
      then?.();
    });
    say(1, () => say(2, () => say(3)));
  }

  /* --- VM actions --- */

  open(vmId: string, what: 't3' | 'vscode' | 'ssh') {
    const name = { t3: 'T3 Code', vscode: 'VS Code', ssh: 'SSH' }[what];
    this.set(() => {}, `${name} opens ${this.vmName(vmId)}`);
  }

  openForward(vmId: string, port: number) { this.set(() => {}, `${this.vmName(vmId)}:${port} opens in the browser`); }
  openChildConsole(_vmId: string, child: string) { this.set(() => {}, `Console of ${child} opens`); }
  setVmPassword(vmId: string, password: string) { if (password.length >= 8) this.set(() => {}, `Password of ${this.vmName(vmId)} changed`); }

  power(vmId: string, on: boolean) {
    this.set(d => { this.vm(d, vmId).power = on ? 'starting' : 'stopping'; });
    const done = () => this.set(d => {
      const v = this.vm(d, vmId);
      v.power = on ? 'on' : 'off';
      if (!on) v.usage.cpuPct = 0;
      const hv = d.hosts.find(h => h.id === v.hostId)?.vms.find(x => x.name === v.name);
      if (hv) hv.power = v.power;
    }, `${this.vmName(vmId)} ${on ? 'runs' : 'is off'}`);
    if (this.timers) this.after(3, done); else done();
  }

  restart(vmId: string) {
    this.set(d => { const v = this.vm(d, vmId); v.appliedHw = { memGb: v.settings.hw.memGb, cpus: v.settings.hw.cpus }; v.memTotalGb = v.settings.hw.memGb; v.power = 'on'; }, `Restarting ${this.vmName(vmId)}`);
  }

  reprovision(vmId: string) { this.startJob(vmId, 'reprovision'); }

  reinstall(vmId: string, _freshImage: boolean) { this.startJob(vmId, 'reinstall'); }

  updateEverything() {
    const s = this.s;
    const to = s.release;
    if (!to) return;
    const due = s.vms.filter(v => vmUpdateTarget(s, v)).map(v => ({ vmId: v.id, from: v.version, to: vmUpdateTarget(s, v)! }));
    // hosts the user administers that have no VM of theirs still update
    this.set(d => {
      if (newer(to, d.version) && !due.length) d.version = to;
      for (const h of d.hosts) if (h.admin && h.channel === d.prefs.channel && newer(to, h.version) && !d.vms.some(v => v.hostId === h.id)) h.version = to;
    }, 'Updating everything');
    this.queue.push(...due.map(x => ({ vmId: x.vmId, kind: 'update' as const, extra: { from: x.from, to: x.to, scope: 'all' as const } })));
    if (due.length) this.drain();
    else this.set(d => { if (!updateParts(d).some(p => p.to)) d.release = null; });
  }

  updateVm(vmId: string) {
    const v = this.s.vms.find(x => x.id === vmId);
    const to = v && vmUpdateTarget(this.s, v);
    if (!v || !to) return;
    this.startJob(vmId, 'update', { from: v.version, to, scope: 'vm' });
  }

  stopJob(jobId: string) {
    this.set(d => { const j = this.job(d, jobId); if (j) j.state = 'stopped'; }, 'Stopped');
    this.drain();
  }

  retryJob(jobId: string) {
    const j = this.s.jobs.find(x => x.id === jobId);
    if (!j) return;
    this.startJob(j.vmId, j.kind, { from: j.from, to: j.to });
  }

  dismissJob(jobId: string) {
    this.set(d => { const j = this.job(d, jobId); if (j) j.state = 'stopped'; });
  }

  replyRepair(jobId: string, text: string) {
    const t = text.trim();
    if (!t) return;
    this.set(d => {
      const j = this.job(d, jobId);
      if (!j?.repair) return;
      j.repair.messages.push({ from: 'user', text: t });
      j.repair.state = 'working';
    });
    const stop = /stop|kill|now/i.test(t);
    this.after(3, () => {
      this.set(d => {
        const j = this.job(d, jobId);
        if (!j?.repair) return;
        j.repair.messages.push({ from: 'agent', text: stop ? 'Stopped unattended-upgrades and removed the lock. Running "Installing tools" again.' : 'Waiting for unattended-upgrades to finish, then running "Installing tools" again.' });
        j.repair.state = 'done';
      });
      this.after(3, () => this.retryJob(jobId));
    });
  }

  saveSettings(vmId: string, patch: SettingsPatch, when?: 'now' | 'later') {
    const a = applies(patch);
    this.set(d => {
      const v = this.vm(d, vmId);
      v.settings = applyPatch(v.settings, patch);
      if (a === 'reprovision') v.changeWaits = v.changeWaits ?? 'Settings';
    }, a === 'reprovision' ? 'Saved, applies at the next reprovision' : a === 'reinstall' ? 'Saved, applies at the next reinstall' : a === 'restart' && when === 'later' ? 'Saved, applies at the next restart' : 'Saved');
    if (a === 'restart' && when === 'now') this.restart(vmId);
  }

  updateAgents(vmId: string) {
    this.set(d => { for (const ag of this.vm(d, vmId).agents) ag.version = ag.latest; }, 'Agents updated');
  }

  setProjectOn(vmId: string, project: string, on: boolean) {
    this.set(d => {
      const v = this.vm(d, vmId);
      v.projectsOn = on ? [...new Set([...v.projectsOn, project])] : v.projectsOn.filter(p => p !== project);
      v.changeWaits = project;
    });
  }

  async saveProfile(name: string, doc: Record<string, unknown>, revision: number | undefined): Promise<ProfileSave> {
    const cur = this.s.projects.find(x => x.name === name);
    if (cur && revision !== undefined && (cur.revision ?? 0) !== revision) return { ok: false, changed: true, text: `${name} changed meanwhile; this is the current version` };
    const p = { ...(doc as unknown as Profile), name, revision: (cur?.revision ?? 0) + 1, doc: undefined };
    this.set(d => {
      const i = d.projects.findIndex(x => x.name === name);
      if (i >= 0) d.projects[i] = p; else d.projects.push(p);
      for (const v of d.vms) if (v.projectsOn.includes(name)) v.changeWaits = name;
    });
    return { ok: true, text: 'Saved' };
  }

  addProject(name: string) {
    const n = name.trim();
    if (!n || this.s.projects.some(p => p.name === n)) return;
    this.set(d => { d.projects.push({ name: n, repos: [], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], forwards: [] }); }, `Added ${n}`);
  }

  syncProjects() { this.set(d => { d.sync.when = 'just now'; }, 'Synced'); }

  closeForward(vmId: string, port: number) {
    this.set(d => { const v = this.vm(d, vmId); v.forwards = v.forwards.filter(f => f.port !== port); }, `Closed ${port}`);
  }

  setSharing(vmId: string, key: keyof Vm['sharing'], on: boolean) {
    this.set(d => { this.vm(d, vmId).sharing[key] = on; });
  }

  backupNow(vmId: string) {
    this.set(d => { this.vm(d, vmId).backups.unshift({ id: 'b' + this.seq++, at: 'Just now', kind: 'Requested', size: '415 MB', files: 48230, pinned: false, protected: false }); }, 'Backed up');
  }

  deleteBackup(vmId: string, backupId: string) {
    this.set(d => { const v = this.vm(d, vmId); v.backups = v.backups.filter(b => b.id !== backupId); }, 'Backup deleted');
  }

  restoreBackup(vmId: string, backupId: string) {
    const b = this.s.vms.find(v => v.id === vmId)?.backups.find(x => x.id === backupId);
    if (b) this.startJob(vmId, 'restore');
  }

  pinBackup(vmId: string, backupId: string, pinned: boolean) {
    this.set(d => { const b = this.vm(d, vmId).backups.find(x => x.id === backupId); if (b) b.pinned = pinned; });
  }

  setBackupSchedule(vmId: string, schedule: Partial<Vm['backupSchedule']>) {
    this.set(d => { const v = this.vm(d, vmId); v.backupSchedule = { ...v.backupSchedule, ...schedule }; });
  }

  createVm(req: CreateVmRequest): string | null {
    const name = req.name.trim();
    if (!name || this.s.vms.some(v => v.name === name)) return null;
    let hostId = 'pc';
    if (req.where === 'remote') {
      const addr = (req.hostAddress ?? '').trim();
      const known = this.s.hosts.find(h => h.address === addr || addr.includes(h.name));
      hostId = known?.id ?? 'h' + this.seq++;
      if (!known) {
        const hn = addr.replace(/^https?:\/\//, '').split(/[.:/]/)[0] || 'host';
        this.set(d => { d.hosts.push({ ...structuredClone(d.hosts.find(h => h.id === 'lab')!), id: hostId, name: hn, address: addr, channel: d.prefs.channel, version: d.version, vmCount: 0 }); });
      }
    }
    this.set(d => {
      d.vms.push(newVm(name, hostId, `${name}.example.net`));
      const h = this.host(d, hostId);
      h.vmCount += 1;
      h.vms.push({ name, owner: 'alex', role: 'primary', power: 'off', cpuPct: 0, memGb: 0, memMaxGb: 8, diskGb: 0, cpus: 4, network: 'default', address: '–', lease: 'no expiry', shared: false, tokensToday: '0' });
    });
    this.startJob(name, 'create');
    return name;
  }

  async probeHost(address: string): Promise<HostProbe> {
    return { url: address.trim(), pin: '4F1C9A22D07B3E615A8C0F9471B2DE5536A0C7E19F84B3260D5E7A1C9B04E07B', known: false };
  }

  /* --- vault --- */

  answerLease(requestId: string, allow: boolean) {
    const r = this.s.vaultRequests.find(x => x.id === requestId);
    if (!r) return;
    this.set(d => {
      d.vaultRequests = d.vaultRequests.filter(x => x.id !== requestId);
      d.history.unshift({ at: 'now', vmName: r.vmName, secret: r.secret, event: allow ? `Allowed ${r.span}` : 'Denied' });
      if (allow) d.leases.unshift({ id: 'l' + this.seq++, secret: r.secret, vmName: r.vmName, reason: r.reason, left: r.span.replace(' h', ':00') });
    }, allow ? `Allowed for ${r.span}` : 'Denied');
  }

  enterSecret(requestId: string, value: string | null) {
    const r = this.s.vaultRequests.find(x => x.id === requestId);
    if (!r) return;
    if (value !== null && this.s.secrets.some(x => x.name === r.secret)) {
      this.set(d => { d.vaultRequests = d.vaultRequests.filter(x => x.id !== requestId); }, `${r.secret} already exists, nothing overwritten`);
      return;
    }
    this.set(d => {
      d.vaultRequests = d.vaultRequests.filter(x => x.id !== requestId);
      if (value !== null) d.secrets.push({ name: r.secret, note: r.reason, usedBy: [], last: '–', auto: false });
    }, value === null ? 'Skipped' : 'Saved in the vault');
  }

  addSecret(name: string, note: string, value: string) {
    const n = name.trim();
    if (!n || !value || this.s.secrets.some(x => x.name === n)) return;
    this.set(d => { d.secrets.push({ name: n, note: note.trim(), usedBy: [], last: '–', auto: false }); }, 'Saved in the vault');
  }

  replaceSecret(name: string, value: string) {
    if (!value || !this.s.secrets.some(x => x.name === name)) return;
    this.set(d => { d.leases = d.leases.filter(l => l.secret !== name); }, `Replaced ${name}`);
  }

  /** An invented value; the fake keeps none. */
  async revealSecret(name: string): Promise<string> { return `${name}-sample-value`; }

  deleteSecret(name: string) { this.set(d => { d.secrets = d.secrets.filter(x => x.name !== name); }, `Deleted ${name}`); }

  setVaultMode(hostId: string, mode: 'available' | 'locked') {
    this.set(d => { const m = d.vaultModes.find(x => x.hostId === hostId); if (m) m.mode = mode; }, 'Saved');
  }

  setAutoApprove(name: string, on: boolean) { this.set(d => { const x = d.secrets.find(s => s.name === name); if (x) x.auto = on; }); }

  takeBack(leaseId: string) { this.set(d => { d.leases = d.leases.filter(l => l.id !== leaseId); }, 'Taken back and scrubbed'); }

  startPairing() {
    this.set(d => { d.pairing = { state: 'showing', words: PAIR_WORDS, expiresInS: 600 }; });
    const tick = (n: number) => this.after(Math.round(1000 / this.tickMs), () => {
      const p = this.s.pairing;
      if (!p || p.state !== 'showing') return;
      if (n >= 6) { this.set(d => { d.pairing = { state: 'confirm', words: PAIR_WORDS, deviceName: 'Tablet' }; }); return; }
      this.set(d => { if (d.pairing?.state === 'showing') d.pairing.expiresInS -= 1; });
      tick(n + 1);
    });
    tick(0);
  }

  confirmPairing(accept: boolean) {
    const p = this.s.pairing;
    if (!p || p.state !== 'confirm') return;
    this.set(d => {
      if (accept) { d.devices.push({ id: 'd' + this.seq++, name: p.deviceName, kind: 'Phone', added: 'today', seen: 'now', me: false, revoked: false }); d.pairing = { state: 'done', deviceName: p.deviceName }; }
      else d.pairing = null;
    }, accept ? `${p.deviceName} paired` : 'Pairing refused');
  }

  cancelPairing() { this.set(d => { d.pairing = null; }); }

  /** The fake shows the words on this device and invents the other side, so there is no code to enter. */
  enterPairingCode() { /* not part of the fake's pairing */ }

  revokeDevice(deviceId: string) {
    const dev = this.s.devices.find(x => x.id === deviceId);
    if (!dev || dev.me) return;
    this.set(d => {
      d.devices.find(x => x.id === deviceId)!.revoked = true;
      d.revocation = { deviceId, deviceName: dev.name, hosts: d.hosts.map((h, i) => ({ name: h.name, confirmed: i === 0 })), rotate: ROTATE.map(r => ({ ...r, done: false })) };
    }, `${dev.name} revoked`);
    this.after(4, () => this.set(d => { if (d.revocation?.deviceId === deviceId) for (const h of d.revocation.hosts) h.confirmed = true; }));
  }

  openRevocation(deviceId: string) {
    const dev = this.s.devices.find(x => x.id === deviceId);
    if (!dev?.revoked || this.s.revocation?.deviceId === deviceId) return;
    this.set(d => { d.revocation = { deviceId, deviceName: dev.name, hosts: d.hosts.map(h => ({ name: h.name, confirmed: true })), rotate: ROTATE.map(r => ({ ...r, done: false })) }; });
  }

  markRotated(secret: string) { this.set(d => { const r = d.revocation?.rotate.find(x => x.secret === secret); if (r) r.done = !r.done; }); }

  closeRevocation() { this.set(d => { d.revocation = null; }); }

  /* --- hosts --- */

  private hostSet(hostId: string, fn: (h: Host, d: Draft) => void, notice?: string) {
    this.set(d => { const h = this.host(d, hostId); if (h.admin) fn(h, d); }, notice);
  }

  updateHost(hostId: string) {
    const to = this.s.release;
    const h = this.s.hosts.find(x => x.id === hostId);
    if (!h || !to || !newer(to, h.version)) return;
    this.hostSet(hostId, x => { x.updating = true; x.updates.unshift({ at: 'now', from: x.version, to, state: 'running', by: 'alex' }); }, `Updating ${h.name}`);
    const done = () => this.hostSet(hostId, (x, d) => {
      x.updating = false; x.version = to; x.updates[0].state = 'done';
      if (!updateParts(d).some(p => p.to)) d.release = null;
    }, `${h.name} runs ${to}`);
    if (this.timers) this.after(5, done); else done();
  }

  updateHostVms(hostId: string) {
    const h = this.s.hosts.find(x => x.id === hostId);
    if (!h) return;
    const own = new Set(this.s.vms.filter(x => x.hostId === hostId).map(v => v.name));
    const others = h.vms.filter(v => !own.has(v.name) && v.role === 'primary').map(v => v.name);
    // other users' VMs run as host jobs; the user's own VMs show their job on their own pages
    const ids = others.map(() => 'j-' + this.seq++);
    this.hostSet(hostId, x => { others.forEach((vm, i) => x.jobs.unshift({ id: ids[i], kind: 'reprovision', vm, owner: x.vms.find(v => v.name === vm)!.owner, state: 'running', phase: 'queued', created: 'now' })); },
      `Reprovisioning every VM on ${h.name}, one after the other`);
    const finish = () => this.hostSet(hostId, x => { for (const j of x.jobs) if (ids.includes(j.id)) { j.state = 'done'; j.phase = 'done'; } });
    if (this.timers) this.after(8, finish); else finish();
    this.queue.push(...this.s.vms.filter(x => x.hostId === hostId).map(v => ({ vmId: v.id, kind: 'reprovision' as const })));
    this.drain();
  }

  addUser(hostId: string, u: { name: string; role: 'admin' | 'user'; maxVms: number; hostForwards: HostUser['hostForwards'] }) {
    const name = u.name.trim();
    const h = this.s.hosts.find(x => x.id === hostId);
    if (!name || !h || h.users.some(x => x.name === name)) return;
    const token = h.signIn === 'token' ? 'cst_' + Math.random().toString(36).slice(2, 12) + Math.random().toString(36).slice(2, 12) : null;
    this.hostSet(hostId, (x, d) => {
      x.users.push({
        name, role: u.role, enabled: true, maxVms: u.maxVms, hostForwards: u.hostForwards, httpsForwards: false, nested: 'inherit',
        allowance: { childVms: true, maxChildren: 1, cpu: null, ramGb: null, diskGb: null, maxLifetime: '3d', neverLifetime: false, sharing: true },
        primaries: 0, children: 0, tokensMonth: '0', tokens: token ? [{ id: 't-' + this.seq++, label: 'first client', created: 'today', lastUsed: '–' }] : [],
      });
      x.audit.unshift({ at: 'now', actor: 'alex', action: 'user.add', target: name });
      d.handout = { hostId, user: name, address: x.address, token };
    }, `Added ${name}`);
  }

  saveUser(hostId: string, u: HostUser) {
    // tokens have their own actions; a save never brings back a revoked one or drops a new one
    this.hostSet(hostId, x => { const i = x.users.findIndex(y => y.name === u.name); if (i >= 0) x.users[i] = { ...u, tokens: x.users[i].tokens }; }, 'Saved');
  }

  removeUser(hostId: string, name: string) {
    this.hostSet(hostId, x => { x.users = x.users.filter(y => y.name !== name); x.audit.unshift({ at: 'now', actor: 'alex', action: 'user.remove', target: name }); }, `Removed ${name}`);
  }

  issueToken(hostId: string, user: string, label: string) {
    const token = 'cst_' + Math.random().toString(36).slice(2, 14);
    this.hostSet(hostId, (x, d) => {
      const u = x.users.find(y => y.name === user);
      if (!u) return;
      u.tokens.push({ id: 't-' + this.seq++, label: label.trim() || 'token', created: 'today', lastUsed: '–' });
      d.handout = { hostId, user, address: x.address, token };
    });
  }

  revokeToken(hostId: string, user: string, tokenId: string) {
    this.hostSet(hostId, x => { const u = x.users.find(y => y.name === user); if (u) u.tokens = u.tokens.filter(t => t.id !== tokenId); }, 'Token revoked');
  }

  closeHandout() { this.set(d => { d.handout = null; }); }

  hostVmAction(hostId: string, vm: string, action: 'start' | 'restart' | 'shutdown' | 'delete' | 'console' | 'rotate-token' | 'toggle-shared' | 'renew-lease') {
    const notes: Record<typeof action, string> = { start: `Starting ${vm}`, restart: `Restarting ${vm}`, shutdown: `Shutting down ${vm}`, delete: `Deleting ${vm}`, console: `Console of ${vm} opens`, 'rotate-token': `New token for ${vm}`, 'toggle-shared': 'Saved', 'renew-lease': 'Lifetime restarted' };
    this.hostSet(hostId, (x, d) => {
      const v = x.vms.find(y => y.name === vm);
      if (!v) return;
      // the same VM as seen in the user's own VM list
      const mine = d.vms.find(y => y.hostId === hostId && y.name === vm);
      const parent = v.parent ? d.vms.find(y => y.hostId === hostId && y.name === v.parent) : undefined;
      if (action === 'start' || action === 'restart') { v.power = 'on'; if (mine) mine.power = 'on'; }
      if (action === 'shutdown') { v.power = 'off'; v.cpuPct = 0; v.memGb = 0; if (mine) { mine.power = 'off'; mine.usage.cpuPct = 0; } }
      if (action === 'delete') {
        x.vms = x.vms.filter(y => y.name !== vm && y.parent !== vm);
        if (mine) { d.vms = d.vms.filter(y => y !== mine); d.jobs = d.jobs.filter(j => j.vmId !== mine.id); }
        if (parent) parent.children = parent.children.filter(c => c.name !== vm);
      }
      if (action === 'toggle-shared') v.shared = !v.shared;
      if (action === 'renew-lease') v.lease = '12 h';
      if (action !== 'console') x.audit.unshift({ at: 'now', actor: 'alex', action: 'vm.' + action, target: vm });
    }, notes[action]);
  }

  saveHostVm(hostId: string, vm: string, patch: Partial<{ cpus: number; memMaxGb: number; network: 'default' | 'relayed' | 'direct' }>) {
    this.hostSet(hostId, x => { const v = x.vms.find(y => y.name === vm); if (v) Object.assign(v, patch); }, 'Saved, applies at the next restart');
  }

  deleteMedia(hostId: string, name: string) { this.hostSet(hostId, x => { x.media = x.media.filter(m => m.name !== name || m.refs > 0); }, `Deleted ${name}`); }
  cleanupMedia(hostId: string) { this.hostSet(hostId, x => { x.jobs.unshift({ id: 'j-' + this.seq++, kind: 'media-cleanup', vm: '–', owner: 'alex', state: 'done', phase: 'done', created: 'now' }); }, 'Cleanup done'); }

  acquireWindows(hostId: string, product: string, edition: string, language: string) {
    this.hostSet(hostId, x => {
      x.media.unshift({ name: `${product.toLowerCase().replace(/\s+/g, '')}-${edition.toLowerCase()}-${language}.iso`, owner: 'alex', role: 'install', state: 'downloading', size: '–', refs: 0, created: 'now' });
    }, 'Download started');
  }

  addWinKey(hostId: string, key: { product: string; edition: string; kind: 'Retail' | 'MAK' | 'KMS'; key: string }) {
    if (key.key.trim().length < 5) return;
    this.hostSet(hostId, x => { x.winKeys.push({ id: 'k' + this.seq++, product: key.product, edition: key.edition, kind: key.kind, partial: '•••••-•••••-•••••-•••••-' + key.key.trim().slice(-5).toUpperCase(), used: '0', held: false }); }, 'Key added');
  }

  removeWinKey(hostId: string, id: string) { this.hostSet(hostId, x => { x.winKeys = x.winKeys.filter(k => k.id !== id || k.held); }); }

  assignWinKey(hostId: string, guest: string, keyId: string) {
    this.hostSet(hostId, x => {
      const g = x.winGuests.find(y => y.name === guest), k = x.winKeys.find(y => y.id === keyId);
      if (!g || !k) return;
      g.keyId = keyId; k.held = true;
    }, `Key assigned to ${guest}`);
  }

  activateWindows(hostId: string, guest: string) {
    this.hostSet(hostId, x => { const g = x.winGuests.find(y => y.name === guest); if (g?.keyId) g.activation = 'activated'; }, `Activating ${guest}`);
  }

  cancelHostJob(hostId: string, jobId: string) { this.hostSet(hostId, x => { const j = x.jobs.find(y => y.id === jobId); if (j) { j.state = 'failed'; j.phase = 'cancelled'; } }, 'Cancelled'); }
  retryHostJob(hostId: string, jobId: string) { this.hostSet(hostId, x => { const j = x.jobs.find(y => y.id === jobId); if (j) { j.state = 'done'; j.phase = 'done'; } }, 'Done'); }

  setUsageWindow(hostId: string, w: 'today' | 'month' | 'all') { this.hostSet(hostId, x => { x.usage.window = w; }); }

  saveConfig(hostId: string, section: string, json: string): string | null {
    try { JSON.parse(json); } catch (e) { return (e as Error).message; }
    this.hostSet(hostId, x => { const c = x.config.find(y => y.name === section); if (c) { c.json = json; c.updated = 'now'; } x.audit.unshift({ at: 'now', actor: 'alex', action: 'config.save', target: section }); }, `Saved ${section}`);
    return null;
  }

  installCertificate(hostId: string, pem: string) {
    if (!/BEGIN CERTIFICATE/.test(pem)) { this.set(() => {}, 'That is not a PEM certificate'); return; }
    this.hostSet(hostId, x => { x.cert = { subject: 'CN=' + x.name + '.example.net', validUntil: '13 Nov 2027', daysLeft: 365, sha256: '9B:03:…:4D:A1', issuer: 'Example Issuing CA' }; }, 'Certificate installed');
  }

  removeCertificate(hostId: string) { this.hostSet(hostId, x => { x.cert = null; }, 'Certificate removed'); }

  /* --- this device --- */

  setPrefs(patch: Partial<Prefs>) { this.set(d => { d.prefs = { ...d.prefs, ...patch }; }); }

  showRecoveryCode() { return RECOVERY_CODE; }
  exportRecoveryCode(how: 'file' | 'print') { this.set(() => {}, how === 'file' ? 'Saved as a file' : 'Printing'); }
  openLog() { this.set(() => {}, 'Log opens'); }

  upgradeNext() {
    const u = this.s.upgrade;
    if (!u) return;
    if (u.step === 'intro') return this.set(d => { d.upgrade!.step = 'code'; });
    if (u.step === 'code') {
      this.set(d => { d.upgrade!.step = 'convert'; d.upgrade!.items = UPGRADE_ITEMS(0); });
      const step = (i: number) => this.after(2, () => {
        this.set(d => { d.upgrade!.items = UPGRADE_ITEMS(i); if (i >= 6) d.upgrade!.step = 'done'; });
        if (i < 6) step(i + 1);
      });
      if (this.timers) step(1); else this.set(d => { d.upgrade!.items = UPGRADE_ITEMS(6); d.upgrade!.step = 'done'; });
      return;
    }
    if (u.step === 'done') this.set(d => { d.upgrade = null; });
  }

  /** The fake starts as a device that is set up already. */
  firstRunStart() { /* no first run in the fake */ }
  firstRunNext() { /* no first run in the fake */ }
}

const UPDATE_STEPS = ['Download', 'This device', 'Host', 'Reprovision'];
const CREATE_STEPS = ['Reserving', 'Installing Ubuntu', 'Installing tools', 'Projects', 'Services'];
const RESTORE_STEPS = ['Fetching the backup', 'Restoring', 'Reprovision'];
const stepsOf = (k: JobKind) => (k === 'reinstall' ? REINSTALL_STEPS : k === 'update' ? UPDATE_STEPS : k === 'create' ? CREATE_STEPS : k === 'restore' ? RESTORE_STEPS : REPROVISION_STEPS);

const REPAIR_SCRIPT = [
  'Reading the log of "Installing tools".',
  'The apt lock belongs to unattended-upgrades, which is installing security updates right now.',
  'Nothing is broken; the step started while the updates ran.',
  'Two ways: wait until the updates finish (a few minutes), or stop them and run the step again now. Which do you want?',
];

const ROTATE = [{ secret: 'forge-token', last: '4 d' }, { secret: 'deploy-key', last: '9 d' }, { secret: 'staging-db', last: '12 d' }];

function UPGRADE_ITEMS(progress = 0) {
  const names: [string, string][] = [['VS Code closed', ''], ['VMs', '4'], ['Projects', '4'], ['Key vault', '5'], ['Backups', '5'], ['VMs to the new engine', '4']];
  return names.map(([name, count], i) => ({ name, count, state: (i < progress ? 'done' : i === progress ? 'running' : 'waiting') as 'done' | 'running' | 'waiting', pct: i === progress ? 60 : i < progress ? 100 : 0 }));
}

function newVm(name: string, hostId: string, address: string): Vm {
  const base = sampleState().vms[1];
  return { ...structuredClone(base), id: name, name, hostId, address, power: 'off', projectsOn: [], forwards: [], backups: [], children: [], changeWaits: null, installed: 'today', reprovisioned: '–', usage: { cpuPct: 0, memUsedGb: 0, diskUsedGb: 0 } };
}
