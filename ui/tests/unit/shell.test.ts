import { describe, expect, it } from 'vitest';
import { makeShell, openExternal } from '../../src/ui/shell';

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
  it('asks the document that frames it (the VS Code view) when there is no host object', () => {
    const sent: unknown[] = [];
    const targets: string[] = [];
    const w = win('http://127.0.0.1:5000/?surf=pop#/');
    (w as unknown as { parent: unknown }).parent = { postMessage: (m: unknown, t: string) => { sent.push(m); targets.push(t); } };
    const sh = makeShell(w);
    sh.openWindow({ page: 'vault' });
    sh.close();
    expect(sent).toEqual([{ type: 'openWindow', route: '#/vault' }, { type: 'close' }]);
    expect(w.location.href).toBe('http://127.0.0.1:5000/?surf=pop#/');
  });

  it('does not treat a top-level page as framed', () => {
    const w = win('http://127.0.0.1:5000/?surf=pop#/');
    (w as unknown as { parent: unknown }).parent = w;
    makeShell(w).openWindow({ page: 'vault' });
    expect(w.location.href).toBe('http://127.0.0.1:5000/?surf=win#/vault');
  });
});

describe('openExternal', () => {
  it('opens a new window in a browser and in the desktop shell, and asks the frame owner in VS Code', () => {
    const opened: string[] = [];
    const open = ((u: string) => { opened.push(u); return null; }) as unknown as Window['open'];
    const plain = Object.assign(win('http://127.0.0.1:5000/'), { open });
    openExternal(plain, 'http://localhost:3000');
    const shell = Object.assign(win('http://127.0.0.1:5000/', { postMessage: () => { throw new Error('not the host object'); } }), { open });
    openExternal(shell, 'http://localhost:3001');
    expect(opened).toEqual(['http://localhost:3000', 'http://localhost:3001']);

    const sent: unknown[] = [];
    const framed = Object.assign(win('http://127.0.0.1:5000/'), { open, parent: { postMessage: (m: unknown) => sent.push(m) } });
    openExternal(framed, 'http://localhost:3002');
    expect(sent).toEqual([{ type: 'openExternal', url: 'http://localhost:3002' }]);
    expect(opened).toHaveLength(2);
  });
});
