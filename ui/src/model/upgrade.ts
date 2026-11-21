import type { Upgrade } from '../backend/types';

type Item = Upgrade['items'][number];
/** `group`: a row standing for several items, whose `count` is "n / total". */
export type ConvertRow = { key: string; name: string; state: Item['state']; count: string; pct: number; group?: boolean };

/** The groups the converter has many items of, by the prefix of their id (finding 6). */
const GROUPS: [string, string][] = [['profile:', 'Profiles'], ['secret:', 'Secrets']];

/** The convert step's rows: one per group with "n / total" (the items finished of all the group has), at the place of
    its first item, the other items as they are. A failed item of a group keeps its own row after the group's, with its
    message. */
export function convertRows(items: Item[]): ConvertRow[] {
  const out: ConvertRow[] = [];
  const seen = new Set<string>();
  for (const it of items) {
    const g = GROUPS.find(([prefix]) => it.id.startsWith(prefix));
    if (!g) { out.push({ key: it.id, name: it.name, state: it.state, count: it.count, pct: it.pct }); continue; }
    if (seen.has(g[0])) continue;
    seen.add(g[0]);
    const all = items.filter(x => x.id.startsWith(g[0]));
    const finished = all.filter(x => x.state === 'done' || x.state === 'skipped' || x.state === 'failed').length;
    const failed = all.filter(x => x.state === 'failed');
    const state: Item['state'] = finished === all.length ? (failed.length ? 'failed' : 'done')
      : finished === 0 && all.every(x => x.state === 'waiting') ? 'waiting' : 'running';
    out.push({ key: g[0], name: g[1], state, count: `${finished} / ${all.length}`, pct: Math.round((finished / all.length) * 100), group: true });
    for (const f of failed) out.push({ key: f.id, name: f.name, state: 'failed', count: f.count, pct: 0 });
  }
  return out;
}
