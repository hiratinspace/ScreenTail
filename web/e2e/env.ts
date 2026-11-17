/** The names the global setup (harness.ts) hands the workers its world under. */
export const env = {
  api: 'SCREENTRAIL_E2E_API_URL',
  configured: 'SCREENTRAIL_E2E_CONFIGURED_URL',
  unconfigured: 'SCREENTRAIL_E2E_UNCONFIGURED_URL',
  clientId: 'SCREENTRAIL_E2E_CLIENT_ID',
  run: 'SCREENTRAIL_E2E_RUN',
  keyId: 'SCREENTRAIL_E2E_KEY_ID',
  // A path, never the key: the browser is started with the workers' environment.
  privateKeyFile: 'SCREENTRAIL_E2E_PRIVATE_KEY_FILE',
  downloadUrl: 'SCREENTRAIL_E2E_DOWNLOAD_URL',
} as const;

/** The addresses on the signup list, per run, so a run never meets an owner a previous run made. */
export function allowed(run: string): string[] {
  return ['owner'].map((who) => `${who}-${run}@e2e.example`);
}
