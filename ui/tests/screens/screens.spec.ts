import { expect, test, type Page } from '@playwright/test';

/* Screenshots of the main screens, light and dark, on the three surfaces. The fake backend starts in a
   scene with its timers off, so each picture is stable. */

type Surf = 'pop' | 'win' | 'phone' | 'alone';
const SIZE: Record<Surf, { width: number; height: number }> = {
  pop: { width: 380, height: 560 },
  win: { width: 1080, height: 680 },
  phone: { width: 390, height: 780 },
  alone: { width: 380, height: 240 },
};

const SHOTS: [Surf, string, string, string?][] = [
  // surface, name, scene, route
  ['pop', 'calm', 'calm'],
  ['pop', 'update', 'update'],
  // A nightly's version with its whole commit: short, nothing scrolls sideways (finding 18).
  ['pop', 'update-nightly', 'update-nightly'],
  ['pop', 'failed', 'failed-reinstall'],
  ['pop', 'warned', 'warned'],
  ['pop', 'change-waits', 'changed'],
  ['pop', 'vault-request', 'lease'],
  ['pop', 'job', 'job'],
  ['pop', 'repair', 'repair'],
  ['alone', 'vault-request', 'lease'],
  ['alone', 'new-secret', 'enter'],
  ['alone', 'signed-out', 'signed-out'],
  ['pop', 'signed-out', 'signed-out'],
  ['win', 'signed-out', 'signed-out'],
  // A lost sign-in asks on every page of the window, as a vault request does (finding 43).
  ['win', 'signed-out-vault', 'signed-out', 'vault'],
  ['win', 'overview', 'calm'],
  ['win', 'job', 'job'],
  ['win', 'warned', 'warned'],
  ['win', 'connections', 'calm', 'connections'],
  ['win', 'settings', 'calm', 'settings'],
  ['win', 'settings-agents-unknown', 'old-guest', 'settings/agents'],
  ['win', 'repair', 'repair'],
  ['win', 'updating', 'updating'],
  ['win', 'projects', 'calm', 'projects'],
  ['win', 'projects-upstream', 'upstream', 'projects'],
  ['win', 'project-file', 'calm', 'file/webshop'],
  ['win', 'settings-restart', 'restart', 'settings'],
  ['win', 'settings-agents', 'calm', 'settings/agents'],
  ['win', 'sign-in', 'sign-in', 'settings/agents'],
  ['win', 'sign-in-failed', 'sign-in-failed', 'settings/agents'],
  ['win', 'sign-in-browser', 'sign-in-browser', 'settings/agents'],
  ['win', 'sign-in-key', 'sign-in-key', 'settings/agents'],
  ['win', 'vault', 'calm', 'vault'],
  ['win', 'devices', 'calm', 'devices'],
  ['win', 'backups', 'calm', 'backups'],
  ['win', 'pair', 'pair-confirm', 'pair'],
  ['win', 'revoked', 'revoked', 'revoke/d2'],
  ['win', 'host-users', 'handout', 'host/orchard/users'],
  ['win', 'host-vms', 'calm', 'host/orchard/vms'],
  ['win', 'host-media', 'calm', 'host/orchard/media'],
  ['win', 'construct-update', 'update', 'app'],
  ['win', 'create-vm', 'calm', 'create'],
  ['win', 'create-sign-in', 'created', 'create/quill'],
  ['win', 'create-ready', 'created', 'create/quill/ready'],
  ['win', 'upgrade-convert', 'upgrade-convert'],
  ['win', 'upgrade-code', 'upgrade-code'],
  ['win', 'upgrade-host', 'upgrade-host'],
  ['win', 'upgrade-done', 'upgrade-done'],
  ['win', 'upgrade-restart', 'upgrade-restart'],
  ['win', 'first-run', 'first-run'],
  ['win', 'first-run-restart', 'first-run-restart'],
  ['phone', 'home', 'calm'],
  ['phone', 'vault-request', 'enter'],
  ['phone', 'projects', 'calm', 'projects'],
  ['phone', 'vault', 'calm', 'vault'],
  ['phone', 'construct', 'update', 'app'],
  ['phone', 'create-sign-in', 'created', 'create/quill'],
];

