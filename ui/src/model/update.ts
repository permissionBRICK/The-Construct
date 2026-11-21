import type { Part, State } from '../backend/types';
import { hostUpdateTarget, newer, vmUpdateTarget } from './vm';

/** Everything the user owns and what Update does to each: this device, the hosts they administer, their own
    VMs. A host they do not administer, or on another channel, is listed and left alone. */
export function updateParts(state: State): Part[] {
  const rel = state.release;
  const parts: Part[] = [{ name: 'This device', version: state.version, to: rel && newer(rel, state.version) ? rel : null, note: null }];
  for (const h of state.hosts) {
    if (h.updateTo !== undefined) parts.push({ name: h.name, version: h.version, to: hostUpdateTarget(state, h), note: null });
    else if (h.channel !== state.prefs.channel) parts.push({ name: h.name, version: h.version, to: null, note: 'channel' });
    else if (!h.admin) parts.push({ name: h.name, version: h.version, to: null, note: rel && newer(rel, h.version) ? 'admin' : null });
    else parts.push({ name: h.name, version: h.version, to: hostUpdateTarget(state, h), note: null });
  }
  for (const v of state.vms) {
    const host = state.hosts.find(h => h.id === v.hostId);
    parts.push({ name: v.name, version: v.version, to: vmUpdateTarget(state, v), note: host && host.channel !== state.prefs.channel ? 'channel' : null });
  }
  return parts;
}

export const updateDue = (state: State) => updateParts(state).some(p => p.to);

/** The order Update runs in: device first, then hosts, then VMs one after the other. */
export function updatePlan(state: State): string[] {
  return updateParts(state).filter(p => p.to).map(p => p.name);
}

/** A version as the screens show it: a build's commit after `+` cut to seven characters, the channel named when it
    is not stable. `1.0.0+355ea2f2f0ec…` on nightly → `1.0.0 nightly · 355ea2f`. */
export function shortVersion(v: string, channel?: string): string {
  const [base, commit] = v.split('+', 2);
  const ch = channel && channel !== 'stable' ? ` ${channel}` : '';
  return commit ? `${base}${ch} · ${commit.slice(0, 7)}` : base;
}

/** The two halves of the short version, for a narrow place that shows them on two lines: `1.0.0` and
    `nightly · 355ea2f` (empty for a plain release). */
export function versionParts(v: string, channel?: string): { base: string; build: string } {
  const [base, commit] = v.split('+', 2);
  const ch = channel && channel !== 'stable' ? channel : '';
  return { base, build: commit ? [ch, commit.slice(0, 7)].filter(Boolean).join(' · ') : '' };
}
