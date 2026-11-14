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
  ['pop', 'change-waits', 'changed'],
  ['pop', 'vault-request', 'lease'],
  ['pop', 'job', 'job'],
  ['pop', 'repair', 'repair'],
  ['alone', 'vault-request', 'lease'],
  ['alone', 'new-secret', 'enter'],
  ['win', 'overview', 'calm'],
  ['win', 'repair', 'repair'],
  ['win', 'updating', 'updating'],
  ['win', 'projects', 'calm', 'projects'],
  ['win', 'project-file', 'calm', 'file/webshop'],
  ['win', 'settings-restart', 'restart', 'settings'],
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
  ['win', 'upgrade-code', 'upgrade-code'],
  ['win', 'upgrade-host', 'upgrade-host'],
  ['win', 'upgrade-done', 'upgrade-done'],
  ['phone', 'home', 'calm'],
  ['phone', 'vault-request', 'enter'],
  ['phone', 'projects', 'calm', 'projects'],
  ['phone', 'vault', 'calm', 'vault'],
  ['phone', 'construct', 'update', 'app'],
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

test('a secret is shown for a moment, copied, hidden, and replaced in place', async ({ page }) => {
  await open(page, 'win', 'calm', 'dark', 'vault');
  const row = page.locator('tr', { hasText: 'forge-token' });
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
