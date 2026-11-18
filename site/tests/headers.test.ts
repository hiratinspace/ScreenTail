import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// The static pages' headers (public/_headers) are the site's main defence; these tests keep them strict.
const file = readFileSync(fileURLToPath(new URL('../public/_headers', import.meta.url).href), 'utf8');
const global = file.split(/\n(?=\/)/).find((block) => block.startsWith('/*\n'));
const header = (name: string) => {
  const line = global?.split('\n').find((l) => l.trim().toLowerCase().startsWith(`${name.toLowerCase()}:`));
  return line?.slice(line.indexOf(':') + 1).trim();
};
const csp = Object.fromEntries(
  (header('Content-Security-Policy') ?? '').split(';').map((d) => d.trim()).filter(Boolean).map((d) => {
    const [name, ...values] = d.split(/\s+/);
    return [name, values];
  }),
);

describe('public/_headers', () => {
  it('has a block for every path', () => {
    expect(global).toBeDefined();
  });

  it('denies everything by default and allows only this site', () => {
    expect(csp['default-src']).toEqual(["'none'"]);
    for (const d of ['script-src', 'style-src', 'img-src', 'font-src', 'connect-src']) expect(csp[d]).toEqual(["'self'"]);
  });

  it('allows no inline code, eval or other sites anywhere', () => {
    const all = Object.values(csp).flat().join(' ');
    expect(all).not.toMatch(/unsafe-|https?:|\*|data:|blob:/);
  });

  it('cannot be framed, rebased or posted elsewhere, and requires Trusted Types', () => {
    expect(csp['frame-ancestors']).toEqual(["'none'"]);
    expect(csp['base-uri']).toEqual(["'none'"]);
    expect(csp['form-action']).toEqual(["'self'"]);
    expect(csp['require-trusted-types-for']).toEqual(["'script'"]);
    expect(csp['trusted-types']).toEqual(["'none'"]);
  });

  it('sends the other hardening headers', () => {
    expect(header('Strict-Transport-Security')).toMatch(/max-age=63072000/);
    expect(header('X-Content-Type-Options')).toBe('nosniff');
    expect(header('Referrer-Policy')).toBe('strict-origin-when-cross-origin');
    expect(header('Cross-Origin-Opener-Policy')).toBe('same-origin');
    expect(header('X-Frame-Options')).toBe('DENY');
    expect(header('Permissions-Policy')).toMatch(/camera=\(\).*microphone=\(\)/);
  });
});
