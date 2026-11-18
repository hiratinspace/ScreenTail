/**
 * The pilot form (site/src/pages/index.astro, #pilot) posts here. This file checks the submission and
 * turns it into one plain-text email to the owner's inbox; worker/index.ts sends it. Nothing is stored,
 * and nothing a visitor typed is ever logged.
 *
 * The form works without JavaScript: it is an ordinary POST, and every answer is a 303 to a static page.
 */

export const limits = {
  /** The whole body. A real submission is under 3 KB. */
  bodyBytes: 8 * 1024,
  name: 100,
  email: 254,
  company: 120,
  message: 2000,
} as const;

export const choices = {
  technicians: ['1 to 2', '3 to 10', '11 to 30', 'More than 30'],
  psa: ['ConnectWise Manage', 'HaloPSA', 'Autotask', 'Other or none'],
  tool: ['ScreenConnect', 'Remote Desktop', 'Splashtop', 'TeamViewer', 'AnyDesk', 'Other'],
} as const;

export type Pilot = {
  name: string;
  email: string;
  company: string;
  technicians: string;
  psa: string;
  tool: string;
  message: string;
};

export type Checked =
  | { kind: 'ok'; pilot: Pilot }
  /** The hidden field was filled in: a bot. It is told "thanks" and nothing is sent. */
  | { kind: 'bot' }
  | { kind: 'invalid'; field: keyof Pilot | 'form' };

// One address, no spaces, a dot in the domain. Deliberately plain: the inbox is the real check.
const emailShape = /^[^\s@<>"',;:()\\[\]]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/;

/** Control characters (line breaks included) out, runs of spaces collapsed, ends trimmed. */
export function clean(value: string, keepLineBreaks = false): string {
  const unified = value.split('\r\n').join('\n').split('\r').join('\n');
  let stripped = '';
  for (const ch of unified) {
    const code = ch.codePointAt(0) ?? 0;
    const control = code < 0x20 || (code >= 0x7f && code <= 0x9f) || code === 0x2028 || code === 0x2029;
    if (!control) stripped += ch;
    else if (ch === '\n' && keepLineBreaks) stripped += '\n';
    else stripped += ' ';
  }
  const collapsed = keepLineBreaks
    ? stripped.replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n')
    : stripped.replace(/\s+/g, ' ');
  return collapsed.trim();
}

export function check(form: URLSearchParams): Checked {
  // The honeypot: hidden from people and from screen readers, so only a bot fills it in.
  if ((form.get('hp_trap') ?? '') !== '') return { kind: 'bot' };

  const text = (key: string, max: number, required: boolean, lines = false) => {
    const raw = form.get(key);
    if (raw === null) return required ? null : '';
    if (raw.length > max * 2) return null;
    const value = clean(raw, lines);
    if (value.length > max) return null;
    if (required && value === '') return null;
    return value;
  };
  const pick = (key: keyof typeof choices) => {
    const value = form.get(key);
    return value !== null && (choices[key] as readonly string[]).includes(value) ? value : null;
  };

  const name = text('name', limits.name, true);
  if (name === null) return { kind: 'invalid', field: 'name' };
  const email = text('email', limits.email, true);
  if (email === null || !emailShape.test(email)) return { kind: 'invalid', field: 'email' };
  const company = text('company', limits.company, true);
  if (company === null) return { kind: 'invalid', field: 'company' };
  const technicians = pick('technicians');
  if (technicians === null) return { kind: 'invalid', field: 'technicians' };
  const psa = pick('psa');
  if (psa === null) return { kind: 'invalid', field: 'psa' };
  const tool = pick('tool');
  if (tool === null) return { kind: 'invalid', field: 'tool' };
  const message = text('message', limits.message, false, true);
  if (message === null) return { kind: 'invalid', field: 'message' };

  return { kind: 'ok', pilot: { name, email, company, technicians, psa, tool, message } };
}

/** The email to the owner. Plain text only: nothing a visitor typed is ever put into HTML. */
export function compose(pilot: Pilot): { subject: string; text: string } {
  // Company is at most limits.company characters, so the subject needs no cut.
  const subject = `Pilot enquiry: ${pilot.company}`;
  const text = [
    'A pilot enquiry from the ScreenTrail site.',
    '',
    `Name: ${pilot.name}`,
    `Email: ${pilot.email}`,
    `Company: ${pilot.company}`,
    `Technicians: ${pilot.technicians}`,
    `PSA: ${pilot.psa}`,
    `Remote tool: ${pilot.tool}`,
    '',
    'Message:',
    pilot.message === '' ? '(none)' : pilot.message,
    '',
    'Reply to this email to answer them. The site keeps no copy of this enquiry.',
  ].join('\n');
  return { subject, text };
}

/**
 * Whether the post came from this site's own page. A browser sends Sec-Fetch-Site and Origin on a form
 * post; another site's page posting here would show its own origin. A client that sends neither (a
 * script) is let through to the honeypot, the field checks and the rate limit.
 */
export function sameOrigin(request: Request): boolean {
  const fetchSite = request.headers.get('Sec-Fetch-Site');
  if (fetchSite !== null && fetchSite !== 'same-origin') return false;
  const origin = request.headers.get('Origin');
  if (origin !== null && origin !== new URL(request.url).origin) return false;
  return true;
}
