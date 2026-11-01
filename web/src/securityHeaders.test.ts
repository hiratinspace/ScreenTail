// @vitest-environment node
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { build, preview, type PreviewServer } from 'vite';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { appHeaders, baseHeaders, contentSecurityPolicy } from '../security-headers';
import viteConfig from '../vite.config';

const web = resolve(__dirname, '..');
const configFile = join(web, 'vite.config.ts');

describe('the content security policy', () => {
  it("follows Google's guidance for Google Identity Services and allows nothing else from elsewhere", () => {
    expect(contentSecurityPolicy['script-src']).toEqual(["'self'", 'https://accounts.google.com/gsi/client']);
    // Google's list plus inline styles, which the GIS client inserts itself (ADR-0009).
    expect(contentSecurityPolicy['style-src']).toEqual(["'self'", "'unsafe-inline'", 'https://accounts.google.com/gsi/style']);
    expect(contentSecurityPolicy['frame-src']).toEqual(['https://accounts.google.com/gsi/']);
    expect(contentSecurityPolicy['connect-src']).toEqual(["'self'", 'https://accounts.google.com/gsi/']);
    expect(contentSecurityPolicy['default-src']).toEqual(["'none'"]);
    expect(contentSecurityPolicy['frame-ancestors']).toEqual(["'none'"]);
    expect(contentSecurityPolicy['base-uri']).toEqual(["'none'"]);
    expect(contentSecurityPolicy['object-src']).toEqual(["'none'"]);
  });

  it('allows no inline script, no eval and no wildcard, and inline styles nowhere but style-src', () => {
    for (const [directive, sources] of Object.entries(contentSecurityPolicy)) {
      for (const loose of ["'unsafe-eval'", "'unsafe-hashes'", "'wasm-unsafe-eval'", 'data:', 'blob:', '*', 'http:', 'https:']) {
        expect(sources, directive).not.toContain(loose);
      }
      if (directive !== 'style-src') {
        expect(sources, directive).not.toContain("'unsafe-inline'");
      }
    }
    expect(appHeaders['Content-Security-Policy']).toContain("script-src 'self' https://accounts.google.com/gsi/client;");
  });

  it('turns off the browser features the page does not use, and keeps the related headers', () => {
    for (const feature of ['camera', 'microphone', 'geolocation', 'payment', 'usb', 'display-capture']) {
      expect(appHeaders['Permissions-Policy']).toContain(`${feature}=()`);
    }
    // FedCM: Google's sign-in may need it inside its own iframe, so it is left at the browser's default.
    expect(appHeaders['Permissions-Policy']).not.toContain('identity-credentials-get');
    expect(appHeaders['X-Content-Type-Options']).toBe('nosniff');
    expect(appHeaders['Referrer-Policy']).toBe('strict-origin-when-cross-origin');
    expect(appHeaders['Cross-Origin-Opener-Policy']).toBe('same-origin-allow-popups');
    expect(appHeaders['X-Frame-Options']).toBe('DENY');
  });

  it('is sent by vite preview whole, and by the dev server without the CSP its inline scripts need lifted', () => {
    expect(viteConfig.preview?.headers).toEqual(appHeaders);
    expect(viteConfig.server?.headers).toEqual(baseHeaders);
    expect(baseHeaders['Content-Security-Policy']).toBeUndefined();
  });
});

describe('the production build under that policy', () => {
  let outDir: string;
  let server: PreviewServer;

  beforeAll(async () => {
    outDir = mkdtempSync(join(tmpdir(), 'screentail-web-csp-'));
    await build({ root: web, configFile, logLevel: 'silent', build: { outDir, emptyOutDir: true } });
    server = await preview({ root: web, configFile, logLevel: 'silent', build: { outDir }, preview: { port: 0, strictPort: false, open: false } });
  }, 60_000);

  afterAll(async () => {
    await server?.close();
    rmSync(outDir, { recursive: true, force: true });
  });

  it('has no inline script, inline style or inline event handler in its page', () => {
    const html = readFileSync(join(outDir, 'index.html'), 'utf8');
    const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];

    expect(scripts.length).toBeGreaterThan(0);
    for (const [, attributes, body] of scripts) {
      expect(attributes).toMatch(/\bsrc="\/assets\//);
      expect(body!.trim()).toBe('');
    }
    expect(html).not.toMatch(/<style\b/i);
    expect(html).not.toMatch(/\sstyle=/i);
    expect(html).not.toMatch(/\son[a-z]+=/i);
  });

  it('is served by vite preview with every header a deployment sends', async () => {
    const address = server.resolvedUrls?.local[0];
    expect(address).toBeDefined();
    const page = await fetch(address!);

    expect(page.status).toBe(200);
    for (const [name, value] of Object.entries(appHeaders)) {
      expect(page.headers.get(name)).toBe(value);
    }
  });
});
