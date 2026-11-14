import { formatRoute, type Route } from '../model/route';
import type { Shell } from './ctx';

/** The WebView2 host object the Windows shell provides; absent in a browser. */
interface WebViewHost { postMessage(message: unknown): void }

type Win = Pick<Window, 'location'> & { chrome?: { webview?: WebViewHost }; parent?: unknown };

/** Where the page's requests to its shell go: the desktop shell's WebView2 host object, or the document that
    frames the page (the VS Code extension's view). Null in a plain browser tab. The messages carry a route or
    a link, never a secret, so a framing document of any origin may get them; the extension checks each one. */
function shellHost(w: Win): WebViewHost | null {
  const h = w.chrome?.webview;
  if (h && typeof h.postMessage === 'function') return h;
  const parent = w.parent as { postMessage?(message: unknown, target: string): void } | undefined;
  if (parent && parent !== (w as unknown) && typeof parent.postMessage === 'function')
    return { postMessage: m => parent.postMessage!(m, '*') };
  return null;
}

/** The shell hooks for the standalone page: messages to the shell when there is one, else what a browser tab
    can do itself. Nothing else in the UI posts messages. */
export function makeShell(w: Win): Shell {
  return {
    openWindow(r: Route) {
      const h = shellHost(w);
      if (h) { h.postMessage({ type: 'openWindow', route: formatRoute(r) }); return; }
      const u = new URL(w.location.href);
      u.searchParams.set('surf', 'win');
      u.hash = formatRoute(r);
      w.location.href = u.toString();
    },
    close() {
      shellHost(w)?.postMessage({ type: 'close' });
    },
  };
}

/** Opens a link outside the page. Framed (VS Code), a new window is not allowed, so the frame's owner opens
    it; elsewhere the browser or the desktop shell does (the shell sends new windows to the default browser). */
export function openExternal(w: Win & Pick<Window, 'open'>, url: string): void {
  const framed = !(w.chrome?.webview) && shellHost(w);
  if (framed) { framed.postMessage({ type: 'openExternal', url }); return; }
  try { w.open(url, '_blank', 'noopener'); } catch { /* no window here */ }
}
