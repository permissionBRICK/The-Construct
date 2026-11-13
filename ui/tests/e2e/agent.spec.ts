import { expect, request, test, type Page } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, existsSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/* The UI on the real API: the engine (constructd in fake mode, with a scripted guest so a provision job runs
   to its end) and two device agents, each serving the built bundle. One browser drives both: first-run setup
   on the first device after confirming the host's fingerprint, a VM created through the UI, a reprovision to
   its end, a secret added and revealed, the VM asking for it through the engine's guest routes and the card
   allowing it, a profile edited and saved, a reinstall to its end, then the second device paired. */

const ui = fileURLToPath(new URL('../..', import.meta.url));
const repo = join(ui, '..');
const ENGINE = join(ui, 'tests/e2e/engine-host/bin/Release/net10.0/EngineHost.dll');
const AGENT = join(repo, 'agent/src/construct-agent/bin/Release/net10.0/construct-agent.dll');

const children: ChildProcess[] = [];
const dirs: string[] = [];

function startEngine(): Promise<{ url: string; pin: string; token: string; dir: string }> {
  // Its stdin stays open: the host runs until the test closes it.
  const p = spawn('dotnet', [ENGINE], { stdio: ['pipe', 'pipe', 'pipe'] });
  children.push(p);
  return new Promise((resolve, reject) => {
    let out = '';
    p.stdout!.on('data', (b: Buffer) => {
      out += b.toString();
      const line = out.split('\n').find(l => l.startsWith('{"url"'));
      if (line) resolve(JSON.parse(line));
    });
    p.on('exit', code => reject(new Error(`engine host exited ${code}`)));
  });
}

async function startAgent(name: string): Promise<{ base: string; token: string }> {
  const dir = mkdtempSync(join(tmpdir(), `ui-e2e-${name}-`));
  dirs.push(dir);
  // The file protection, as in the agent's own tests: no keyring on a build machine.
  const p = spawn('dotnet', [AGENT, 'run', '--data-dir', dir, '--ui', join(ui, 'dist')], { stdio: 'ignore', env: { ...process.env, DBUS_SESSION_BUS_ADDRESS: '' } });
  children.push(p);
  const file = join(dir, 'endpoint.json');
  for (let i = 0; i < 300 && !existsSync(file); i++) await new Promise(r => setTimeout(r, 100));
  const ep = JSON.parse(readFileSync(file, 'utf8')) as { port: number; token: string };
  return { base: `http://127.0.0.1:${ep.port}`, token: ep.token };
}

/** Opens the bundle the way `construct-agent open` does: the token in the fragment. */
async function open(page: Page, agent: { base: string; token: string }) {
  await page.goto(`${agent.base}/#agent-token=${agent.token}`);
  // The token leaves the address bar at once.
  await expect.poll(() => page.url()).not.toContain('agent-token');
}

/** The host's fingerprint, shown in place before the host is added, is the engine's; confirm it. */
async function confirmPin(page: Page, pin: string) {
  const box = page.getByRole('group', { name: 'Host fingerprint' });
  await expect(box).toBeVisible();
  expect((await box.locator('.fp').textContent())!.replace(/\s/g, '')).toBe(pin.toUpperCase());
  await box.getByRole('button', { name: 'It matches' }).click();
}

test.afterAll(() => {
  for (const c of children) { c.stdin?.end(); c.kill(); }
  for (const d of dirs) rmSync(d, { recursive: true, force: true });
});

