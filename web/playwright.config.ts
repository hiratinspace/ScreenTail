import { defineConfig, devices } from '@playwright/test';

// The web admin in a real browser against the real backend (ST-099 follow-up). e2e/harness.ts starts the
// backend and the built page; docs/dev/web-admin.md, section 6, says what it needs.
export default defineConfig({
  testDir: 'e2e',
  testMatch: '**/*.e2e.ts',
  globalSetup: './e2e/harness.ts',
  // One worker: the suite is a few tests, each in a tenant of its own, and one at a time keeps it plain.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: 'e2e-report' }]] : 'list',
  outputDir: 'e2e-results',
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1366, height: 768 },
    // Kept only when a test fails: the pages show this run's made-up tenant and nothing else.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    permissions: ['clipboard-read', 'clipboard-write'],
    serviceWorkers: 'block',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
