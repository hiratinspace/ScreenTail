/**
 * What every browser test gets (ST-099 follow-up): a page that cannot reach anything beyond this
 * machine, Google's sign-in script replaced by a stand-in whose button signs a Google-shaped ID token
 * with this run's test key, and a watch on Content-Security-Policy violations that fails the test if
 * the page logged any. Plus the checks the tests share: the page's security headers and axe-core.
 */
import AxeBuilder from '@axe-core/playwright';
import { expect, test as base, type Page } from '@playwright/test';
import { createPrivateKey, sign } from 'node:crypto';
import { appHeaders } from '../security-headers.ts';
import { env } from './env.ts';

function setting(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is not set: run the suite with \`npm run e2e\`, whose global setup sets it.`);
  }
  return value;
}

export const world = {
  get api() {
    return setting(env.api);
  },
  get configured() {
    return setting(env.configured);
  },
  get unconfigured() {
    return setting(env.unconfigured);
  },
  get clientId() {
    return setting(env.clientId);
  },
  get run() {
    return setting(env.run);
  },
  get downloadUrl() {
    return setting(env.downloadUrl);
  },
};

export const sessionCookie = '__Host-screentail-admin';

export interface GoogleAccount {
  sub: string;
  email: string;
  name: string;
}

/** An ID token shaped like Google's, signed with this run's key, for the audience the page initialised Google with. */
export function signIdToken(account: GoogleAccount, nonce: string, audience: string): string {
  const now = Math.floor(Date.now() / 1000);
  const part = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const body = `${part({ alg: 'RS256', kid: setting(env.keyId), typ: 'JWT' })}.${part({
    iss: 'https://accounts.google.com',
    aud: audience,
    azp: audience,
    sub: account.sub,
    email: account.email,
    email_verified: true,
    name: account.name,
    nonce,
    iat: now,
    nbf: now - 60,
    exp: now + 3600,
  })}`;
  const signature = sign('sha256', Buffer.from(body), createPrivateKey(setting(env.privateKey)));
  return `${body}.${signature.toString('base64url')}`;
}

/** The Google account the stand-in's button signs in as, and the tokens it handed the page. */
export class FakeGoogle {
  account: GoogleAccount | undefined;
  readonly issued: string[] = [];

  credential(nonce: string, clientId: string): string {
    if (!this.account) {
      throw new Error('The test clicked the Google button without saying which account signs in.');
    }
    const token = signIdToken(this.account, nonce, clientId);
    this.issued.push(token);
    return token;
  }
}

const gisClient = 'https://accounts.google.com/gsi/client';

/**
 * Google Identity Services as far as the page can tell: `initialize` and `renderButton`, served from
 * Google's own address so the page's real CSP decides whether it may run. Its button asks the test,
 * through a binding, for a token carrying the nonce and client id the page gave it, as Google's would.
 */
const fakeGis = `(() => {
  let options;
  window.google = { accounts: { id: {
    initialize(o) { options = o; },
    renderButton(element) {
      const { nonce, client_id: clientId, callback } = options;
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = 'Sign in with Google (test)';
      button.addEventListener('click', async () => {
        callback({ credential: await window.screentailTestCredential(nonce, clientId) });
      });
      element.appendChild(button);
    },
  } } };
})();`;

interface Fixtures {
  google: FakeGoogle;
  /** Every CSP violation the page reported, by event or console. The test fails if any is left at its end. */
  violations: string[];
  /** Every request for anything beyond this machine, refused. The test fails if there was one. */
  outside: string[];
}

export const test = base.extend<Fixtures>({
  // Destructuring is how Playwright knows which fixtures a fixture needs, even with none.
  // eslint-disable-next-line no-empty-pattern
  google: async ({}, provide) => {
    await provide(new FakeGoogle());
  },
  // eslint-disable-next-line no-empty-pattern
  violations: async ({}, provide) => {
    await provide([]);
  },
  // eslint-disable-next-line no-empty-pattern
  outside: async ({}, provide) => {
    await provide([]);
  },
  page: async ({ page, google, violations, outside }, provide) => {
    await page.context().route('**/*', async (route) => {
      const url = new URL(route.request().url());
      if (url.hostname === 'localhost' || url.hostname === '127.0.0.1') {
        await route.continue();
      } else if (url.href === gisClient) {
        await route.fulfill({ status: 200, contentType: 'text/javascript; charset=utf-8', body: fakeGis });
      } else {
        outside.push(url.href);
        await route.abort('blockedbyclient');
      }
    });
    await page.exposeBinding('screentailTestCredential', (_source, nonce: string, clientId: string) => google.credential(nonce, clientId));
    await page.exposeBinding('screentailCspViolation', (_source, line: string) => {
      violations.push(line);
    });
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) => {
        const report = (window as unknown as { screentailCspViolation: (line: string) => void }).screentailCspViolation;
        void report(`${e.effectiveDirective} blocked ${e.blockedURI || '(inline)'} at ${e.sourceFile}:${e.lineNumber}`);
      });
    });
    page.on('console', (message) => {
      if (/content security policy/i.test(message.text())) {
        violations.push(`console: ${message.text()}`);
      }
    });

    await provide(page);

    expect(violations, 'Content-Security-Policy violations the page reported').toEqual([]);
    expect(outside, 'requests for anything beyond this machine').toEqual([]);
  },
});

export { expect };

/** axe-core on the page as it stands: no serious or critical violation of WCAG 2.x A or AA. */
export async function expectAccessible(page: Page, where: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const serious = results.violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.impact} ${v.id}: ${v.help} at ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`);
  expect(serious, `serious or critical axe-core violations on ${where}`).toEqual([]);
}

/**
 * The page's headers. Written out here, not only compared with security-headers.ts, so that a header
 * dropped from that file (and so from every deployment, whose Caddy snippet is made from it) fails.
 */
export function expectPageHeaders(headers: Record<string, string>): void {
  const csp = headers['content-security-policy'] ?? '';
  for (const directive of [
    "default-src 'none'",
    "script-src 'self' https://accounts.google.com/gsi/client;",
    "frame-ancestors 'none'",
    "base-uri 'none'",
    "object-src 'none'",
    "form-action 'self'",
    "connect-src 'self' https://accounts.google.com/gsi/;",
  ]) {
    expect(csp, 'Content-Security-Policy').toContain(directive);
  }
  expect(csp, 'no eval anywhere').not.toContain('unsafe-eval');
  expect(csp.split(';').find((d) => d.trim().startsWith('script-src')), 'no inline script').not.toContain('unsafe-inline');
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['referrer-policy']).toBe('strict-origin-when-cross-origin');
  expect(headers['cross-origin-opener-policy']).toBe('same-origin-allow-popups');
  for (const feature of ['camera=()', 'microphone=()', 'geolocation=()', 'display-capture=()', 'payment=()', 'usb=()']) {
    expect(headers['permissions-policy'], 'Permissions-Policy').toContain(feature);
  }
  // And every header the source of truth lists, with its exact value.
  for (const [name, value] of Object.entries(appHeaders)) {
    expect(headers[name.toLowerCase()], name).toBe(value);
  }
}

/** The API's own headers on one of its answers: closed CSP, and not stored by a browser or proxy. */
export function expectApiHeaders(headers: Record<string, string>): void {
  expect(headers['content-security-policy']).toContain("default-src 'none'");
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['cache-control']).toContain('no-store');
}
