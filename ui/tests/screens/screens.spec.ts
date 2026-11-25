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
  ['pop', 'everything', 'everything'],
  ['pop', 'vm-off', 'off'],
  ['pop', 'failed', 'failed-reinstall'],
  ['pop', 'warned', 'warned'],
  ['pop', 'change-waits', 'changed'],
  ['pop', 'vault-request', 'lease'],
  // Files a scrub found still holding a secret: one yellow card per VM, Redact all for several.
  ['pop', 'vault-files', 'files'],
  ['pop', 'job', 'job'],
  ['pop', 'repair', 'repair'],
  ['alone', 'vault-request', 'lease'],
  ['alone', 'new-secret', 'enter'],
  ['alone', 'vault-file', 'file'],
  ['alone', 'signed-out', 'signed-out'],
  ['pop', 'signed-out', 'signed-out'],
  ['win', 'signed-out', 'signed-out'],
  // A lost sign-in asks on every page of the window, as a vault request does (finding 43).
  ['win', 'signed-out-vault', 'signed-out', 'vault'],
  ['win', 'overview', 'calm'],
  ['win', 'vm-off', 'off'],
  ['win', 'job', 'job'],
  ['win', 'warned', 'warned'],
  ['win', 'connections', 'calm', 'connections'],
  ['win', 'settings', 'calm', 'settings'],
  ['win', 'settings-agents-unknown', 'old-guest', 'settings/agents'],
  ['win', 'repair', 'repair'],
  // With `repair.chat` the repair is answered in Construct's chat instead of T3 Code.
  ['win', 'repair-chat', 'repair-chat'],
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
  ['win', 'vault-files', 'files', 'vault'],
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
  ['win', 'upgrade-intro', 'upgrade-intro'],
  ['win', 'upgrade-engine', 'upgrade-engine'],
  ['win', 'upgrade-name', 'upgrade-name'],
  ['win', 'upgrade-convert', 'upgrade-convert'],
  ['win', 'upgrade-code', 'upgrade-code'],
  ['win', 'upgrade-host', 'upgrade-host'],
  ['win', 'upgrade-done', 'upgrade-done'],
  ['win', 'upgrade-restart', 'upgrade-restart'],
  ['win', 'first-run', 'first-run'],
  ['win', 'first-run-restart', 'first-run-restart'],
  ['phone', 'home', 'calm'],
  ['phone', 'vault-request', 'enter'],
  ['phone', 'vault-files', 'files'],
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

