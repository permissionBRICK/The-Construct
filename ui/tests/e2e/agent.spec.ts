import { expect, test, type Page } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/* The UI on the real API: the engine (constructd in fake mode, with a scripted guest so a provision job runs
   to its end) and two device agents, each serving the built bundle. One browser drives both: first-run setup
   on the first device, a VM created through the UI, a reprovision to its end, then the second device paired. */

const ui = fileURLToPath(new URL('../..', import.meta.url));
const repo = join(ui, '..');
const ENGINE = join(ui, 'tests/e2e/engine-host/bin/Release/net10.0/EngineHost.dll');
const AGENT = join(repo, 'agent/src/construct-agent/bin/Release/net10.0/construct-agent.dll');

const children: ChildProcess[] = [];
const dirs: string[] = [];

function startEngine(): Promise<{ url: string; pin: string; token: string }> {
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

  // The second device asks to join; the first approves it by the code it shows, after comparing the words.
  const pb = await browser.newPage();
  pb.on('pageerror', e => errors.push(e.message));
  await open(pb, b);
  await pb.getByRole('radio', { name: 'Pair with a device' }).click();
  await pb.getByLabel('Host address').fill(engine.url);
  await pb.getByLabel('Device name').fill('tablet');
  await pb.getByRole('button', { name: 'Continue' }).click();
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
