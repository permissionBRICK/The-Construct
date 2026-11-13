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

  it('allows a lease for the span the user chose', () => {
    const be = new FakeBackend({ scene: 'lease', timers: false });
    be.answerLease(be.snapshot().vaultRequests[0].id, true, 900);
    expect(be.snapshot().history[0].event).toBe('Allowed 15 min');
  });

  it('closes only a forward this device opened, and switches LAN access on a solo host', () => {
    const be = new FakeBackend({ timers: false });
    be.closeForward('atlas', 'f3');
    be.closeForward('atlas', 'f1');
    expect(be.snapshot().vms.find(v => v.id === 'atlas')!.forwards.map(f => f.id)).toEqual(['f2', 'f3']);
    be.setLanAccess('pc', true);
    be.setLanAccess('orchard', true);
    expect(be.snapshot().hosts.map(h => [h.id, h.lan])).toEqual([['pc', true], ['orchard', undefined], ['lab', undefined]]);
  });

  it('saves an entered secret, never auto-approved, and overwrites nothing', () => {
    const be = new FakeBackend({ scene: 'enter', timers: false });
    const r = be.snapshot().vaultRequests[0];
    be.enterSecret(r.id, 'sk-test-1');
    expect(be.snapshot().secrets.find(x => x.name === r.secret)?.auto).toBe(false);
    expect(be.snapshot().notice?.text).toBe('Saved in the vault');
    be.demo('enter');   // the same name again: it exists now
    be.enterSecret(be.snapshot().vaultRequests[0].id, 'other');
    expect(be.snapshot().secrets.filter(x => x.name === r.secret)).toHaveLength(1);
    expect(be.snapshot().notice?.text).toMatch(/already exists/);
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
    expect(s.everything?.running).toBe(false);
    expect(s.everything?.parts.find(p => p.name === 'lab')).toMatchObject({ part: 'engine', state: 'skipped' });
    expect(s.everything?.parts.filter(p => p.part === 'vms').every(p => p.state === 'done')).toBe(true);
  });

  it('updates only one VM when asked, leaving this device alone', () => {
    const be = new FakeBackend({ scene: 'update', tick: 10 });
    be.updateVm('harbor');
    run(5000);
    const s = be.snapshot();
    expect(s.vms.find(v => v.id === 'harbor')!.version).toBe('2.5.0');
    expect(s.vms.find(v => v.id === 'atlas')!.version).toBe('2.4.0');
    expect(s.version).toBe('2.4.0');
    expect(s.release).toBe('2.5.0');
  });

  it('starts Update everything after a job that already runs', () => {
    const be = new FakeBackend({ tick: 10 });
    be.reprovision('atlas');
    be.demo('release');
    be.updateEverything();
    run(10000);
    const s = be.snapshot();
    expect(s.vms.filter(v => v.hostId !== 'lab').every(v => v.version === '2.5.0')).toBe(true);
    expect(s.release).toBeNull();
  });

  it('reprovisions every VM on a host, other users\' as host jobs', () => {
    const be = new FakeBackend({ tick: 10 });
    be.updateHostVms('orchard');
    expect(be.snapshot().hosts.find(h => h.id === 'orchard')!.jobs.filter(j => j.state === 'running').map(j => j.vm)).toEqual(['moss']);
    expect(activeJob(be.snapshot(), 'harbor')?.kind).toBe('reprovision');
    run(2000);
    expect(be.snapshot().hosts.find(h => h.id === 'orchard')!.jobs.every(j => j.state !== 'running')).toBe(true);
  });

  it('keeps host administration and the VM list in step', () => {
    const be = new FakeBackend({ timers: false });
    be.hostVmAction('orchard', 'harbor', 'shutdown');
    expect(be.snapshot().vms.find(v => v.id === 'harbor')!.power).toBe('off');
    be.hostVmAction('orchard', 'harbor', 'delete');
    expect(be.snapshot().vms.find(v => v.id === 'harbor')).toBeUndefined();
    be.createVm({ where: 'local', name: 'quill' });
    expect(be.snapshot().hosts.find(h => h.id === 'pc')!.vms.some(v => v.name === 'quill')).toBe(true);
  });

  it('keeps tokens when a user is saved', () => {
    const be = new FakeBackend({ timers: false });
    const stale = structuredClone(be.snapshot().hosts.find(h => h.id === 'orchard')!.users[0]);
    be.revokeToken('orchard', 'alex', stale.tokens[0].id);
    be.saveUser('orchard', { ...stale, maxVms: 6 });
    const u = be.snapshot().hosts.find(h => h.id === 'orchard')!.users[0];
    expect(u.maxVms).toBe(6);
    expect(u.tokens).toHaveLength(0);
  });

  it('deletes a backup', () => {
    const be = new FakeBackend({ timers: false });
    be.deleteBackup('atlas', 'b1');
    expect(be.snapshot().vms[0].backups.some(b => b.id === 'b1')).toBe(false);
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
    expect(be.snapshot().revocation!.hosts.map(h => h.confirmed)).toEqual([true, false, false]);
    run(100);
    expect(be.snapshot().revocation!.hosts.every(h => h.confirmed)).toBe(true);
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

  it('walks the one-time upgrade in the agent\'s order', () => {
    const be = new FakeBackend({ scene: 'upgrade-intro', timers: false });
    be.upgradeNext();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: null });
    be.upgradeNext();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'recovery', code: null, error: 'Name this device' });
    be.upgradeNext({ deviceName: 'laptop' });
    expect(be.snapshot().upgrade?.code?.length).toBeGreaterThan(3);
    be.upgradeNext();
    expect(be.snapshot().upgrade).toMatchObject({ step: 'close', code: null, writers: ['VS Code'] });
    be.upgradeNext();
    expect(be.snapshot().upgrade?.step).toBe('convert');
    be.upgradeNext();
    expect(be.snapshot().upgrade).toBeNull();
  });

  it('starts the repair agent by hand where the host does not, and stops it', () => {
    const be = new FakeBackend({ tick: 10 });
    be.setRepairAuto('pc', false);
    expect(be.snapshot().hosts.find(h => h.id === 'pc')!.repairAuto).toBe(false);
    be.demo('fail-next');
    be.reprovision('atlas');
    run(500);
    let j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ state: 'failed', repair: null, canRepair: true });
    be.replyRepair(j.id, 'ignored');
    be.startRepair(j.id);
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ canRepair: false, repair: { state: 'working' } });
    // A reply while it works goes nowhere.
    be.replyRepair(j.id, 'too early');
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.messages.some(m => m.from === 'user')).toBe(false);
    be.stopRepair(j.id);
    j = activeJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ canRepair: true, repair: { state: 'stopped', outcome: 'Stopped' } });
    run(200);
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.state).toBe('stopped');
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
