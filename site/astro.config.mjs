// @ts-check
import { defineConfig } from 'astro/config';
import { site } from './src/site.config.ts';

// A fully static site: every page is HTML built ahead of time, served by Cloudflare as static assets.
// The only code that runs on a request is worker/index.ts, and only for /api/*.
export default defineConfig({
  site: site.url,
  output: 'static',
  trailingSlash: 'always',
  build: {
    format: 'directory',
    // Every stylesheet is a file, never a <style> element, so the Content-Security-Policy can say
    // style-src 'self' with no 'unsafe-inline' (scripts/check-dist.mjs holds this).
    inlineStylesheets: 'never',
    assets: '_astro',
  },
  vite: {
    build: {
      // Scripts are files too, for the same reason; Vite would inline a small one as a data URL.
      assetsInlineLimit: 0,
    },
  },
  devToolbar: { enabled: false },
  // No telemetry: Astro's own is turned off for this project in package.json's scripts environment too.
});
