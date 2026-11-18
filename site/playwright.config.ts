import { defineConfig, devices } from '@playwright/test';

// Runs against the built site on Cloudflare's local runtime (wrangler dev), with the real _headers
// and the real Worker, so the CSP and the form are tested as they ship.
export default defineConfig({
  testDir: 'e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never', outputFolder: 'e2e-report' }]] : 'list',
  outputDir: 'e2e-results',
  use: { baseURL: 'http://127.0.0.1:8789', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'phone', use: { ...devices['Pixel 7'] } },
  ],
  webServer: {
    command: 'npx wrangler dev --port 8789 --ip 127.0.0.1 --log-level warn',
    url: 'http://127.0.0.1:8789/',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
