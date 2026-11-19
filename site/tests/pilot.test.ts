import { describe, expect, it, vi } from 'vitest';
import { check, clean, compose, sameOrigin } from '../worker/pilot';
import worker, { handlePilot, type Env } from '../worker/index';

const good = {
  name: 'Ada Lovelace',
  email: 'ada@example.com',
  company: 'Analytical MSP',
  technicians: '3 to 10',
  psa: 'ConnectWise PSA',
  tool: 'ScreenConnect',
  message: 'We run about 200 sessions a week.',
};
const form = (over: Record<string, string> = {}) => new URLSearchParams({ ...good, ...over });

function env(send = vi.fn(async () => ({ messageId: 'x' })), allowed = true): Env {
  return {
    ASSETS: {} as Fetcher,
    PILOT_MAIL: { send } as unknown as SendEmail,
    PILOT_LIMIT: { limit: vi.fn(async () => ({ success: allowed })) } as unknown as RateLimit,
    PILOT_FROM: 'pilot-form@screentrail.example',
    PILOT_TO: 'pilot-inbox@screentrail.example',
  };
}
function post(body: string, headers: Record<string, string> = {}) {
  return new Request('https://screentrail.example/api/pilot', {
    method: 'POST',
    body,
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      Origin: 'https://screentrail.example',
      'Sec-Fetch-Site': 'same-origin',
      'CF-Connecting-IP': '203.0.113.9',
      ...headers,
    },
  });
}

describe('check', () => {
  it('accepts a complete form', () => {
    const r = check(form());
    expect(r).toEqual({ kind: 'ok', pilot: good });
  });

  it('treats a filled honeypot as a bot', () => {
    expect(check(form({ hp_trap: 'http://spam' }))).toEqual({ kind: 'bot' });
  });

  it.each([
    ['name', ''],
    ['email', 'not-an-email'],
    ['email', 'a@b'],
    ['email', 'ada@example.com\r\nBcc: victim@example.com'],
    ['company', '   '],
    ['technicians', '1000'],
    ['psa', 'Something else'],
    ['tool', ''],
  ])('refuses a bad %s', (field, value) => {
    expect(check(form({ [field]: value }))).toEqual({ kind: 'invalid', field });
  });

  it('refuses a field longer than its limit', () => {
    expect(check(form({ company: 'x'.repeat(121) }))).toEqual({ kind: 'invalid', field: 'company' });
    expect(check(form({ message: 'x'.repeat(2001) }))).toEqual({ kind: 'invalid', field: 'message' });
  });

  it('lets the message be left out', () => {
    const f = form();
    f.delete('message');
    expect(check(f)).toMatchObject({ kind: 'ok', pilot: { message: '' } });
  });

  it('flattens line breaks out of one-line fields, so none reaches a mail header', () => {
    const r = check(form({ company: 'Acme\r\nBcc: someone@example.com' }));
    expect(r).toMatchObject({ kind: 'ok', pilot: { company: 'Acme Bcc: someone@example.com' } });
  });
});

describe('clean', () => {
  it('strips control characters and collapses spaces', () => {
    expect(clean('  a\u0000b\t\tc d  ')).toBe('a b c d');
  });
  it('keeps paragraph breaks in the message, at most one blank line', () => {
    expect(clean('one\r\n\r\n\r\n\r\ntwo\u0007', true)).toBe('one\n\ntwo');
    expect(clean('Server\tDown', true)).toBe('Server Down');
  });
});

describe('compose', () => {
  it('writes a plain-text email with every field and no HTML', () => {
    const { subject, text } = compose(good);
    expect(subject).toBe('Pilot enquiry: Analytical MSP');
    for (const v of Object.values(good)) expect(text).toContain(v);
    expect(text).not.toMatch(/<[a-z]/i);
  });
});

describe('sameOrigin', () => {
  it('refuses a post from another site', () => {
    expect(sameOrigin(post('', { Origin: 'https://evil.example', 'Sec-Fetch-Site': 'cross-site' }))).toBe(false);
    expect(sameOrigin(post('', { Origin: 'https://evil.example' }))).toBe(false);
  });
  it('accepts its own page', () => {
    expect(sameOrigin(post(''))).toBe(true);
  });
});

describe('handlePilot', () => {
  it('sends one email to the fixed inbox and redirects to the thanks page', async () => {
    const send = vi.fn(async () => ({ messageId: 'x' }));
    const res = await handlePilot(post(form().toString()), env(send));
    expect(res.status).toBe(303);
    expect(res.headers.get('Location')).toBe('https://screentrail.example/pilot/thanks/');
    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith(expect.objectContaining({
      to: 'pilot-inbox@screentrail.example',
      replyTo: { name: good.name, email: good.email },
      subject: 'Pilot enquiry: Analytical MSP',
    }));
  });

  it('tells a bot thanks and sends nothing', async () => {
    const send = vi.fn();
    const res = await handlePilot(post(form({ hp_trap: 'x' }).toString()), env(send));
    expect(res.headers.get('Location')).toContain('/pilot/thanks/');
    expect(send).not.toHaveBeenCalled();
  });

  it('sends nothing for an invalid form', async () => {
    const send = vi.fn();
    const res = await handlePilot(post(form({ email: 'nope' }).toString()), env(send));
    expect(res.headers.get('Location')).toContain('/pilot/problem/');
    expect(send).not.toHaveBeenCalled();
  });

  it('sends nothing past the rate limit', async () => {
    const send = vi.fn();
    const res = await handlePilot(post(form().toString()), env(send, false));
    expect(res.headers.get('Location')).toContain('/pilot/busy/');
    expect(send).not.toHaveBeenCalled();
  });

  it('refuses another site, another method, another body type and an oversized body', async () => {
    const e = env();
    expect((await handlePilot(post(form().toString(), { Origin: 'https://evil.example' }), e)).status).toBe(403);
    expect((await handlePilot(new Request('https://screentrail.example/api/pilot'), e)).status).toBe(405);
    expect((await handlePilot(post('{}', { 'Content-Type': 'application/json' }), e)).status).toBe(415);
    expect((await handlePilot(post(`message=${'x'.repeat(9000)}`), e)).status).toBe(413);
  });

  it('logs only the error code when the mail fails, never what was typed', async () => {
    const error = Object.assign(new Error('nope'), { code: 'E_SENDER_NOT_VERIFIED' });
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = await handlePilot(post(form().toString()), env(vi.fn(async () => { throw error; })));
    expect(res.headers.get('Location')).toContain('/pilot/problem/');
    const logged = log.mock.calls.flat().join(' ');
    expect(logged).toContain('E_SENDER_NOT_VERIFIED');
    for (const v of Object.values(good)) expect(logged).not.toContain(v);
    log.mockRestore();
  });

  it('answers every response with the security headers', async () => {
    const res = await handlePilot(post(form().toString()), env());
    expect(res.headers.get('Content-Security-Policy')).toContain("default-src 'none'");
    expect(res.headers.get('X-Content-Type-Options')).toBe('nosniff');
    expect(res.headers.get('Cache-Control')).toBe('no-store');
  });
});

describe('the Worker', () => {
  it('handles only /api/pilot', async () => {
    const res = await worker.fetch(new Request('https://screentrail.example/api/other'), env());
    expect(res.status).toBe(404);
  });
});
