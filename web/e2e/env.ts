/** The names the global setup (harness.ts) hands the workers its world under. */
export const env = {
  api: 'SCREENTAIL_E2E_API_URL',
  configured: 'SCREENTAIL_E2E_CONFIGURED_URL',
  unconfigured: 'SCREENTAIL_E2E_UNCONFIGURED_URL',
  clientId: 'SCREENTAIL_E2E_CLIENT_ID',
  run: 'SCREENTAIL_E2E_RUN',
  keyId: 'SCREENTAIL_E2E_KEY_ID',
  privateKey: 'SCREENTAIL_E2E_PRIVATE_KEY',
  downloadUrl: 'SCREENTAIL_E2E_DOWNLOAD_URL',
} as const;

/** The addresses on the signup list, per run, so a run never meets an owner a previous run made. */
export function allowed(run: string): string[] {
  return ['owner'].map((who) => `${who}-${run}@e2e.example`);
}
