import { describe, expect, it } from 'vitest';
import { makeShell } from '../../src/ui/shell';

/* The desktop shell's hooks: in WebView2 the page asks the shell's host object; in a browser it navigates. */

function win(href: string, webview?: { postMessage(m: unknown): void } | { postMessage: string }) {
  const location = { href } as Location;
  return { location, ...(webview ? { chrome: { webview } } : {}) } as unknown as Window & { chrome?: { webview?: { postMessage(m: unknown): void } } };
}

describe('makeShell', () => {
  it('posts close and openWindow to the WebView2 host object only', () => {
    const sent: unknown[] = [];
    const w = win('https://127.0.0.1:5000/?surf=pop#/', { postMessage: m => sent.push(m) });
    const sh = makeShell(w);
    sh.close();
    sh.openWindow({ page: 'vault' });
    sh.openWindow({ page: 'settings', arg: 'svc' });
    expect(sent).toEqual([{ type: 'close' }, { type: 'openWindow', route: '#/vault' }, { type: 'openWindow', route: '#/settings/svc' }]);
    // The page stays where it is; the shell opens the window.
    expect(w.location.href).toBe('https://127.0.0.1:5000/?surf=pop#/');
  });

  it('keeps the browser behaviour without a host object', () => {
    const w = win('https://127.0.0.1:5000/?surf=pop#/');
    const sh = makeShell(w);
    sh.close();
    sh.openWindow({ page: 'vault' });
    expect(w.location.href).toBe('https://127.0.0.1:5000/?surf=win#/vault');
  });

  it('ignores a webview object that cannot post', () => {
    const w = win('https://127.0.0.1:5000/?surf=alone#/', { postMessage: 'nope' });
    makeShell(w).openWindow({ page: 'home' });
    expect(w.location.href).toBe('https://127.0.0.1:5000/?surf=win#/home');
  });
});