test('the repair in a T3 Code thread opens there and has no reply box', async ({ page }) => {
  await open(page, 'win', 'repair', 'dark');
  await expect(page.getByRole('textbox', { name: 'Reply' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Open in T3 Code' }).click();
  await expect(page.getByRole('status')).toHaveText('T3 Code opens on the repair thread');
});

test('the repair agent in Construct\'s chat takes a reply', async ({ page }) => {
  await open(page, 'win', 'repair-chat', 'dark');
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

test('the pop-out disables text selection on chrome while keeping inputs, logs and vault file paths selectable', async ({ page }) => {
  await open(page, 'pop', 'calm', 'dark');
  const header = page.locator('.hd');
  await expect(page.locator('.app.s-pop')).toHaveCSS('user-select', 'none');
  await expect(page.locator('details.who summary')).toHaveCSS('user-select', 'none');
  const box = (await header.boundingBox())!;
  await page.mouse.move(box.x + 4, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 4, box.y + box.height / 2);
  await page.mouse.up();
  expect(await page.evaluate(() => getSelection()?.toString() ?? '')).toBe('');

  await open(page, 'pop', 'enter', 'dark');
  await expect(page.locator('input')).toHaveCSS('user-select', 'text');

  await open(page, 'pop', 'failed-reinstall', 'dark');
  await expect(page.locator('.logbox pre.code')).toHaveCSS('user-select', 'text');

  await open(page, 'pop', 'files', 'dark');
  await expect(page.locator('code.vfp').first()).toHaveCSS('user-select', 'all');
});

test('floating surfaces close open dropdown menus when blurred, hidden, escaped, or clicked outside and restore fitted height', async ({ page }) => {
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    const listeners = new Set<(e: { data?: unknown }) => void>();
    (window as unknown as { __sent: unknown[]; __emitHost: (data: unknown) => void }).__sent = sent;
    (window as unknown as { __emitHost: (data: unknown) => void }).__emitHost = data => {
      for (const fn of listeners) fn({ data });
    };
    (window as unknown as { chrome: unknown }).chrome = {
      webview: {
        postMessage: (m: unknown) => { sent.push(m); },
        addEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.add(fn); },
        removeEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.delete(fn); },
      },
    };
  });
  await open(page, 'pop', 'calm', 'dark');
  const sizes = () => page.evaluate(() => (window as unknown as { __sent: { type: string; height: number }[] }).__sent.filter(m => m.type === 'size').map(m => m.height));

  const vmMenu = page.locator('details.who');
  const openMenu = async () => {
    await vmMenu.locator('summary').click();
    await expect(vmMenu).toHaveAttribute('open', '');
  };

  // Losing focus (clicking off the pop-out) closes the VM switcher menu.
  await openMenu();
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(vmMenu).not.toHaveAttribute('open', '');

  // Desktop shell hide notification closes the VM switcher menu.
  await openMenu();
  await page.evaluate(() => (window as unknown as { __emitHost: (d: unknown) => void }).__emitHost({ type: 'hide' }));
  await expect(vmMenu).not.toHaveAttribute('open', '');

  // Page becoming hidden (visibilitychange) closes the menu.
  await openMenu();
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    document.dispatchEvent(new Event('visibilitychange'));
    delete (document as unknown as { hidden?: boolean }).hidden;
  });
  await expect(vmMenu).not.toHaveAttribute('open', '');

  // Pressing Escape closes the menu.
  await openMenu();
  await page.keyboard.press('Escape');
  await expect(vmMenu).not.toHaveAttribute('open', '');

  // Clicking outside the menu inside the pop-out closes it.
  await openMenu();
  await page.locator('.host').click();
  await expect(vmMenu).not.toHaveAttribute('open', '');

  // Clicking a disabled control outside the menu also closes it.
  await open(page, 'pop', 'off', 'dark');
  await page.locator('details.who summary').click();
  await expect(page.locator('details.who')).toHaveAttribute('open', '');
  await page.getByRole('button', { name: 'Reinstall' }).click({ force: true });
  await expect(page.locator('details.who')).not.toHaveAttribute('open', '');

  // Secondary update menu also closes on blur.
  await open(page, 'pop', 'update', 'dark');
  const moreMenu = page.locator('details.more');
  await moreMenu.locator('summary').click();
  await expect(moreMenu).toHaveAttribute('open', '');
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(moreMenu).not.toHaveAttribute('open', '');

  // A menu that extends past the surface grows the fitted height and restores it on blur/hide.
  await open(page, 'alone', 'lease', 'dark');
  const baseHeight = Math.ceil((await page.locator('.alone').boundingBox())!.height);
  await expect.poll(async () => (await sizes()).at(-1)).toBe(baseHeight);
  const spanMenu = page.locator('details.more');
  await spanMenu.locator('summary').click();
  await expect(spanMenu).toHaveAttribute('open', '');
  await expect.poll(async () => (await sizes()).at(-1)).toBeGreaterThan(baseHeight);
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(spanMenu).not.toHaveAttribute('open', '');
  await expect.poll(async () => (await sizes()).at(-1)).toBe(baseHeight);

  await spanMenu.locator('summary').click();
  await expect(spanMenu).toHaveAttribute('open', '');
  await expect.poll(async () => (await sizes()).at(-1)).toBeGreaterThan(baseHeight);
  await page.evaluate(() => (window as unknown as { __emitHost: (d: unknown) => void }).__emitHost({ type: 'hide' }));
  await expect(spanMenu).not.toHaveAttribute('open', '');
  await expect.poll(async () => (await sizes()).at(-1)).toBe(baseHeight);
});

