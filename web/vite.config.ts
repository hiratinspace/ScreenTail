import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    // Testing Library's automatic cleanup between tests needs a global afterEach.
    globals: true,
  },
});
