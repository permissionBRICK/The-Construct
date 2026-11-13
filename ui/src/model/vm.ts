import type { Host, Job, State, Vm } from '../backend/types';

export type Dot = 'on' | 'off' | 'busy' | 'bad';

/** The job shown on a VM: the newest one that runs or failed and was not dismissed. */
export function activeJob(state: State, vmId: string): Job | null {
  for (let i = state.jobs.length - 1; i >= 0; i--) {
    const j = state.jobs[i];
    if (j.vmId === vmId && (j.state === 'running' || j.state === 'failed')) return j;
  }
  return null;
}

export function vmDot(vm: Vm, job: Job | null): Dot {
  if (job?.state === 'running') return 'busy';
  if (job?.state === 'failed') return 'bad';
  if (vm.power === 'starting' || vm.power === 'stopping') return 'busy';
  return vm.power === 'on' ? 'on' : 'off';
}

const JOB_WORD: Record<Job['kind'], string> = {
  reprovision: 'Reprovisioning',
  reinstall: 'Reinstalling',
  update: 'Updating',
  restore: 'Restoring',
  create: 'Creating',
};

export function jobWord(job: Job): string {
  if (job.state === 'failed') return job.repair?.state === 'working' ? 'Fixing' : 'Failed';
  return JOB_WORD[job.kind];
}

export function vmWord(vm: Vm, job: Job | null): string {
  if (job) return jobWord(job);
  return { on: 'Running', off: 'Off', starting: 'Starting', stopping: 'Stopping' }[vm.power];
}

/** Title of a failed job's block. */
export function failedTitle(job: Job): string {
  const k = job.kind === 'update' ? 'Update' : job.kind[0].toUpperCase() + job.kind.slice(1);
  return `${k} failed`;
}

export type MainAction =
  | { kind: 'update'; to: string }
  | { kind: 'reprovision'; waits: string | null };

/** What the Reprovision tile does: Update when a newer Construct is out for this VM, else Reprovision,
    with the yellow dot when a change waits for the next reprovision. */
export function mainAction(state: State, vm: Vm): MainAction {
  const to = vmUpdateTarget(state, vm);
  if (to) return { kind: 'update', to };
  return { kind: 'reprovision', waits: vm.changeWaits };
}

/** The version this VM would move to with Update, or null. */
export function vmUpdateTarget(state: State, vm: Vm): string | null {
  if (vm.updateTo !== undefined) return vm.updateTo;
  const host = state.hosts.find(h => h.id === vm.hostId);
  if (!host) return null;
  // The VM follows its host. A host this user administers moves to the device's release on the same channel.
  let target = host.version;
  if (host.admin && host.channel === state.prefs.channel && state.release && newer(state.release, host.version)) target = state.release;
  return newer(target, vm.version) ? target : null;
}

/** The release a host this user administers moves to with Update, or null. A host that reports its own offer
    (`updateTo`) uses that; otherwise this device's release on the same channel. */
export function hostUpdateTarget(state: State, host: Host): string | null {
  if (!host.admin) return null;
  if (host.updateTo !== undefined) return host.updateTo;
  return host.channel === state.prefs.channel && state.release && newer(state.release, host.version) ? state.release : null;
}

/** Semver-ish compare of dotted numbers. */
export function cmpVersion(a: string, b: string): number {
  const pa = a.split('.').map(n => parseInt(n, 10) || 0), pb = b.split('.').map(n => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d) return d < 0 ? -1 : 1;
  }
  return 0;
}
export const newer = (a: string, b: string) => cmpVersion(a, b) > 0;