test('the overview updates or signs in a coding agent in one click', async ({ page }) => {
  await open(page, 'win', 'calm', 'light');
  const codex = page.locator('tr', { hasText: 'Codex' });
  await codex.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('group', { name: 'Sign in to Codex' })).toBeVisible();
  await page.getByRole('group', { name: 'Sign in to Codex' }).getByRole('button', { name: 'Cancel' }).click();
  await codex.getByRole('button', { name: 'Update' }).click();
  await expect(page.locator('.blk.job .bh b')).toHaveText('Updating');
  await expect(page.locator('.blk.job .lbl')).toHaveText('Updating Codex');
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

test('files that still hold a secret are answered in the vault card: Delete asks in place, Redact all takes the rest', async ({ page }) => {
  await open(page, 'win', 'files', 'light', 'vault');
  const card = page.getByRole('alertdialog', { name: 'Secrets in files on atlas' });
  const first = card.getByRole('group', { name: '/root/.secrets/forge.json' });
  await first.getByRole('button', { name: 'Delete' }).click();
  await first.getByRole('button', { name: 'Cancel' }).click();
  await expect(first.getByRole('button', { name: 'Keep' })).toBeVisible();
  await first.getByRole('button', { name: 'Keep' }).click();
  await expect(page.getByRole('status')).toHaveText('Kept forge.json');
  await expect(first).toHaveCount(0);
  // One file left: its own Redact is the coloured one, and there is no Redact all.
  await expect(card.getByRole('button', { name: 'Redact all' })).toHaveCount(0);
  await expect(card.getByRole('button', { name: 'Redact', exact: true })).toHaveClass(/pri/);
  await card.getByRole('button', { name: 'Delete' }).click();
  await card.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('status')).toHaveText('Deleting .env.local');
  await expect(card).toHaveCount(0);

  await open(page, 'pop', 'files', 'dark');
  await page.getByRole('button', { name: 'Redact all' }).click();
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Control panel' })).toBeVisible();
});

test('a file answer that fails brings the file back to the lone card, which stays open for another try', async ({ page }) => {
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    (window as unknown as { __sent: unknown[] }).__sent = sent;
    (window as unknown as { chrome: unknown }).chrome = { webview: { postMessage: (m: unknown) => { sent.push(m); } } };
  });
  await open(page, 'alone', 'file-fails', 'dark');
  const closes = () => page.evaluate(() => (window as unknown as { __sent: { type: string }[] }).__sent.filter(m => m.type === 'close').length);
  const card = page.getByRole('alertdialog', { name: 'Secrets in files on atlas' });
  await card.getByRole('button', { name: 'Redact', exact: true }).click();
  // The fake engine refuses this first answer after 50 ms.
  await page.waitForTimeout(300);
  await expect(card.getByRole('group', { name: '/root/.secrets/forge.json' })).toHaveCount(1);
  expect(await closes()).toBe(0);
  await card.getByRole('button', { name: 'Redact', exact: true }).click();
  await expect.poll(closes).toBe(1);
});

test('the lone vault card of a VM\'s files stays until each file is answered, then closes the surface', async ({ page }) => {
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    (window as unknown as { __sent: unknown[] }).__sent = sent;
    (window as unknown as { chrome: unknown }).chrome = { webview: { postMessage: (m: unknown) => { sent.push(m); } } };
  });
  await open(page, 'alone', 'files', 'dark');
  const closes = () => page.evaluate(() => (window as unknown as { __sent: { type: string }[] }).__sent.filter(m => m.type === 'close').length);
  const card = page.getByRole('alertdialog', { name: 'Secrets in files on atlas' });
  await card.getByRole('group', { name: '/root/.secrets/forge.json' }).getByRole('button', { name: 'Redact' }).click();
  await expect(card.getByRole('group')).toHaveCount(1);
  expect(await closes()).toBe(0);
  await card.getByRole('button', { name: 'Redact', exact: true }).click();
  await expect.poll(closes).toBe(1);
});

