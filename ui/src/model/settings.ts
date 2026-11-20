import type { AgentId, SettingsPatch, VmSettings } from '../backend/types';

/** The coding agents a VM can have, in the order the pages list them, with the engine's tool id for their version and
    whether a new VM gets them (S32). */
export const AGENTS: readonly { id: AgentId; tool: string; name: string; byDefault: boolean }[] = [
  { id: 'claude-code', tool: 'claude', name: 'Claude Code', byDefault: true },
  { id: 'codex', tool: 'codex', name: 'Codex', byDefault: true },
  { id: 'opencode', tool: 'opencode', name: 'OpenCode', byDefault: true },
  { id: 'antigravity', tool: 'antigravity', name: 'Antigravity', byDefault: false },
];

/** The agents a VM has without a choice of its own. */
export const DEFAULT_AGENTS: AgentId[] = AGENTS.filter(a => a.byDefault).map(a => a.id);

/** A list of agent ids as one switch per agent; ids this UI does not know are left out. */
export function agentSwitches(on: readonly string[]): Record<AgentId, boolean> {
  return Object.fromEntries(AGENTS.map(a => [a.id, on.includes(a.id)])) as Record<AgentId, boolean>;
}

/** The switches as the engine's list, in the order above. */
export const agentList = (on: Partial<Record<AgentId, boolean>>): AgentId[] => AGENTS.filter(a => on[a.id]).map(a => a.id);

/** When a settings group takes effect. Restart-class changes ask "restart now or later"; reprovision-class
    ones put the yellow dot on Reprovision; the rest apply at once without a sign. */
export type Applies = 'now' | 'restart' | 'reprovision' | 'reinstall';

const GROUP: Record<keyof VmSettings, Applies> = {
  hw: 'restart',
  disk: 'reinstall',
  idle: 'now',
  services: 'reprovision',
  identity: 'reprovision',
  agents: 'reprovision',
};

export function applies(patch: SettingsPatch): Applies {
  const order: Applies[] = ['now', 'reprovision', 'restart', 'reinstall'];
  let worst: Applies = 'now';
  for (const k of Object.keys(patch) as (keyof VmSettings)[]) {
    const a = k === 'hw' && patch.hw && !('memGb' in patch.hw) && !('cpus' in patch.hw) ? 'now' : GROUP[k];
    if (order.indexOf(a) > order.indexOf(worst)) worst = a;
  }
  return worst;
}

export const groupApplies = (g: keyof VmSettings) => GROUP[g];

/** The restart line: "Memory 16 → 32 GB · vCPUs 4 → 8". Empty when nothing restart-class differs. */
export function hwChange(now: { memGb: number; cpus: number }, want: { memGb: number; cpus: number }): string {
  const out: string[] = [];
  if (now.memGb !== want.memGb) out.push(`Memory ${now.memGb} → ${want.memGb} GB`);
  if (now.cpus !== want.cpus) out.push(`vCPUs ${now.cpus} → ${want.cpus}`);
  return out.join(' · ');
}

/** Merge a patch into settings without mutating. */
export function applyPatch(s: VmSettings, p: SettingsPatch): VmSettings {
  const out = { ...s } as VmSettings;
  for (const k of Object.keys(p) as (keyof VmSettings)[]) (out as any)[k] = { ...s[k], ...p[k] };
  return out;
}
