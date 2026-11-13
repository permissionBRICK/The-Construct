import type { SettingsPatch, VmSettings } from '../backend/types';

/** When a settings group takes effect. Restart-class changes ask "restart now or later"; reprovision-class
    ones put the yellow dot on Reprovision; the rest apply at once without a sign. */
export type Applies = 'now' | 'restart' | 'reprovision' | 'reinstall';

const GROUP: Record<keyof VmSettings, Applies> = {
  hw: 'restart',
  disk: 'reinstall',
  idle: 'now',
  services: 'reprovision',
  identity: 'reprovision',
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