test('pop-out shows Loading instead of New VM while initial VMs load, and re-posts height on shell show', async ({ page }) => {
  let releaseVms!: () => void;
  const vmsGate = new Promise<void>(r => { releaseVms = r; });
  await page.addInitScript(() => {
    const sent: unknown[] = [];
    const listeners = new Set<(e: { data?: unknown }) => void>();
    (window as unknown as { __sent: unknown[]; __emitShell: (data: unknown) => void }).__sent = sent;
    (window as unknown as { __emitShell: (data: unknown) => void }).__emitShell = (data: unknown) => {
      for (const fn of listeners) fn({ data });
    };
    (window as unknown as { chrome: unknown }).chrome = {
      webview: {
        postMessage: (m: unknown) => { sent.push(m); },
        addEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.add(fn); },
        removeEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.delete(fn); },
      },
    };
  });
  const e1 = 'engine1';
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/events') { await route.fulfill({ status: 200, contentType: 'text/event-stream', body: ': connected\n\n' }); return; }
    if (p === '/api/health') { await route.fulfill({ json: { status: 'ok', version: '3.1.0', pid: 1, startedAt: '2026-11-24T00:00:00Z' } }); return; }
    if (p === '/api/state') { await route.fulfill({ json: { device: { id: 'd1', name: 'pc', account: 'a1', registered: true, storeEngine: e1, protection: 'dpapi', protectionWeak: false, recoveryUnconfirmed: false }, engines: [{ id: e1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'AB', engineId: 'x', enrolled: true, signedIn: false, store: true }], pairing: null } }); return; }
    if (p === '/api/upgrade') { await route.fulfill({ json: { legacy: true, stage: 'converted', steps: [], found: null, storeEngine: e1, localEngine: null, items: [], notes: [], oldWriters: [], running: false, error: null, oldHosts: [] } }); return; }
    if (p === `/api/engines/${e1}/api/v1/whoami`) { await route.fulfill({ json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: 'admin', maxVms: 5, allowHostForwards: true } }); return; }
    if (p === `/api/engines/${e1}/api/v1/vms`) {
      await vmsGate;
      await route.fulfill({ json: [{ name: 'atlas', owner: 'alex', cpu: 4, ramGb: 8, diskGb: 60, created: '2026-11-01T10:00:00Z', state: 'running', sshForwardPort: 2201, publicHost: 'host.test', deleting: false, idlePolicy: { timeoutMinutes: 30, action: 'save', maxTimeoutMinutes: 0, clamped: false }, forwards: [], kind: 'primary', parent: null, sharing: 'private', shared: false, resourceUsage: { observedAt: null, stale: false, state: null, cpuUsagePercent: 12, memoryAssignedBytes: 4 * 1024 ** 3, memoryDemandBytes: null, diskFileBytes: 20 * 1024 ** 3, uptimeSeconds: 1 }, pendingCpu: null, pendingRamGb: null }] });
      return;
    }
    if (p === `/api/engines/${e1}/api/v1/host/status`) {
      // Simulate a slow admin endpoint: must never hold back showing the VM.
      await new Promise(() => {});
    }
    if (p.endsWith('/jobs') || p === '/api/devices' || p === '/api/notices/dismissed' || p === '/api/connections' || p.endsWith('/profiles') || p.endsWith('/profile-conflicts') || p === '/api/vault/secrets' || p === '/api/vault/history') {
      await route.fulfill({ json: [] }); return;
    }
    if (p === '/api/vault/approvals') { await route.fulfill({ json: { approvals: [], unreachable: [] } }); return; }
    if (p === '/api/vault/entry-requests') { await route.fulfill({ json: { requests: [], unreachable: [] } }); return; }
    if (p === '/api/vault/files') { await route.fulfill({ json: { files: [], unreachable: [] } }); return; }
    if (p === '/api/update') { await route.fulfill({ json: { version: '3.1.0', channel: 'stable', available: null, state: 'idle', error: null, installed: true, checkedAt: null } }); return; }
    if (p === '/api/device/settings') { await route.fulfill({ json: { channel: 'stable', autoUpdate: true, startWithOs: true } }); return; }
    if (p === '/api/device/features') { await route.fulfill({ json: { platform: 'windows', notifications: true, microphone: true, share: true, startWithOs: true, shell: true, engineHost: true } }); return; }
    if (p === '/api/device/ssh-key') { await route.fulfill({ json: { publicKey: 'ssh-ed25519 AAAA', path: 'k' } }); return; }
    if (p === '/api/update/everything') { await route.fulfill({ json: { running: false, parts: [] } }); return; }
    if (p === `/api/engines/${e1}/api/v1/health`) { await route.fulfill({ json: { status: 'ok', schemaVersion: 1, schemaMinReadableBy: 0, apiFeatures: [], maintenance: null, commit: 'c'.repeat(40), packageVersion: '3.1.0' } }); return; }
    if (p === `/api/engines/${e1}/api/v1/me/updates`) { await route.fulfill({ json: { autoReprovision: true } }); return; }
    await route.fulfill({ status: 404, json: { title: 'not found', status: 404 } });
  });

  await page.setViewportSize(SIZE.pop);
  await page.goto('index.html?surf=pop&theme=dark#agent-token=test-token');
  await expect(page.getByRole('status')).toHaveText('Loading…');
  await expect(page.getByRole('button', { name: 'New VM' })).toHaveCount(0);

  releaseVms();
  await expect(page.getByRole('button', { name: 'Control panel' })).toBeVisible();
  await expect(page.locator('.who summary')).toContainText('atlas');

  const before = await page.evaluate(() => (window as unknown as { __sent: { type: string }[] }).__sent.filter(m => m.type === 'size').length);
  await page.evaluate(() => (window as unknown as { __emitShell: (d: unknown) => void }).__emitShell({ type: 'show' }));
  await expect.poll(() => page.evaluate(() => (window as unknown as { __sent: { type: string }[] }).__sent.filter(m => m.type === 'size').length)).toBeGreaterThan(before);
});

