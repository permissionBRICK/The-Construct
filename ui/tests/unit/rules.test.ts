import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/* The owner's rules, where a source scan can check them. */
const files: string[] = [];
const walk = (d: string) => { for (const f of readdirSync(d)) { const p = join(d, f); if (statSync(p).isDirectory()) walk(p); else if (/\.(tsx?|css)$/.test(f)) files.push(p); } };
walk(fileURLToPath(new URL('../../src', import.meta.url)));
const src = files.map(f => [f, readFileSync(f, 'utf8')] as const);

describe('rules', () => {
  it('uses no native dialogs', () => {
    for (const [f, t] of src) expect(t, f).not.toMatch(/\b(window\.)?(alert|confirm|prompt)\s*\(/);
  });
  it('has no network code', () => {
    for (const [f, t] of src) expect(t, f).not.toMatch(/\bfetch\s*\(|XMLHttpRequest|WebSocket|EventSource/);
  });
  it('loads no fonts or scripts from a CDN', () => {
    for (const [f, t] of src) expect(t, f).not.toMatch(/https?:\/\/(fonts\.googleapis|fonts\.gstatic|cdn)/);
  });
});
