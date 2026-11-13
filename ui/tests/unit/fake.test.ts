import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeBackend, SCENES } from '../../src/backend/fake';
import { activeJob } from '../../src/model/vm';

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

const run = (ms: number) => vi.advanceTimersByTime(ms);

describe('FakeBackend', () => {
  it('builds every scene without throwing', () => {
    for (const scene of SCENES) expect(() => new FakeBackend({ scene, timers: false }).snapshot()).not.toThrow();
  });

  it('notifies subscribers and hands out a new snapshot per change', () => {
    const be = new FakeBackend({ tick: 10 });
    const before = be.snapshot();
    const fn = vi.fn();
    const off = be.subscribe(fn);
    be.setPrefs({ scheme: 'copper' });
    expect(fn).toHaveBeenCalledTimes(1);
    expect(be.snapshot()).not.toBe(before);
    expect(before.prefs.scheme).toBe('green');
    off();
    be.setPrefs({ scheme: 'blue' });
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('reprovisions with progress and clears a waiting change', () => {
    const be = new FakeBackend({ scene: 'changed', tick: 10 });
    be.reprovision('atlas');
    const j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.state).toBe('running');
    run(30);
    expect(activeJob(be.snapshot(), 'atlas')!.pct).toBeGreaterThan(0);
    run(1000);
    expect(activeJob(be.snapshot(), 'atlas')).toBeNull();
    expect(be.snapshot().jobs.at(-1)!.state).toBe('done');
    expect(be.snapshot().vms[0].changeWaits).toBeNull();
  });

  it('starts the repair agent on failure, takes a reply and retries', () => {
    const be = new FakeBackend({ tick: 10 });
    be.demo('fail-next');
    be.reprovision('atlas');
    run(500);
    let j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.state).toBe('failed');
    expect(j.repair?.state).toBe('asking');
    expect(j.repair!.messages.length).toBe(4);
    be.replyRepair(j.id, 'stop them');
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair!.messages.at(-1)).toEqual({ from: 'user', text: 'stop them' });
    run(40);
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.state).toBe('done');
    run(40);
    expect(activeJob(be.snapshot(), 'atlas')!.state).toBe('running');   // retried
    run(2000);
    expect(activeJob(be.snapshot(), 'atlas')).toBeNull();
  });

  it('answers a lease request and records it', () => {
    const be = new FakeBackend({ scene: 'lease', timers: false });
    const r = be.snapshot().vaultRequests[0];
    be.answerLease(r.id, true);
    const s = be.snapshot();
    expect(s.vaultRequests).toHaveLength(0);
    expect(s.leases[0]).toMatchObject({ secret: 'forge-token', vmName: 'atlas' });
    expect(s.history[0].event).toBe('Allowed 2 h');
  });

  it('saves an entered secret only if the name is free', () => {
    const be = new FakeBackend({ scene: 'enter', timers: false });
    const r = be.snapshot().vaultRequests[0];
    be.enterSecret(r.id, 'hunter22');   // staging-db exists in the sample vault
    expect(be.snapshot().secrets.filter(x => x.name === 'staging-db')).toHaveLength(1);
    expect(be.snapshot().notice?.text).toMatch(/already exists/);
    be.demo('enter');
    be.deleteSecret('staging-db');
    const r2 = be.snapshot().vaultRequests[0];
    be.enterSecret(r2.id, 'v');
    expect(be.snapshot().secrets.find(x => x.name === 'staging-db')?.auto).toBe(false);
  });

  it('updates everything the user owns, VMs one after the other', () => {
    const be = new FakeBackend({ scene: 'update', tick: 10 });
    be.updateEverything();
    expect(activeJob(be.snapshot(), 'atlas')?.kind).toBe('update');
    run(5000);
    const s = be.snapshot();
    expect(s.version).toBe('2.5.0');
    expect(s.hosts.find(h => h.id === 'orchard')!.version).toBe('2.5.0');
    expect(s.hosts.find(h => h.id === 'lab')!.version).toBe('2.6.0');   // other channel, not admin
    expect(s.vms.filter(v => v.hostId !== 'lab').every(v => v.version === '2.5.0')).toBe(true);
    expect(s.release).toBeNull();
  });

  it('updates only one VM when asked', () => {
    const be = new FakeBackend({ scene: 'update', tick: 10 });
    be.updateVm('harbor');
    run(5000);
    const s = be.snapshot();
    expect(s.vms.find(v => v.id === 'harbor')!.version).toBe('2.5.0');
    expect(s.vms.find(v => v.id === 'atlas')!.version).toBe('2.4.0');
    expect(s.release).toBe('2.5.0');
  });

  it('asks restart now or later for hardware, and marks reprovision changes', () => {
    const be = new FakeBackend({ timers: false });
    be.saveSettings('atlas', { hw: { memGb: 32 } }, 'later');
    let v = be.snapshot().vms[0];
    expect(v.settings.hw.memGb).toBe(32);
    expect(v.appliedHw.memGb).toBe(16);
    be.restart('atlas');
    v = be.snapshot().vms[0];
    expect(v.appliedHw.memGb).toBe(32);
    be.saveSettings('atlas', { services: { smb: true } });
    expect(be.snapshot().vms[0].changeWaits).toBe('Settings');
  });

  it('pairs a device after the words are confirmed', () => {
    const be = new FakeBackend({ tick: 100 });
    be.startPairing();
    expect(be.snapshot().pairing?.state).toBe('showing');
    run(8000);
    expect(be.snapshot().pairing?.state).toBe('confirm');
    be.confirmPairing(true);
    expect(be.snapshot().devices.at(-1)?.name).toBe('Tablet');
    expect(be.snapshot().pairing?.state).toBe('done');
  });

  it('revokes a device and lists secrets to rotate', () => {
    const be = new FakeBackend({ tick: 10 });
    be.revokeDevice('d1');   // this device: refused
    expect(be.snapshot().revocation).toBeNull();
    be.revokeDevice('d2');
    expect(be.snapshot().revocation).toMatchObject({ confirmed: 1, total: 2 });
    run(100);
    expect(be.snapshot().revocation?.confirmed).toBe(2);
    be.markRotated('forge-token');
    expect(be.snapshot().revocation?.rotate[0].done).toBe(true);
  });

  it('shows what a new user needs after adding them', () => {
    const be = new FakeBackend({ timers: false });
    be.addUser('orchard', { name: 'kai', role: 'user', maxVms: 1, hostForwards: 'default' });
    const h = be.snapshot().handout!;
    expect(h).toMatchObject({ hostId: 'orchard', user: 'kai', address: 'https://orchard.example.net:8443' });
    expect(h.token).toMatch(/^cst_/);
    be.addUser('pc', { name: 'kai', role: 'user', maxVms: 1, hostForwards: 'default' });
    expect(be.snapshot().handout?.token).toBeNull();   // Windows sign-in host: no token
  });

  it('refuses host administration on a host the user does not administer', () => {
    const be = new FakeBackend({ timers: false });
    be.addUser('lab', { name: 'kai', role: 'user', maxVms: 1, hostForwards: 'default' });
    expect(be.snapshot().hosts.find(h => h.id === 'lab')!.users).toHaveLength(0);
  });

  it('validates configuration JSON', () => {
    const be = new FakeBackend({ timers: false });
    expect(be.saveConfig('orchard', 'usage', '{ bad')).toMatch(/JSON|token|position/i);
    expect(be.saveConfig('orchard', 'usage', '{"retentionDays": 30}')).toBeNull();
  });

  it('creates a VM on a pasted remote host', () => {
    const be = new FakeBackend({ timers: false });
    be.createVm({ where: 'remote', name: 'quill', hostAddress: 'https://forge.example.net:8443', signIn: 'token', token: 't' });
    const s = be.snapshot();
    const vm = s.vms.find(v => v.name === 'quill')!;
    expect(s.hosts.find(h => h.id === vm.hostId)?.name).toBe('forge');
    expect(activeJob(s, 'quill')?.kind).toBe('create');
  });

  it('walks the one-time upgrade', () => {
    const be = new FakeBackend({ scene: 'upgrade-intro', timers: false });
    be.upgradeNext();
    expect(be.snapshot().upgrade?.step).toBe('code');
    be.upgradeNext();
    expect(be.snapshot().upgrade?.step).toBe('done');
    be.upgradeNext();
    expect(be.snapshot().upgrade).toBeNull();
  });
});

describe('Windows guests', () => {
  it('assigns a free key and activates', () => {
    const be = new FakeBackend({ timers: false });
    be.assignWinKey('orchard', 'reed', 'k2');
    let h = be.snapshot().hosts.find(x => x.id === 'orchard')!;
    expect(h.winGuests.find(g => g.name === 'reed')!.keyId).toBe('k2');
    expect(h.winKeys.find(k => k.id === 'k2')!.held).toBe(true);
    be.activateWindows('orchard', 'reed');
    h = be.snapshot().hosts.find(x => x.id === 'orchard')!;
    expect(h.winGuests.find(g => g.name === 'reed')!.activation).toBe('activated');
  });
});