test('pop-out shows Host unreachable instead of New VM when all hosts fail, and recovers on shell show', async ({ page }) => {
  await page.addInitScript(() => {
    const listeners = new Set<(e: { data?: unknown }) => void>();
    (window as unknown as { __emitShell: (data: unknown) => void }).__emitShell = (data: unknown) => {
      for (const fn of listeners) fn({ data });
    };
    (window as unknown as { chrome: unknown }).chrome = {
      webview: {
        postMessage: () => {},
        addEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.add(fn); },
        removeEventListener: (type: string, fn: (e: { data?: unknown }) => void) => { if (type === 'message') listeners.delete(fn); },
      },
    };
  });
  const e1 = 'engine1';
  let reachable = false;
  await page.route('**/api/**', async route => {
    const p = new URL(route.request().url()).pathname;
    if (p === '/api/events') { await route.fulfill({ status: 200, contentType: 'text/event-stream', body: ': connected\n\n' }); return; }
    if (p === '/api/health') { await route.fulfill({ json: { status: 'ok', version: '3.1.0', pid: 1, startedAt: '2026-11-24T00:00:00Z' } }); return; }
    if (p === '/api/state') { await route.fulfill({ json: { device: { id: 'd1', name: 'pc', account: 'a1', registered: true, storeEngine: e1, protection: 'dpapi', protectionWeak: false, recoveryUnconfirmed: false }, engines: [{ id: e1, name: 'orchard', url: 'https://orchard.test:8443', pin: 'AB', engineId: 'x', enrolled: true, signedIn: false, store: true }], pairing: null } }); return; }
    if (p === '/api/upgrade') { await route.fulfill({ json: { legacy: false, stage: 'none', steps: [], found: null, storeEngine: e1, localEngine: null, items: [], notes: [], oldWriters: [], running: false, error: null, oldHosts: [] } }); return; }
    if (p === `/api/engines/${e1}/api/v1/whoami`) {
      if (!reachable) { await route.fulfill({ status: 502, json: { title: 'The engine did not answer', status: 502, code: 'engine-unreachable' } }); return; }
      await route.fulfill({ json: { name: 'alex', kind: 'user', scheme: 'Device', known: true, role: 'user', maxVms: 5, allowHostForwards: true } }); return;
    }
    if (p === `/api/engines/${e1}/api/v1/vms`) {
      await route.fulfill({ json: [{ name: 'atlas', owner: 'alex', cpu: 4, ramGb: 8, diskGb: 60, created: '2026-11-01T10:00:00Z', state: 'running', sshForwardPort: 2201, publicHost: 'host.test', deleting: false, idlePolicy: { timeoutMinutes: 30, action: 'save', maxTimeoutMinutes: 0, clamped: false }, forwards: [], kind: 'primary', parent: null, sharing: 'private', shared: false, resourceUsage: { observedAt: null, stale: false, state: null, cpuUsagePercent: 12, memoryAssignedBytes: 4 * 1024 ** 3, memoryDemandBytes: null, diskFileBytes: 20 * 1024 ** 3, uptimeSeconds: 1 }, pendingCpu: null, pendingRamGb: null }] }); return;
    }
    if (p.endsWith('/jobs') || p === '/api/devices' || p === '/api/notices/dismissed' || p === '/api/connections' || p.endsWith('/profiles') || p.endsWith('/profile-conflicts') || p === '/api/vault/secrets' || p === '/api/vault/history') {
      await route.fulfill({ json: [] }); return;
    }
    if (p === '/api/vault/approvals') { await route.fulfill({ json: { approvals: [], unreachable: [] } }); return; }
    if (p === '/api/vault/entry-requests') { await route.fulfill({ json: { requests: [], unreachable: [] } }); return; }
    if (p === '/api/vault/files') { await route.fulfill({ json: { files: [], unreachable: [] } }); return; }
    if (p === '/api/update') { await route.fulfill({ json: { version: '3.1.0', channel: 'stable', available: null, state: 'idle', error: null, installed: true, checkedAt: null } }); return; }
    if (p === '/api/device/settings') { await route.fulfill({ json: { channel: 'stable', autoUpdate: true, startWithOs: true } }); return; }
    if (p === '/api/device/features') { await route.fulfill({ json: { platform: 'windows', notifications: true, microphone: true, share: true, startWithOs: true, shell: true, engineHost: true } }); return; }
    if (p === '/api/device/ssh-key') { await route.fulfill({ json: { publicKey: 'ssh-ed25519 AAAA', path: 'k' } }); return; }
    if (p === '/api/update/everything') { await route.fulfill({ json: { running: false, parts: [] } }); return; }
    if (p === `/api/engines/${e1}/api/v1/health`) { await route.fulfill({ json: { status: 'ok', schemaVersion: 1, schemaMinReadableBy: 0, apiFeatures: [], maintenance: null, commit: 'c'.repeat(40), packageVersion: '3.1.0' } }); return; }
    if (p === `/api/engines/${e1}/api/v1/me/updates`) { await route.fulfill({ json: { autoReprovision: true } }); return; }
    await route.fulfill({ status: 404, json: { title: 'not found', status: 404 } });
  });

  await page.setViewportSize(SIZE.pop);
  await page.goto('index.html?surf=pop&theme=dark#agent-token=test-token');
  await expect(page.getByText('Host unreachable')).toBeVisible();
  await expect(page.getByText('orchard: The engine did not answer')).toBeVisible();
  await expect(page.getByRole('button', { name: 'New VM' })).toHaveCount(0);

  reachable = true;
  await page.evaluate(() => (window as unknown as { __emitShell: (d: unknown) => void }).__emitShell({ type: 'show' }));
  await expect(page.getByRole('button', { name: 'Control panel' })).toBeVisible();
  await expect(page.locator('.who summary')).toContainText('atlas');
});
