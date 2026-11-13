import { describe, expect, it } from 'vitest';
import { sampleState } from '../../src/backend/sample';
import { activeJob, cmpVersion, mainAction, vmDot, vmUpdateTarget, vmWord } from '../../src/model/vm';
import { updateDue, updateParts, updatePlan } from '../../src/model/update';
import { applies, applyPatch, hwChange } from '../../src/model/settings';
import { formatRoute, parseRoute, section } from '../../src/model/route';
import { contrast, cssVars, SCHEME_ORDER, SCHEMES } from '../../src/model/tokens';
import type { Job } from '../../src/backend/types';
import { profileError } from '../../src/model/profile';

const job = (p: Partial<Job>): Job => ({ id: 'j', vmId: 'atlas', kind: 'reprovision', state: 'running', step: 's', stepIndex: 0, stepCount: 3, pct: 0, log: [], error: null, repair: null, ...p });

describe('versions', () => {
  it('compares dotted numbers', () => {
    expect(cmpVersion('2.5.0', '2.4.9')).toBe(1);
    expect(cmpVersion('2.10.0', '2.9.0')).toBe(1);
    expect(cmpVersion('2.4', '2.4.0')).toBe(0);
    expect(cmpVersion('1.0.0', '1.0.1')).toBe(-1);
  });
});

describe('VM state', () => {
  it('shows the job over the power state', () => {
    const s = sampleState();
    const vm = s.vms[0];
    expect(vmDot(vm, null)).toBe('on');
    expect(vmDot(vm, job({}))).toBe('busy');
    expect(vmDot(vm, job({ state: 'failed' }))).toBe('bad');
    expect(vmWord(vm, job({ kind: 'reinstall' }))).toBe('Reinstalling');
    expect(vmWord(vm, job({ state: 'failed', repair: { agent: 'a', state: 'working', messages: [], t3Thread: false } }))).toBe('Fixing');
    expect(vmWord(s.vms[1], null)).toBe('Off');
  });

  it('picks the newest running or failed job, never a finished one', () => {
    const s = sampleState();
    s.jobs = [job({ id: 'a', state: 'failed' }), job({ id: 'b', state: 'done' }), job({ id: 'c', vmId: 'pebble' })];
    expect(activeJob(s, 'atlas')?.id).toBe('a');
    s.jobs.push(job({ id: 'd', state: 'stopped' }));
    expect(activeJob(s, 'atlas')?.id).toBe('a');
    expect(activeJob(s, 'harbor')).toBeNull();
  });
});

describe('the Reprovision tile', () => {
  it('is Reprovision when calm, with the dot when a change waits', () => {
    const s = sampleState();
    expect(mainAction(s, s.vms[0])).toEqual({ kind: 'reprovision', waits: null });
    s.vms[0].changeWaits = 'webshop';
    expect(mainAction(s, s.vms[0])).toEqual({ kind: 'reprovision', waits: 'webshop' });
  });

  it('becomes Update when a release is out, which covers a waiting change', () => {
    const s = sampleState();
    s.release = '2.5.0';
    s.vms[0].changeWaits = 'webshop';
    expect(mainAction(s, s.vms[0])).toEqual({ kind: 'update', to: '2.5.0' });
  });

  it('follows the host: a host the user does not administer is not moved', () => {
    const s = sampleState();
    s.release = '2.5.0';
    const kiln = s.vms.find(v => v.id === 'kiln')!;   // on lab: nightly, not admin, already 2.6.0
    expect(vmUpdateTarget(s, kiln)).toBeNull();
    const lab = s.hosts.find(h => h.id === 'lab')!;
    lab.version = '2.7.0';
    expect(vmUpdateTarget(s, kiln)).toBe('2.7.0');   // its admin updated the host; the VM may follow
  });
});