async function open(page: Page, surf: Surf, scene: string, theme: string, route = '') {
  await page.setViewportSize(SIZE[surf]);
  await page.goto(`index.html?surf=${surf}&theme=${theme}&scene=${scene}&timers=0#/${route}`);
  await page.evaluate(() => document.fonts.ready);
}

for (const theme of ['light', 'dark']) {
  for (const [surf, name, scene, route] of SHOTS) {
    test(`${surf} ${name} ${theme}`, async ({ page }) => {
      const errors: string[] = [];
      page.on('pageerror', e => errors.push(e.message));
      await open(page, surf, scene, theme, route);
      await expect(page).toHaveScreenshot(`${surf}-${name}-${theme}.png`);
      expect(errors).toEqual([]);
    });
  }
}

test('pop-out reinstall asks in place', async ({ page }) => {
  await open(page, 'pop', 'calm', 'dark');
  await page.getByRole('button', { name: 'Reinstall' }).click();
  await expect(page.getByText('Wipe atlas?')).toBeVisible();
  await expect(page).toHaveScreenshot('pop-reinstall-dark.png');
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByText('Wipe atlas?')).toHaveCount(0);
});

test('a memory change asks restart now or later', async ({ page }) => {
  await open(page, 'win', 'calm', 'light', 'settings');
  await page.getByRole('radiogroup', { name: 'Memory' }).getByRole('radio', { name: '32' }).click();
  await expect(page.getByText('Memory 16 → 32 GB')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Restart now' })).toBeVisible();
  await page.getByRole('button', { name: 'Later' }).click();
  // the restart still waits, shown in yellow with one button
  await expect(page.getByText('Memory 16 → 32 GB')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Later' })).toHaveCount(0);
});

test('a swap file size is checked, then waits for the next reprovision', async ({ page }) => {
  await open(page, 'win', 'calm', 'light', 'settings');
  const swap = page.getByRole('textbox', { name: 'Swap file' });
  // What it takes is in its tooltip, not in a line under it (finding 25).
  await expect(swap).toHaveAttribute('title', 'auto: half the RAM, at most 8 GB · 0: none');
  await expect(page.getByText('auto: half the RAM')).toHaveCount(0);
  await swap.fill('300');
  await swap.press('Enter');
  await expect(swap).toHaveAttribute('aria-invalid', 'true');
  // auto is saved even though it is shown already: unset only keeps what the VM has.
  await swap.fill('auto');
  await swap.press('Enter');
  await expect(page.getByText('Saved, applies at the next reprovision')).toBeVisible();
  await swap.fill(' 16 ');
  await swap.press('Enter');
  await expect(swap).toHaveAttribute('aria-invalid', 'false');
  await expect(swap).toHaveValue('16');
  await expect(page.getByText('Saved, applies at the next reprovision')).toBeVisible();
});

test('the repair agent takes a reply', async ({ page }) => {
  await open(page, 'win', 'repair', 'dark');
  await page.getByRole('textbox', { name: 'Reply' }).fill('wait for them');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.locator('.chat p.user')).toHaveText('wait for them');
});

test('colour schemes switch from the Construct page', async ({ page }) => {
  await open(page, 'win', 'calm', 'dark', 'app');
  for (const scheme of ['Blue', 'Copper', 'Black and white']) {
    await page.getByRole('radio', { name: scheme }).click();
    await expect(page).toHaveScreenshot(`win-scheme-${scheme.toLowerCase().replace(/ /g, '-')}-dark.png`);
  }
});

test('no native dialogs open', async ({ page }) => {
  let dialogs = 0;
  page.on('dialog', d => { dialogs++; void d.dismiss(); });
  await open(page, 'win', 'calm', 'light', 'vault');
  await page.locator('tbody tr').first().click();
  await page.getByRole('button', { name: 'Delete' }).first().click();
  await page.getByRole('button', { name: 'Cancel' }).click();
  await open(page, 'win', 'calm', 'light', 'devices');
  await page.getByRole('button', { name: 'Revoke' }).first().click();
  await page.getByRole('button', { name: 'Revoke' }).first().click();
  await expect(page.getByText('Revoked', { exact: true })).toBeVisible();
  expect(dialogs).toBe(0);
});

test('a hardware draft stays with its VM', async ({ page }) => {
  await open(page, 'win', 'calm', 'light', 'settings');
  await page.getByRole('radiogroup', { name: 'Memory' }).getByRole('radio', { name: '32' }).click();
  await expect(page.getByRole('button', { name: 'Later' })).toBeVisible();
  await page.getByRole('button', { name: /pebble/ }).click();
  await expect(page.getByRole('button', { name: 'Later' })).toHaveCount(0);
});

test('a remote host is added only after its fingerprint is confirmed in place', async ({ page }) => {
  await open(page, 'win', 'calm', 'light', 'create');
  await page.getByRole('radio', { name: 'Remote host' }).click();
  await page.getByLabel('Name').fill('quill');
  await page.getByLabel('Host address').fill('https://lab.example.net:8443');
  await page.getByRole('radio', { name: 'Token' }).click();
  await page.getByLabel('Token').fill('tok');
  await page.getByRole('button', { name: 'Create' }).click();
  const pin = page.getByRole('group', { name: 'Host fingerprint' });
  await expect(pin).toContainText('4F1C 9A22');
  await expect(page).toHaveScreenshot('win-create-pin-light.png');
  // A new address drops the confirm.
  await page.getByLabel('Host address').fill('https://other.example.net:8443');
  await expect(pin).toHaveCount(0);
  await page.getByRole('button', { name: 'Create' }).click();
  await page.getByRole('button', { name: 'It matches' }).click();
  await expect(page.locator('.vtab', { hasText: 'quill' })).toBeVisible();
});

test('a secret is one line; opened, it is shown for a moment, copied, hidden, and replaced in place', async ({ page }) => {
  await open(page, 'win', 'calm', 'dark', 'vault');
  const line = page.locator('tr', { hasText: 'forge-token' });
  // One compact line, the long note cut with an ellipsis and whole in its tooltip (finding 23).
  expect((await line.boundingBox())!.height).toBeLessThan(50);
  const note = line.locator('td.t');
  await expect(note).toHaveAttribute('title', /rotate it after every handover/);
  expect(await note.evaluate(e => e.scrollWidth > e.clientWidth)).toBe(true);
  await expect(page.getByRole('tablist').evaluate(e => e.scrollHeight > e.clientHeight)).resolves.toBe(false);
  await line.click();
  await expect(line).toHaveAttribute('aria-expanded', 'true');
  const row = page.locator('tr.more-row');
  await expect(row.locator('.note')).toContainText('rotate it after every handover to another team');
  await row.getByRole('button', { name: 'Show' }).click();
  await expect(row.locator('code.reveal')).toHaveText('forge-token-sample-value');
  await expect(page).toHaveScreenshot('win-vault-reveal-dark.png');
  await row.getByRole('button', { name: 'Hide' }).click();
  await expect(page.getByText('forge-token-sample-value')).toHaveCount(0);
  await row.getByRole('button', { name: 'Replace' }).click();
  await row.getByLabel('New value of forge-token').fill('new-value');
  await row.getByRole('button', { name: 'Save' }).click();
  await expect(row.getByLabel('New value of forge-token')).toHaveCount(0);
});

test('a job\'s log opens at its end', async ({ page }) => {
  await open(page, 'win', 'failed-reinstall', 'light');
  const log = page.locator('.logbox pre');
  await expect(log).toBeVisible();
  expect(await log.evaluate(e => e.scrollHeight > e.clientHeight && e.scrollTop + e.clientHeight >= e.scrollHeight - 4)).toBe(true);
});

test('a failure block is put away with its cross', async ({ page }) => {
  await open(page, 'pop', 'failed-reinstall', 'dark');
  await page.locator('.blk.bad.job').getByRole('button', { name: 'Dismiss' }).click();
  await expect(page.locator('.blk.bad.job')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Reinstall' })).toBeVisible();
});

test('the pop-out tells the desktop shell how tall its content is, an open menu included', async ({ page }) => {
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    (window as unknown as { __sent: unknown[] }).__sent = sent;
    (window as unknown as { chrome: unknown }).chrome = { webview: { postMessage: (m: unknown) => { sent.push(m); } } };
  });
  await open(page, 'pop', 'calm', 'dark');
  const sizes = () => page.evaluate(() => (window as unknown as { __sent: { type: string; height: number }[] }).__sent.filter(m => m.type === 'size').map(m => m.height));
  const pop = (await page.locator('.pop').boundingBox())!;
  await expect.poll(async () => (await sizes()).at(-1)).toBe(Math.ceil(pop.height));
  expect(pop.height).toBeLessThan(400);
  await page.locator('details.who summary').click();
  const menu = (await page.locator('details.who .vml').boundingBox())!;
  await expect.poll(async () => (await sizes()).at(-1)).toBeGreaterThanOrEqual(Math.ceil(menu.y + menu.height));
});

