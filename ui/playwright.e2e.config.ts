import { defineConfig } from '@playwright/test';

// The end-to-end test: the real engine (fake mode) and two real device agents, the built bundle served by the
// agents, headless Chromium. `npm run test:e2e` builds the .NET parts first; build the bundle before.
export default defineConfig({
  testDir: 'tests/e2e',
  workers: 1,
  timeout: 240000,
  reporter: 'list',
  expect: { timeout: 30000 },
  use: { browserName: 'chromium', headless: true, viewport: { width: 1080, height: 720 } },
});
