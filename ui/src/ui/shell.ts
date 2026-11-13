import { formatRoute, type Route } from '../model/route';
import type { Shell } from './ctx';

/** The WebView2 host object the Windows shell provides; absent in a browser. */
interface WebViewHost { postMessage(message: unknown): void }

type Win = Pick<Window, 'location'> & { chrome?: { webview?: WebViewHost } };

/** The shell hooks for the standalone page: messages to the desktop shell's WebView2 host when it is there,
    else what a browser tab can do itself. Only `window.chrome.webview` is ever posted to. */
export function makeShell(w: Win): Shell {
  const host = () => {
    const h = w.chrome?.webview;
    return h && typeof h.postMessage === 'function' ? h : null;
  };
  return {
    openWindow(r: Route) {
      const h = host();
      if (h) { h.postMessage({ type: 'openWindow', route: formatRoute(r) }); return; }
      const u = new URL(w.location.href);
      u.searchParams.set('surf', 'win');
      u.hash = formatRoute(r);
      w.location.href = u.toString();
    },
    close() {
      host()?.postMessage({ type: 'close' });
    },
  };
}