test('the overview updates or signs in a coding agent in one click', async ({ page }) => {
  await open(page, 'win', 'calm', 'light');
  const codex = page.locator('tr', { hasText: 'Codex' });
  await codex.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('group', { name: 'Sign in to Codex' })).toBeVisible();
  await page.getByRole('group', { name: 'Sign in to Codex' }).getByRole('button', { name: 'Cancel' }).click();
  await codex.getByRole('button', { name: 'Update' }).click();
  await expect(page.locator('.blk.job .bh b')).toHaveText('Reprovisioning');
  // Claude Code is signed in and up to date: nothing to click.
  await expect(page.locator('tr', { hasText: 'Claude Code' }).getByRole('button')).toHaveCount(0);
});

test('Connect opens a terminal on the VM; no command to copy', async ({ page }) => {
  await open(page, 'win', 'calm', 'light', 'connections');
  await expect(page.locator('code', { hasText: 'ssh ' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await expect(page.getByRole('status')).toHaveText('SSH opens atlas');
});

test('a failed reinstall shows its step and log, and asks again before Retry reinstalls', async ({ page }) => {
  await open(page, 'win', 'failed-reinstall', 'light');
  await expect(page.locator('.blk.bad.job .msg')).toContainText('Installing Ubuntu');
  await expect(page.locator('.logbox')).toBeVisible();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.locator('.blk.bad.job')).toBeVisible();
  await expect(page).toHaveScreenshot('win-reinstall-retry-light.png');
  await page.getByRole('button', { name: 'Reinstall again' }).click();
  await expect(page.locator('.blk.job .bh b')).toHaveText('Reinstalling');
});

test('cards popped alone give way to the next one and close the surface only when none is left', async ({ page }) => {
  // The desktop shell's host object, recording what the page asks of it.
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    (window as unknown as { __sent: unknown[] }).__sent = sent;
    (window as unknown as { chrome: unknown }).chrome = { webview: { postMessage: (m: unknown) => { sent.push(m); } } };
  });
  await open(page, 'alone', 'signed-out-two', 'dark');
  const closes = () => page.evaluate(() => (window as unknown as { __sent: { type: string }[] }).__sent.filter(m => m.type === 'close').length);
  const card = page.getByRole('alertdialog');
  await expect(card).toHaveCount(1);
  const first = await card.getAttribute('aria-label');
  await card.getByRole('button', { name: 'Dismiss' }).click();
  await expect(card).toHaveCount(1);
  await expect(card).not.toHaveAttribute('aria-label', first ?? '');
  expect(await closes()).toBe(0);
  await card.getByRole('button', { name: 'Dismiss' }).click();
  await expect.poll(closes).toBe(1);
});
