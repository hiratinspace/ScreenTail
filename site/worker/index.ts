/**
 * The site's only server code. Cloudflare serves every page as a static file; this runs first only for
 * /api/* (wrangler.jsonc, run_worker_first). It handles one thing: POST /api/pilot, the pilot form.
 */
import { check, compose, limits, sameOrigin } from './pilot';

export interface Env {
  ASSETS: Fetcher;
  PILOT_MAIL: SendEmail;
  PILOT_LIMIT: RateLimit;
  PILOT_FROM: string;
  PILOT_TO: string;
}

// The same headers the static pages get (public/_headers), with a CSP for answers that are never
// pages; a redirect or an error carries them too. tests/headers.test.ts holds the two sets together.
export const security: Record<string, string> = {
  'Content-Security-Policy': "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
  'Strict-Transport-Security': 'max-age=63072000; includeSubDomains; preload',
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  'Cross-Origin-Opener-Policy': 'same-origin',
  'Cross-Origin-Resource-Policy': 'same-origin',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=(), usb=()',
  'X-Frame-Options': 'DENY',
  'Cache-Control': 'no-store',
};

function seeOther(request: Request, path: string): Response {
  return new Response(null, { status: 303, headers: { ...security, Location: new URL(path, request.url).toString() } });
}

function plain(status: number, body: string, extra: Record<string, string> = {}): Response {
  return new Response(body, { status, headers: { ...security, 'Content-Type': 'text/plain; charset=utf-8', ...extra } });
}

/** Reads at most `max` bytes, or null if the body is longer. */
async function readCapped(request: Request, max: number): Promise<string | null> {
  const declared = Number(request.headers.get('Content-Length') ?? '0');
  if (declared > max) return null;
  if (request.body === null) return '';
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > max) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }
  const all = new Uint8Array(total);
  let at = 0;
  for (const chunk of chunks) {
    all.set(chunk, at);
    at += chunk.byteLength;
  }
  return new TextDecoder().decode(all);
}

export async function handlePilot(request: Request, env: Env): Promise<Response> {
  if (request.method !== 'POST') return plain(405, 'Use the form on the home page.', { Allow: 'POST' });
  if (!sameOrigin(request)) return plain(403, 'This form only accepts posts from its own page.');
  const type = request.headers.get('Content-Type') ?? '';
  if (!type.toLowerCase().startsWith('application/x-www-form-urlencoded')) return plain(415, 'Send the form as a web form.');

  const visitor = request.headers.get('CF-Connecting-IP') ?? 'unknown';
  const { success } = await env.PILOT_LIMIT.limit({ key: `pilot:${visitor}` });
  if (!success) return seeOther(request, '/pilot/problem/');

  const body = await readCapped(request, limits.bodyBytes);
  if (body === null) return plain(413, 'That was more than the form sends.');

  const result = check(new URLSearchParams(body));
  if (result.kind === 'bot') return seeOther(request, '/pilot/thanks/');
  if (result.kind === 'invalid') return seeOther(request, '/pilot/problem/');

  const { subject, text } = compose(result.pilot);
  try {
    await env.PILOT_MAIL.send({
      from: { name: 'ScreenTrail site', email: env.PILOT_FROM },
      to: env.PILOT_TO,
      replyTo: { name: result.pilot.name, email: result.pilot.email },
      subject,
      text,
    });
  } catch (error) {
    // The error's code only (for example E_SENDER_NOT_VERIFIED), never the enquiry.
    const code = error instanceof Error && 'code' in error ? String((error as { code: unknown }).code) : 'unknown';
    console.error(`pilot mail failed: ${code}`);
    return seeOther(request, '/pilot/problem/');
  }
  return seeOther(request, '/pilot/thanks/');
}

export default {
  async fetch(request, env): Promise<Response> {
    const { pathname } = new URL(request.url);
    if (pathname === '/api/pilot' || pathname === '/api/pilot/') return handlePilot(request, env);
    return plain(404, 'Not found.');
  },
} satisfies ExportedHandler<Env>;
