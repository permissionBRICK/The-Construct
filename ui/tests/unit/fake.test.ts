import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeBackend, SCENES } from '../../src/backend/fake';
import { activeJob, warnedJob } from '../../src/model/vm';

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

  it('updates a single coding agent without reprovisioning or touching other agents', () => {
    const be = new FakeBackend({ scene: 'changed', tick: 10 });
    const before = be.snapshot().vms[0];
    const behind = before.agents.filter(a => a.latest !== a.version);
    expect(behind.length).toBeGreaterThanOrEqual(1);
    const chosen = behind[0];
    be.updateAgents('atlas', chosen.id);
    const j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.kind).toBe('update');
    expect(j.step).toBe(`Updating ${chosen.name}`);
    run(1000);
    const after = be.snapshot().vms[0];
    expect(after.agents.find(a => a.id === chosen.id)!.version).toBe(chosen.latest);
    // Does not clear a pending reprovision change because it did not reprovision the VM.
    expect(after.changeWaits).toBe(before.changeWaits);
  });

  it('starts the repair in a T3 Code thread on failure, answered there and not here', () => {
    const be = new FakeBackend({ tick: 10 });
    be.demo('fail-next');
    be.reprovision('atlas');
    run(500);
    const j = activeJob(be.snapshot(), 'atlas')!;
    expect(j.repair).toMatchObject({ agent: 'T3 Code', state: 'asking', t3Thread: { threadId: 'thread-repair' } });
    be.replyRepair(j.id, 'stop them');
    expect(activeJob(be.snapshot(), 'atlas')!.repair!.messages).toHaveLength(4);
    be.open('atlas', 't3', j.repair!.t3Thread!);
    expect(be.snapshot().notice?.text).toBe('T3 Code opens on the repair thread');
  });

  it('with the chat setting starts the repair agent on failure, takes a reply and retries', () => {
    const be = new FakeBackend({ tick: 10 });
    be.setRepairChat(be.snapshot().vms.find(v => v.id === 'atlas')!.hostId, true);
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

  it('answers each file that still holds a secret and records it', () => {
    const be = new FakeBackend({ scene: 'files', timers: false });
    const [a, b] = be.snapshot().vaultFiles;
    be.answerFile(a.id, 'keep');
    be.answerFile(b.id, 'redact');
    const s = be.snapshot();
    expect(s.vaultFiles).toEqual([]);
    expect(s.history.slice(0, 2).map(h => h.event)).toEqual([`Redacted ${b.path}`, `Kept ${a.path}`]);
    expect(s.notice?.text).toBe('Redacting .env.local');
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
    be.enterSecret(r.id, 'sk-test-1', 'sandbox-user');
    const saved = be.snapshot().secrets.find(x => x.name === r.secret);
    expect(saved?.auto).toBe(false);
    expect(saved?.hasUsername).toBe(true);
    expect(be.snapshot().notice?.text).toBe('Saved in the vault');
    be.demo('enter');   // the same name again: it exists now
    be.enterSecret(be.snapshot().vaultRequests[0].id, 'other');
    expect(be.snapshot().secrets.filter(x => x.name === r.secret)).toHaveLength(1);
    expect(be.snapshot().notice?.text).toMatch(/already exists/);
  });

  it('tracks secret usernames on add, update and reveal; username-only edits keep leases', async () => {
    const be = new FakeBackend({ timers: false });
    be.addSecret('svc-login', 'Service login', 'pw-1', 'svc-user');
    expect(be.snapshot().secrets.find(x => x.name === 'svc-login')?.hasUsername).toBe(true);
    expect(await be.revealSecret('svc-login')).toEqual({ username: 'svc-login-user', value: 'svc-login-sample-value' });
    // Updating only the username on staging-db keeps its active lease.
    expect(be.snapshot().leases.some(l => l.secret === 'staging-db')).toBe(true);
    be.updateSecret('staging-db', { username: '' });
    expect(be.snapshot().secrets.find(x => x.name === 'staging-db')?.hasUsername).toBe(false);
    expect(be.snapshot().leases.some(l => l.secret === 'staging-db')).toBe(true);
    expect(await be.revealSecret('staging-db')).toEqual({ username: '', value: 'staging-db-sample-value' });
    // Replacing the value ends its leases.
    be.updateSecret('staging-db', { value: 'pw-2' });
    expect(be.snapshot().leases.some(l => l.secret === 'staging-db')).toBe(false);
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

  it('reprovisions every VM on a host that runs, other users\' as host jobs, and skips one that is off', () => {
    const be = new FakeBackend({ tick: 10 });
    const orchard = () => be.snapshot().hosts.find(h => h.id === 'orchard')!;
    // moss is off: the host's own reprovision of every VM never starts one.
    be.updateHostVms('orchard');
    expect(orchard().jobs.some(j => j.vm === 'moss')).toBe(false);
    expect(activeJob(be.snapshot(), 'harbor')?.kind).toBe('reprovision');
    run(2000);
    be.hostVmAction('orchard', 'moss', 'start');
    be.updateHostVms('orchard');
    expect(orchard().jobs.filter(j => j.state === 'running').map(j => j.vm)).toEqual(['moss']);
    run(2000);
    expect(orchard().jobs.every(j => j.state !== 'running')).toBe(true);
  });

  it('starts a VM that is off first when the user reprovisions it, and leaves it running', () => {
    const be = new FakeBackend({ tick: 10 });
    expect(be.snapshot().vms.find(v => v.id === 'pebble')!.power).toBe('off');
    be.reprovision('pebble');
    expect(activeJob(be.snapshot(), 'pebble')).toMatchObject({ state: 'running', step: 'Starting the VM', stepIndex: 0 });
    run(2000);
    expect(be.snapshot().jobs.at(-1)).toMatchObject({ vmId: 'pebble', state: 'done' });
    expect(be.snapshot().vms.find(v => v.id === 'pebble')!.power).toBe('on');
  });

  it('keeps host administration and the VM list in step', () => {
    const be = new FakeBackend({ timers: false });
    be.childPower('atlas', 'win-test', false);
    expect(be.snapshot().vms.find(v => v.id === 'atlas')!.children.find(c => c.name === 'win-test')!.power).toBe('off');
    expect(be.snapshot().hosts.find(h => h.id === 'pc')!.vms.find(v => v.name === 'win-test')!.power).toBe('off');
    be.childPower('atlas', 'win-test', true);
    expect(be.snapshot().vms.find(v => v.id === 'atlas')!.children.find(c => c.name === 'win-test')!.power).toBe('on');
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

  it('creates a VM with the coding agents chosen and switches one for the next reprovision', () => {
    const be = new FakeBackend({ timers: false });
    be.createVm({ where: 'local', name: 'quill', agents: ['claude-code', 'antigravity'] });
    expect(be.snapshot().vms.find(v => v.name === 'quill')!.settings.agents).toEqual({ 'claude-code': true, codex: false, opencode: false, antigravity: true });
    be.saveSettings('atlas', { agents: { antigravity: true } });
    const atlas = be.snapshot().vms.find(v => v.name === 'atlas')!;
    expect(atlas.settings.agents.antigravity).toBe(true);
    expect(atlas.changeWaits).toBe('Settings');
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

  it('signs in to an agent with a code typed, and fails a wrong one', () => {
    const be = new FakeBackend({ tick: 10 });
    expect(be.snapshot().vms[0].agents.find(a => a.id === 'codex')!.signedIn).toBe(false);
    be.signIn('atlas', 'claude-code');
    expect(be.snapshot().signIn).toMatchObject({ vmId: 'atlas', agent: 'claude-code', state: 'starting' });
    run(15);
    // It goes on in the browser; the CLI's own paste stays.
    expect(be.snapshot().signIn).toMatchObject({ state: 'running', browser: true });
    expect(be.snapshot().signIn!.text).toContain('Paste code here');
    be.signInInput({ text: 'bad' });
    run(15);
    expect(be.snapshot().signIn).toMatchObject({ state: 'failed' });
    expect(be.snapshot().signIn!.text).toContain('Invalid code');
    be.closeSignIn();
    expect(be.snapshot().signIn).toBeNull();

    be.signIn('atlas', 'antigravity');
    run(15);
    be.signInInput({ key: 'down' });
    be.signInInput({ text: '4/0Acode' });
    run(15);
    // Signed in: the panel goes, and the agent shows as signed in (reported now, if it was not before).
    expect(be.snapshot().signIn).toBeNull();
    expect(be.snapshot().vms[0].agents.find(a => a.id === 'antigravity')).toMatchObject({ signedIn: true, installed: true });
  });

  it('puts a lost sign-in away, and a sign-in clears it', () => {
    const be = new FakeBackend({ scene: 'signed-out', tick: 10 });
    const claude = () => be.snapshot().vms[0].agents.find(a => a.id === 'claude')!;
    expect(claude().lostAt).toBeTruthy();
    be.dismissSignInNotice('atlas', 'claude-code');
    expect(claude().lostAt).toBeNull();

    const again = new FakeBackend({ scene: 'signed-out', tick: 10 });
    again.signIn('atlas', 'claude-code');
    run(100);
    expect(again.snapshot().vms[0].agents.find(a => a.id === 'claude')).toMatchObject({ signedIn: true, lostAt: null });
  });

  it('signs OpenCode in with an OpenRouter key, or opens its own list', () => {
    const be = new FakeBackend({ tick: 10 });
    be.signIn('atlas', 'opencode');
    expect(be.snapshot().signIn).toMatchObject({ agent: 'opencode', state: 'key' });
    be.signIn('atlas', 'opencode', 'list');
    run(15);
    expect(be.snapshot().signIn).toMatchObject({ agent: 'opencode', state: 'running' });
    expect(be.snapshot().signIn!.text).toContain('Select integration');
    be.signInWithKey('atlas', 'sk-or-v1-test');
    expect(be.snapshot().signIn!.text).not.toContain('sk-or-v1-test');
    run(30);
    expect(be.snapshot().signIn).toBeNull();
    expect(be.snapshot().vms[0].agents.find(a => a.id === 'opencode')!.signedIn).toBe(true);
  });

  it('signs Codex in when its browser sign-in came back, and a new sign-in replaces one that runs', () => {
    const be = new FakeBackend({ tick: 10 });
    be.signIn('atlas', 'opencode');
    run(15);
    be.signIn('atlas', 'codex');
    expect(be.snapshot().signIn).toMatchObject({ agent: 'codex' });
    run(100);
    expect(be.snapshot().signIn).toBeNull();
    expect(be.snapshot().vms[0].agents.find(a => a.id === 'codex')!.signedIn).toBe(true);
  });

  it('repairs a job with warnings only when asked, then reprovisions', () => {
    const be = new FakeBackend({ scene: 'warned', tick: 10 });
    be.setRepairChat(be.snapshot().vms.find(v => v.id === 'atlas')!.hostId, true);
    run(500);
    let j = warnedJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ canRepair: true, repair: null });
    be.startRepair(j.id);
    run(500);
    j = warnedJob(be.snapshot(), 'atlas')!;
    expect(j).toMatchObject({ canRepair: false, repair: { state: 'asking' } });
    be.replyRepair(j.id, 'stop them');
    run(40);
    expect(warnedJob(be.snapshot(), 'atlas')!.repair!.state).toBe('done');
    run(40);
    expect(activeJob(be.snapshot(), 'atlas')).toMatchObject({ kind: 'reprovision', state: 'running' });
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

describe('VM storage relocation', () => {
  it('updates host default path without moving VMs, or moves all VMs when requested', () => {
    const be = new FakeBackend({ timers: false });
    be.setHostStoragePath('pc', 'D:\\ConstructVMs', false);
    let pc = be.snapshot().hosts.find(h => h.id === 'pc')!;
    expect(pc.vmStorageRoot).toBe('D:\\ConstructVMs');
    expect(pc.vms.find(v => v.name === 'atlas')!.diskPath).toContain('C:\\ProgramData\\');

    be.setHostStoragePath('pc', 'E:\\FastVMs', true);
    pc = be.snapshot().hosts.find(h => h.id === 'pc')!;
    expect(pc.vmStorageRoot).toBe('E:\\FastVMs');
    expect(pc.vms.every(v => v.diskPath === `E:\\FastVMs\\${v.name}.vhdx`)).toBe(true);
    expect(be.snapshot().vms.find(v => v.id === 'atlas')!.diskPath).toBe('E:\\FastVMs\\atlas.vhdx');
  });

  it('moves an individual VM disk to a chosen folder', () => {
    const be = new FakeBackend({ timers: false });
    be.moveVmStorage('pc', 'atlas', 'F:\\SingleVm');
    expect(be.snapshot().vms.find(v => v.id === 'atlas')!.diskPath).toBe('F:\\SingleVm\\atlas.vhdx');
    expect(be.snapshot().hosts.find(h => h.id === 'pc')!.vms.find(v => v.name === 'atlas')!.diskPath).toBe('F:\\SingleVm\\atlas.vhdx');
  });
});
