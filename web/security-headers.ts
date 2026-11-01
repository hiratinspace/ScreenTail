/**
 * The web admin's security headers (ST-099 hardening, ADR-0009), in one place: `vite preview` sends
 * them, the dev server sends all but the CSP, and a deployment's web server must send `appHeaders`
 * exactly (docs/dev/web-admin.md has them as header lines).
 *
 * The Content-Security-Policy allows the page's own files and Google Identity Services and nothing
 * else, following Google's CSP guidance for GIS
 * (developers.google.com/identity/gsi/web/guides/get-google-api-clientid#content_security_policy):
 * the GIS script, its stylesheet, and its iframe and endpoints under https://accounts.google.com/gsi/,
 * plus inline styles, which the GIS client itself needs (below). No inline script is allowed, and the
 * production build has none (securityHeaders.test.ts builds it and checks).
 */

const gis = 'https://accounts.google.com/gsi/';

/** The policy as directives, so a test can read one without parsing a string. */
export const contentSecurityPolicy: Record<string, string[]> = {
  'default-src': ["'none'"],
  'script-src': ["'self'", `${gis}client`],
  // 'unsafe-inline' for styles only, and on purpose: the GIS client inserts a <style> element and sets
  // style attributes on its button, and with Google's listed sources alone Chrome blocks both and the
  // button is drawn unstyled (checked 2026-11-01, ADR-0009). A style cannot run code, and the usual way
  // to exfiltrate with one, a url() to another site, is still blocked by img-src and font-src.
  'style-src': ["'self'", "'unsafe-inline'", `${gis}style`],
  'img-src': ["'self'"],
  'font-src': ["'self'"],
  'connect-src': ["'self'", gis],
  'frame-src': [gis],
  'manifest-src': ["'self'"],
  'base-uri': ["'none'"],
  'form-action': ["'self'"],
  'frame-ancestors': ["'none'"],
  'object-src': ["'none'"],
};

export function serialise(policy: Record<string, string[]>): string {
  return Object.entries(policy)
    .map(([directive, sources]) => `${directive} ${sources.join(' ')}`)
    .join('; ');
}

/**
 * Everything the page does not use, switched off for it and for anything it frames. Not listed, so
 * left at the browser's default: `identity-credentials-get` and `publickey-credentials-get`, which
 * Google's sign-in (FedCM, passkeys) may need inside its own iframe.
 */
export const permissionsPolicy = [
  'accelerometer=()',
  'autoplay=()',
  'bluetooth=()',
  'camera=()',
  'display-capture=()',
  'encrypted-media=()',
  'geolocation=()',
  'gyroscope=()',
  'hid=()',
  'magnetometer=()',
  'microphone=()',
  'midi=()',
  'payment=()',
  'screen-wake-lock=()',
  'serial=()',
  'usb=()',
  'xr-spatial-tracking=()',
].join(', ');

/** Every header but the CSP: what the dev server sends, as Vite's dev page runs inline scripts and styles. */
export const baseHeaders: Record<string, string> = {
  'X-Content-Type-Options': 'nosniff',
  // The origin, never the path, goes to another site; Google's button needs the origin to be told.
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  'Permissions-Policy': permissionsPolicy,
  // Google's sign-in pop-up must be able to answer the page that opened it (Google's COOP guidance).
  'Cross-Origin-Opener-Policy': 'same-origin-allow-popups',
  // For browsers that predate frame-ancestors.
  'X-Frame-Options': 'DENY',
};

/** What the built page is served with: `vite preview`, and every deployment. */
export const appHeaders: Record<string, string> = {
  ...baseHeaders,
  'Content-Security-Policy': serialise(contentSecurityPolicy),
};
