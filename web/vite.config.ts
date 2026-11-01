import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';
import { appHeaders, baseHeaders } from './security-headers.ts';

// ST-099. In development the page and the API share an origin through this proxy, so the admin
// session's cookie (SameSite=Strict, host-only) is the page's own. SCREENTAIL_API points it at a
// backend other than the default one (docs/dev/web-admin.md).
const api = process.env.SCREENTAIL_API ?? 'http://localhost:5000';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    // Not the CSP: the dev page runs Vite's inline scripts and styles. Preview has the whole set.
    headers: baseHeaders,
    proxy: {
      '/v1': { target: api, changeOrigin: false },
    },
  },
  preview: {
    port: 4173,
    // The headers a deployment sends, so a local Lighthouse run sees the real policy (ADR-0009).
    headers: appHeaders,
    proxy: {
      '/v1': { target: api, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    // Testing Library's automatic cleanup between tests needs a global afterEach.
    globals: true,
  },
});
