/**
 * The web admin's security headers as Caddy configuration (ST-007), made from `web/security-headers.ts`
 * when the proxy image is built (deploy/web/Dockerfile), so the page is served with exactly the headers
 * it was written for and nobody copies them by hand. Nothing generated is committed.
 *
 *   node deploy/web/page-headers.ts          prints the Caddy snippet `(page_headers)`
 *   node deploy/web/page-headers.ts --json   prints the headers as JSON (deploy/smoke.sh compares the
 *                                            served page with them)
 *
 * Node runs this TypeScript directly (type stripping), so it uses no syntax beyond annotations.
 */
import { pathToFileURL } from 'node:url';
import { appHeaders } from '../../web/security-headers.ts';

/** A header name as RFC 9110 spells one, letters, digits and hyphens being all the page uses. */
const headerName = /^[A-Za-z0-9-]+$/;

/**
 * Printable ASCII with none of the characters that mean something to Caddy inside a quoted token:
 * quotes and backslashes would end or escape it, and braces are Caddy's placeholders, which would be
 * replaced at request time with whatever they name.
 */
const headerValue = /^[\x20-\x7e]+$/;
const special = /["\\{}`]/;

export function caddySnippet(headers: Record<string, string>): string {
  const lines = Object.entries(headers).map(([name, value]) => {
    if (!headerName.test(name)) {
      throw new Error(`Header name ${JSON.stringify(name)} is not one Caddy can be given safely.`);
    }
    if (!headerValue.test(value) || special.test(value) || value.trim() !== value) {
      throw new Error(`The value of ${name} has a character Caddy would not pass through unchanged.`);
    }
    return `\t\t${name} "${value}"`;
  });
  if (lines.length === 0) {
    throw new Error('No headers to send.');
  }

  return [
    '# Generated from web/security-headers.ts by deploy/web/page-headers.ts when the image is built.',
    '# Not committed and not to be edited: change web/security-headers.ts.',
    '(page_headers) {',
    '\theader {',
    ...lines,
    '\t}',
    '}',
    '',
  ].join('\n');
}

const invoked = process.argv[1] ? pathToFileURL(process.argv[1]).href : '';
if (import.meta.url === invoked) {
  process.stdout.write(process.argv.includes('--json') ? `${JSON.stringify(appHeaders)}\n` : caddySnippet(appHeaders));
}
