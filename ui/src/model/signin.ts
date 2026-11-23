import type { Agent, Vm } from '../backend/types';
import { AGENTS } from './settings';

/* A sign-in's terminal text with its links: the CLI's own words, links opened in the browser. */

export type Part = { text: string } | { url: string };

const URL = /https?:\/\/[^\s<>"'`]+/g;

/** The text split into plain runs and http(s) links; punctuation that ends a sentence stays outside the link. */
export function linkParts(text: string): Part[] {
  const out: Part[] = [];
  let last = 0;
  for (const m of text.matchAll(URL)) {
    let url = m[0];
    const tail = /[.,;:!?)\]}]+$/.exec(url)?.[0] ?? '';
    // A closing bracket the link itself opened stays with it.
    const keep = tail.startsWith(')') && url.includes('(') ? 1 : 0;
    url = url.slice(0, url.length - tail.length + keep);
    if (m.index! > last) out.push({ text: text.slice(last, m.index) });
    out.push({ url });
    last = m.index! + url.length;
  }
  if (last < text.length) out.push({ text: text.slice(last) });
  return out;
}

/** The agent the VM reports under its tool id, signed in or not; undefined while the VM has not said. */
export function signedIn(agents: { id: string; signedIn?: boolean | null }[], tool: string): boolean | undefined {
  const s = agents.find(a => a.id === tool)?.signedIn;
  return s ?? undefined;
}

/** What the screens show for an agent's sign-in: `yes`, `no`, `unknown` when the VM reports the agent but cannot say
    (a guest without the sign-in helper), `unsaid` while the VM has not reported the agent at all (a new VM). */
export function signInState(agents: { id: string; signedIn?: boolean | null }[], tool: string): 'yes' | 'no' | 'unknown' | 'unsaid' {
  const a = agents.find(x => x.id === tool);
  if (!a) return 'unsaid';
  return a.signedIn === true ? 'yes' : a.signedIn === false ? 'no' : 'unknown';
}

/** The lost sign-ins (S36) of every VM, oldest first: a card each. */
export function lostSignIns(vms: Vm[]): { vm: Vm; agent: Agent; id: (typeof AGENTS)[number] }[] {
  return vms
    .flatMap(vm => vm.agents.flatMap(agent => {
      const id = AGENTS.find(a => a.tool === agent.id);
      return agent.lostAt && id ? [{ vm, agent, id }] : [];
    }))
    .sort((a, b) => (a.agent.lostAt! < b.agent.lostAt! ? -1 : 1));
}
