// @vitest-environment node
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { caddySnippet } from '../../deploy/web/page-headers';
import { appHeaders } from '../security-headers';

// ST-007. The deployment's proxy sends the page's headers from a snippet made from security-headers.ts
// when its image is built (deploy/web/page-headers.ts). deploy/smoke.sh checks what the running proxy
// actually sends; this checks the snippet itself and that the image is wired to make and use it.

const repo = resolve(__dirname, '..', '..');
const read = (path: string) => readFileSync(resolve(repo, path), 'utf8');

/** The header lines of a snippet, read back the way Caddy would: name, then one double-quoted value. */
function headersIn(snippet: string): Record<string, string> {
  const found: Record<string, string> = {};
  for (const line of snippet.split('\n')) {
    const match = /^\t\t([A-Za-z0-9-]+) "([^"]*)"$/.exec(line);
    if (match) {
      expect(found[match[1]!], `${match[1]} appears twice`).toBeUndefined();
      found[match[1]!] = match[2]!;
    }
  }
  return found;
}

describe("the proxy's page headers", () => {
  it('are exactly the headers security-headers.ts says the page is served with', () => {
    const snippet = caddySnippet(appHeaders);

    expect(headersIn(snippet)).toEqual(appHeaders);
    expect(snippet).toMatch(/^\(page_headers\) \{$/m);
    // Every line is a comment, the snippet's frame or a header: nothing else can slip in.
    const header = /^\t\t[A-Za-z0-9-]+ "[^"]*"$/;
    for (const line of snippet.split('\n').filter((l) => l !== '')) {
      expect(line.startsWith('#') || ['(page_headers) {', '\theader {', '\t}', '}'].includes(line) || header.test(line), line).toBe(true);
    }
  });

  it("refuses a value Caddy would change or read as configuration, rather than sending something else", () => {
    for (const value of ['a {http.request.host}', 'say "hi"', 'back\\slash', 'two\nlines', ' padded', 'tick`', 'café', '']) {
      expect(() => caddySnippet({ 'X-Test': value }), JSON.stringify(value)).toThrow();
    }
    expect(() => caddySnippet({ 'X Test': 'ok' })).toThrow();
    expect(() => caddySnippet({ 'X-Test:': 'ok' })).toThrow();
    expect(() => caddySnippet({})).toThrow();
    expect(caddySnippet({ 'X-Test': "default-src 'none'; a=(), b=()" })).toContain('\t\tX-Test "default-src \'none\'; a=(), b=()"');
  });

  it('are made when the image is built and used on the page and its errors', () => {
    const dockerfile = read('deploy/web/Dockerfile');
    const caddyfile = read('deploy/web/Caddyfile');

    expect(dockerfile).toContain('node /repo/deploy/web/page-headers.ts > /repo/page-headers.caddy');
    expect(dockerfile).toContain('COPY --from=build /repo/page-headers.caddy /etc/caddy/page-headers.caddy');
    expect(caddyfile).toMatch(/^import \/etc\/caddy\/page-headers\.caddy$/m);
    expect(caddyfile.match(/^\t\timport page_headers$/gm)).toHaveLength(2);
  });

  it("are built with the Node version the web app is developed on", () => {
    const major = read('.nvmrc').trim().replace(/^v/, '').split('.')[0];
    const from = /^FROM node:(\d+)\.\d+\.\d+-alpine[\d.]*@sha256:[0-9a-f]{64} AS build$/m.exec(read('deploy/web/Dockerfile'));

    expect(from, 'the build stage pins node by exact version and digest').not.toBeNull();
    expect(from![1]).toBe(major);
  });
});
