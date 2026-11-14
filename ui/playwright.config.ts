import { defineConfig } from '@playwright/test';

// Screenshot tests run against the built bundle (`npm run build` first), served by `vite preview`.
export default defineConfig({
  testDir: 'tests/screens',
  snapshotPathTemplate: '{testDir}/__screenshots__/{arg}{ext}',
  fullyParallel: true,
  workers: 2,
  reporter: 'list',
  expect: { toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled' } },
  use: { baseURL: 'http://127.0.0.1:4799/', browserName: 'chromium', headless: true },
  webServer: {
    command: 'npx vite preview --port 4799 --strictPort --host 127.0.0.1',
    url: 'http://127.0.0.1:4799/index.html',
    reuseExistingServer: false,
  },
});
