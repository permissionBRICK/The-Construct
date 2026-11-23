/* The agent's `/api/events` stream. EventSource cannot send the agent's token as a header (and the token must
   not go into a URL), so this reads the stream with fetch. */

export interface AgentEventData { seq: number; type: string; engine: string | null; data: unknown }

/** Splits a server-sent event stream into events; `feed` takes text as it arrives. */
export class SseParser {
  private buf = '';
  constructor(private on: (type: string, data: string, id: string | null) => void) {}

  feed(text: string) {
    this.buf += text.replace(/\r\n?/g, '\n');
    let end: number;
    while ((end = this.buf.indexOf('\n\n')) >= 0) {
      const block = this.buf.slice(0, end);
      this.buf = this.buf.slice(end + 2);
      let type = 'message', id: string | null = null;
      const data: string[] = [];
      for (const line of block.split('\n')) {
        if (!line || line.startsWith(':')) continue;
        const i = line.indexOf(':');
        const field = i < 0 ? line : line.slice(0, i);
        const value = i < 0 ? '' : line.slice(i + 1).replace(/^ /, '');
        if (field === 'event') type = value;
        else if (field === 'data') data.push(value);
        else if (field === 'id') id = value;
      }
      if (data.length) this.on(type, data.join('\n'), id);
    }
  }
}

/** Follows the stream until `stop` is called, reconnecting with the last id after a drop. `onDenied` is called
    once when the agent refuses the token; the stream stops then. */
export function followEvents(opts: {
  url: (since: number) => string;
  headers: Record<string, string>;
  fetch: typeof fetch;
  on: (type: string, ev: AgentEventData | null) => void;
  onDenied: () => void;
  retryMs?: number;
}): () => void {
  let stopped = false;
  let since = 0;
  const ctl = { abort: null as AbortController | null };
  const loop = async () => {
    while (!stopped) {
      const abort = new AbortController();
      ctl.abort = abort;
      try {
        const res = await opts.fetch(opts.url(since), { headers: opts.headers, signal: abort.signal });
        if (res.status === 401) { stopped = true; opts.onDenied(); return; }
        if (!res.ok || !res.body) throw new Error(String(res.status));
        const parser = new SseParser((type, data, id) => {
          if (id && /^\d+$/.test(id)) since = Math.max(since, Number(id));
          let ev: AgentEventData | null = null;
          try { ev = JSON.parse(data) as AgentEventData; } catch { ev = null; }
          if (type === 'reset' && ev && typeof (ev as unknown as { seq?: number }).seq === 'number') since = (ev as unknown as { seq: number }).seq;
          opts.on(type, ev);
        });
        const reader = res.body.getReader();
        const dec = new TextDecoder();
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          parser.feed(dec.decode(value, { stream: true }));
        }
      } catch {
        /* dropped; reconnect below */
      }
      if (stopped) return;
      await new Promise(r => setTimeout(r, opts.retryMs ?? 2000));
    }
  };
  void loop();
  return () => { stopped = true; ctl.abort?.abort(); };
}
