import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// ST-099. In development the page and the API share an origin through this proxy, so the admin
// session's cookie (SameSite=Strict, host-only) is the page's own. SCREENTAIL_API points it at a
// backend other than the default one (docs/dev/web-admin.md).
const api = process.env.SCREENTAIL_API ?? 'http://localhost:5000';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/v1': { target: api, changeOrigin: false },
    },
  },
  preview: {
    port: 4173,
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
