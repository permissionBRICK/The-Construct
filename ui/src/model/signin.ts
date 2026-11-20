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