describe('update everything', () => {
  it('lists this device, hosts and own VMs, and leaves foreign hosts alone', () => {
    const s = sampleState();
    expect(updateDue(s)).toBe(false);
    s.release = '2.5.0';
    const parts = updateParts(s);
    expect(parts[0]).toMatchObject({ name: 'This device', to: '2.5.0' });
    expect(parts.find(p => p.name === 'orchard')).toMatchObject({ to: '2.5.0', note: null });
    expect(parts.find(p => p.name === 'lab')).toMatchObject({ to: null, note: 'channel' });
    expect(parts.find(p => p.name === 'kiln')).toMatchObject({ to: null, note: 'channel' });
    expect(updatePlan(s)).toEqual(['This device', 'This PC', 'orchard', 'atlas', 'pebble', 'harbor']);
  });

  it('marks a same-channel host the user does not administer', () => {
    const s = sampleState();
    s.release = '2.5.0';
    const o = s.hosts.find(h => h.id === 'orchard')!;
    o.admin = false;
    expect(updateParts(s).find(p => p.name === 'orchard')).toMatchObject({ to: null, note: 'admin' });
    expect(updateParts(s).find(p => p.name === 'harbor')?.to).toBeNull();
  });
});

describe('settings', () => {
  it('classifies when a change applies', () => {
    expect(applies({ hw: { memGb: 32 } })).toBe('restart');
    expect(applies({ hw: { checkpoints: true } })).toBe('now');
    expect(applies({ idle: { afterMin: 15 } })).toBe('now');
    expect(applies({ services: { smb: true } })).toBe('reprovision');
    expect(applies({ disk: { diskGb: 240 }, services: { smb: true } })).toBe('reinstall');
  });

  it('describes a hardware change for the restart choice', () => {
    expect(hwChange({ memGb: 16, cpus: 8 }, { memGb: 32, cpus: 8 })).toBe('Memory 16 → 32 GB');
    expect(hwChange({ memGb: 16, cpus: 8 }, { memGb: 16, cpus: 4 })).toBe('vCPUs 8 → 4');
    expect(hwChange({ memGb: 16, cpus: 8 }, { memGb: 16, cpus: 8 })).toBe('');
  });

  it('merges a patch without touching the original', () => {
    const s = sampleState().vms[0].settings;
    const n = applyPatch(s, { services: { smb: true } });
    expect(n.services.smb).toBe(true);
    expect(s.services.smb).toBe(false);
    expect(n.services.t3Web).toBe(true);
  });
});

describe('routes', () => {
  it('round-trips through the hash', () => {
    for (const r of [{ page: 'home' as const }, { page: 'project' as const, arg: 'web shop' }, { page: 'host' as const, arg: 'orchard', tab: 'users' }]) {
      expect(parseRoute(formatRoute(r))).toEqual(r);
    }
    expect(parseRoute('#/nonsense')).toEqual({ page: 'home' });
    expect(parseRoute('')).toEqual({ page: 'home' });
  });
  it('maps pages to sidebar entries', () => {
    expect(section('file')).toBe('projects');
    expect(section('pair')).toBe('vault');
    expect(section('host')).toBe('hosts');
  });
});

describe('tokens', () => {
  it('has every key in every scheme and mode', () => {
    const keys = Object.keys(SCHEMES.green.light).sort();
    for (const k of SCHEME_ORDER) for (const m of ['light', 'dark'] as const) {
      expect(Object.keys(SCHEMES[k][m]).sort()).toEqual(keys);
      expect(cssVars(k, m)['--font']).toContain('IBM Plex Sans');
    }
  });
  it('keeps text readable in every scheme', () => {
    for (const k of SCHEME_ORDER) for (const m of ['light', 'dark'] as const) {
      const t = SCHEMES[k][m];
      expect(contrast(t.fg, t.bg), `${k} ${m} fg`).toBeGreaterThan(7);
      expect(contrast(t.dim, t.pn), `${k} ${m} dim`).toBeGreaterThan(3);
      expect(contrast(t.prifg, t.pri), `${k} ${m} primary`).toBeGreaterThan(4.5);
      expect(contrast(t.warn, t.warnbg), `${k} ${m} warn`).toBeGreaterThan(4.5);
      expect(contrast(t.sidefg, t.side), `${k} ${m} sidebar`).toBeGreaterThan(7);
    }
  });
});

describe('profile shape', () => {
  it('accepts the sample profiles and names what is wrong', () => {
    for (const p of sampleState().projects) expect(profileError(p)).toBeNull();
    expect(profileError({ name: 'webshop' })).toMatch(/repos/);
    expect(profileError([])).toMatch(/object/);
    const p = structuredClone(sampleState().projects[0]);
    (p.forwards as unknown[]).push({ port: '80', label: 'x' });
    expect(profileError(p)).toMatch(/forwards/);
  });
});