test('first run, a VM, a reprovision and a second device on the real API', async ({ browser }) => {
  const engine = await startEngine();
  const [a, b] = await Promise.all([startAgent('a'), startAgent('b')]);
  const pa = await browser.newPage();
  const errors: string[] = [];
  pa.on('pageerror', e => errors.push(e.message));

  // First run on the first device: a new account, the recovery code shown once.
  await open(pa, a);
  await expect(pa.getByRole('heading', { name: 'This device' })).toBeVisible();
  await pa.getByLabel('Host address').fill(engine.url);
  await pa.getByLabel('Device name').fill('laptop');
  await pa.getByLabel('Token').fill(engine.token);
  await pa.getByRole('button', { name: 'Continue' }).click();
  await confirmPin(pa, engine.pin);
  await expect(pa.getByRole('heading', { name: 'Recovery code' })).toBeVisible();
  const groups = await pa.locator('.codebox span').allTextContents();
  expect(groups.length).toBeGreaterThan(3);
  await pa.getByLabel('I stored it').check();
  await pa.getByRole('button', { name: 'Continue' }).click();

  // No VM yet: the create screen. The engine on loopback is this PC's.
  await expect(pa.getByRole('heading', { name: 'New VM' })).toBeVisible();
  await expect(pa.locator('.codebox')).toHaveCount(0);
  await pa.getByLabel('Name').fill('alpha');
  await pa.getByRole('button', { name: 'Create' }).click();

  // The home screen shows the VM; it is created, then provisioned by the engine.
  await expect(pa.locator('.vp h1')).toHaveText('alpha');
  await expect(pa.locator('.vtab', { hasText: 'alpha' })).toBeVisible();
  await expect(pa.getByRole('status')).toHaveText('Reprovisioned alpha', { timeout: 90000 });
  await expect(pa.locator('.blk.job')).toHaveCount(0);

  // A reprovision started from the UI runs to its end.
  await pa.getByRole('button', { name: 'Reprovision' }).click();
  await expect(pa.locator('.blk.job')).toBeVisible();
  await expect(pa.locator('.blk.job .bh b')).toHaveText('Reprovisioning');
  await expect(pa.getByRole('status')).toHaveText('Reprovisioned alpha', { timeout: 90000 });
  await expect(pa.locator('.blk.job')).toHaveCount(0);
  await expect(pa.locator('.ey')).toContainText('Running');

  // A secret added in the vault and revealed for a moment.
  await pa.goto(`${a.base}/#/vault`);
  await pa.getByRole('button', { name: 'Add' }).click();
  await pa.getByLabel('Name').fill('deploy-token');
  await pa.getByLabel('Note').fill('deploys');
  await pa.getByLabel('Value').fill('e2e-secret-value');
  await pa.getByRole('button', { name: 'Save' }).click();
  const row = pa.locator('tr', { hasText: 'deploy-token' });
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Show' }).click();
  await expect(row.locator('code.reveal')).toHaveText('e2e-secret-value');
  await row.getByRole('button', { name: 'Hide' }).click();
  await expect(pa.getByText('e2e-secret-value')).toHaveCount(0);

  // The VM asks for it through the engine's guest route with its own token; the card names the VM and marks
  // the agent's words; Approve answers it and the VM's poll gets the answer.
  const admin = await request.newContext({ baseURL: engine.url, ignoreHTTPSErrors: true, extraHTTPHeaders: { Authorization: `Bearer ${engine.token}` } });
  const issued = await admin.post('/api/v1/vms/alpha/token', { data: {} });
  expect(issued.status()).toBe(200);
  const vmToken = (await issued.json()).vmToken as string;
  writeFileSync(join(engine.dir, 'vm-token'), `alpha ${vmToken}`);
  const guest = await request.newContext({ baseURL: engine.url, ignoreHTTPSErrors: true, extraHTTPHeaders: { Authorization: `VmToken ${vmToken}` } });
  const asked = await guest.post('/api/v1/vms/alpha/vault/requests', {
    data: { v: 1, op: 'request', names: ['deploy-token'], deadline: Date.now() + 600000, source: 'root@alpha', reason: 'push the release' },
  });
  expect(asked.status()).toBe(202);
  const requestId = (await asked.json()).id as string;
  const card = pa.getByRole('alertdialog', { name: 'Vault request for deploy-token' });
  await expect(card).toBeVisible();
  await expect(card.locator('.bh span')).toContainText('alpha ·');
  await expect(card.locator('.msg .tg')).toHaveText('agent');
  await expect(card.locator('.msg .quote')).toHaveText('push the release');
  await card.getByRole('button', { name: 'Approve' }).click();
  await expect(card).toHaveCount(0);
  // While the engine still applies the answer the poll has no body yet.
  await expect.poll(async () => {
    const r = await guest.get(`/api/v1/vms/alpha/vault/requests/${requestId}?wait=0`);
    const text = await r.text();
    return text ? JSON.parse(text).status : `${r.status()} pending`;
  }).toBe('ok');

  // A profile added, its file edited and saved through the store's engine.
  await pa.goto(`${a.base}/#/projects`);
  await pa.getByRole('button', { name: 'Add' }).click();
  await pa.getByLabel('Project name').fill('webapp');
  await pa.locator('.line').getByRole('button', { name: 'Add' }).click();
  await pa.locator('tr', { hasText: 'webapp' }).click();
  await pa.getByRole('tab', { name: 'File' }).click();
  await pa.getByRole('button', { name: 'Edit' }).click();
  const editor = pa.getByLabel('Profile JSON');
  const doc = JSON.parse(await editor.inputValue());
  doc.provisionCommands = ['echo e2e-edited'];
  await editor.fill(JSON.stringify(doc, null, 2));
  await pa.getByRole('button', { name: 'Save' }).click();
  await expect(pa.getByRole('status')).toHaveText('Saved');
  await expect(pa.locator('pre.code')).toContainText('echo e2e-edited');
  await pa.reload();
  await expect(pa.locator('pre.code')).toContainText('echo e2e-edited');

  // A reinstall asked in place runs to its end against the fake driver: backup, delete, create, provision, restore.
  await pa.goto(`${a.base}/#/home`);
  await pa.getByRole('button', { name: 'Reinstall' }).click();
  await expect(pa.getByText('Wipe alpha?')).toBeVisible();
  await pa.locator('.blk.bad.job').getByRole('button', { name: 'Reinstall' }).click();
  await expect(pa.locator('.blk.job .bh b')).toHaveText('Reinstalling');
  await expect(pa.getByRole('status')).toHaveText('Reinstalled alpha', { timeout: 120000 });
  await expect(pa.locator('.blk.job')).toHaveCount(0);
  await admin.dispose();
  await guest.dispose();

  // The second device asks to join; the first approves it by the code it shows, after comparing the words.
  const pb = await browser.newPage();
  pb.on('pageerror', e => errors.push(e.message));
  await open(pb, b);
  await pb.getByRole('radio', { name: 'Pair with a device' }).click();
  await pb.getByLabel('Host address').fill(engine.url);
  await pb.getByLabel('Device name').fill('tablet');
  await pb.getByRole('button', { name: 'Continue' }).click();
  await confirmPin(pb, engine.pin);
  await expect(pb.getByRole('heading', { name: 'Pair with your other device' })).toBeVisible();
  const code = (await pb.locator('.wz code').textContent())!.trim();
  const words = await pb.locator('.words span').allTextContents();

  await pa.goto(`${a.base}/#/devices`);
  await pa.getByRole('button', { name: 'Pair' }).click();
  await pa.getByLabel('Code from the new device').fill(code);
  await pa.getByRole('button', { name: 'Next' }).click();
  await expect(pa.locator('.blk.warn b')).toHaveText('tablet');
  expect(await pa.locator('.blk.warn .words span').allTextContents()).toEqual(words);
  await pa.getByRole('button', { name: 'Pair', exact: true }).click();
  await expect(pa.getByText('tablet paired')).toBeVisible();

  // The second device is in: its first run closes and it sees the account's VM through its own enrolment.
  await expect(pb.getByRole('heading', { name: 'Pair with your other device' })).toHaveCount(0, { timeout: 60000 });
  await expect(pb.locator('.vtab', { hasText: 'alpha' })).toBeVisible({ timeout: 60000 });
  // The device list is read again on the next refresh; a reload reads it now (the token stays with the tab).
  await pa.getByRole('button', { name: 'Done' }).click();
  await pa.reload();
  await expect(pa.locator('table').getByText('tablet')).toBeVisible();
  await expect(pa.locator('table').getByText('laptop')).toBeVisible();

  expect(errors).toEqual([]);
});
