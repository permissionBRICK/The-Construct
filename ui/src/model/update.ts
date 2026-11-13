import type { Part, State } from '../backend/types';
import { newer, vmUpdateTarget } from './vm';

/** Everything the user owns and what Update does to each: this device, the hosts they administer, their own
    VMs. A host they do not administer, or on another channel, is listed and left alone. */
export function updateParts(state: State): Part[] {
  const rel = state.release;
  const parts: Part[] = [{ name: 'This device', version: state.version, to: rel && newer(rel, state.version) ? rel : null, note: null }];
  for (const h of state.hosts) {
    if (h.channel !== state.prefs.channel) parts.push({ name: h.name, version: h.version, to: null, note: 'channel' });
    else if (!h.admin) parts.push({ name: h.name, version: h.version, to: null, note: rel && newer(rel, h.version) ? 'admin' : null });
    else parts.push({ name: h.name, version: h.version, to: rel && newer(rel, h.version) ? rel : null, note: null });
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
