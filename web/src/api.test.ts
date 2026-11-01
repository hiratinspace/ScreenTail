import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, httpApi } from './api';

interface Sent {
  url: string;
  init: RequestInit;
}

function backend(answers: Array<{ status: number; body?: unknown }>) {
  const sent: Sent[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit) => {
      sent.push({ url, init });
      const answer = answers.shift() ?? { status: 204 };
      return new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
        status: answer.status,
        headers: answer.body === undefined ? {} : { 'Content-Type': 'application/json' },
      });
    }),
  );
  return sent;
}

const header = (s: Sent, name: string) => (s.init.headers as Record<string, string>)[name];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('httpApi', () => {
  it('sends the session CSRF value with every change and never with a read', async () => {
    const sent = backend([
      { status: 200, body: { status: 'signed_in', csrf: 'the-csrf', expiresAt: 'x', owner: {}, tenant: {} } },
      { status: 200, body: { seats: { used: 0, total: 1 }, technicians: [], invites: [] } },
      { status: 201, body: { id: 'i', email: 'a@b', code: 'C', expiresAt: 'x' } },
    ]);
    const api = httpApi();

    await api.signIn('google-token');
    await api.technicians();
    await api.invite('a@b', 'A');

    expect(sent[0]!.url).toBe('/v1/admin/session');
    expect(JSON.parse(sent[0]!.init.body as string)).toEqual({ credential: 'google-token' });
    expect(header(sent[1]!, 'X-ScreenTail-CSRF')).toBeUndefined();
    expect(header(sent[2]!, 'X-ScreenTail-CSRF')).toBe('the-csrf');
    expect(sent.every((s) => s.init.credentials === 'same-origin')).toBe(true);
  });

  it('asks for a sign-in nonce with a POST, and turns a bare 429 into a sentence', async () => {
    const sent = backend([
      { status: 200, body: { nonce: 'n-1', expiresAt: 'x', expiresInSeconds: 600 } },
      { status: 429 },
    ]);
    const api = httpApi();

    expect(await api.signInNonce()).toEqual({ nonce: 'n-1', expiresAt: 'x', expiresInSeconds: 600 });
    const refused = await api.signInNonce().catch((e: unknown) => e);

    expect(sent[0]!.url).toBe('/v1/admin/session/nonce');
    expect(sent[0]!.init.method).toBe('POST');
    expect(refused).toBeInstanceOf(ApiError);
    expect((refused as ApiError).status).toBe(429);
    expect((refused as ApiError).message).toBe('Too many sign-in attempts from this network. Wait a minute and try again.');
  });

  it('reads no session as signed out rather than as an error', async () => {
    backend([{ status: 401 }]);

    expect(await httpApi().session()).toBeNull();
  });

  it("carries the backend's sentence and code on a refusal", async () => {
    backend([{ status: 403, body: { error: 'signup_not_allowed', message: 'Not on the list.' } }]);

    const refused = await httpApi().signIn('t').catch((e: unknown) => e);

    expect(refused).toBeInstanceOf(ApiError);
    expect((refused as ApiError).status).toBe(403);
    expect((refused as ApiError).code).toBe('signup_not_allowed');
    expect((refused as ApiError).message).toBe('Not on the list.');
  });

  it('forgets the CSRF value on sign-out', async () => {
    const sent = backend([
      { status: 200, body: { status: 'signed_in', csrf: 'the-csrf', expiresAt: 'x', owner: {}, tenant: {} } },
      { status: 204 },
      { status: 401 },
    ]);
    const api = httpApi();
    await api.signIn('t');
    await api.signOut();
    await api.revokeDevice('d').catch(() => undefined);

    expect(header(sent[1]!, 'X-ScreenTail-CSRF')).toBe('the-csrf');
    expect(header(sent[2]!, 'X-ScreenTail-CSRF')).toBeUndefined();
  });
});
