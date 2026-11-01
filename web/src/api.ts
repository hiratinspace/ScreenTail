/**
 * The web admin's calls to the backend (ST-099, ADR-0009).
 *
 * The session is an HttpOnly cookie this script never sees. What it does hold is the session's CSRF
 * value, in memory only, which every change sends back in a header. A reload asks the backend for it
 * again with GET /v1/admin/session.
 */

export interface OwnerInfo {
  email: string;
  displayName: string;
}

export interface TenantInfo {
  id: string;
  name: string;
  seats: number;
}

export type SessionAnswer =
  | { status: 'signed_in'; csrf: string; expiresAt: string; owner: OwnerInfo; tenant: TenantInfo }
  | { status: 'needs_tenant'; email: string };

/** A one-time value for Google to sign into the ID token, so a captured token cannot be used twice (ADR-0009). */
export interface SignInNonce {
  nonce: string;
  expiresAt: string;
  /** The same deadline as seconds from when the backend answered, so the page need not trust its clock. */
  expiresInSeconds: number;
}

export interface SignedIn {
  csrf: string;
  expiresAt: string;
  owner: OwnerInfo;
  tenant: TenantInfo;
}

export interface Device {
  id: string;
  name: string;
  activatedAt: string;
  lastSeenAt: string | null;
  revokedAt: string | null;
}

export interface Technician {
  id: string;
  email: string;
  displayName: string;
  disabledAt: string | null;
  devices: Device[];
}

export interface Invite {
  id: string;
  email: string;
  displayName: string;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
  status: 'pending' | 'accepted' | 'expired';
}

export interface Technicians {
  seats: { used: number; total: number };
  technicians: Technician[];
  invites: Invite[];
}

export interface CreatedInvite {
  id: string;
  email: string;
  code: string;
  expiresAt: string;
}

/** The tenant's policy as devices receive it (shared/contracts/tenant-policy.v1.json). */
export interface Policy {
  version: string;
  retentionDays: number;
  localOnly: boolean;
  localOnlyLocked: boolean;
  captureAllWindows: boolean;
  updateChannel: 'stable' | 'pilot';
  updateChannelLocked: boolean;
  updateCheckHours: number;
  updateChecksUnderLocalOnly: boolean;
}

export type PolicyChange = Omit<Policy, 'version'> & { confirmAllWindows: boolean };

export interface Integration {
  provider: string;
  name: string;
  connected: boolean;
  siteUrl: string | null;
  connectedAt: string | null;
  lastCheckedAt: string | null;
  lastResult: 'ok' | 'failed' | null;
  lastError: string | null;
}

export interface Download {
  url: string | null;
  message: string;
}

/** A refusal from the backend, carrying the sentence it gave when it gave one. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  /** From a refusal's Retry-After, in seconds, when it gave one (a 429 does). */
  readonly retryAfterSeconds: number | undefined;

  constructor(status: number, message: string, code?: string, retryAfterSeconds?: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

export interface AdminApi {
  /** The current session, or null when there is none. */
  session(): Promise<SignedIn | null>;
  /** A fresh nonce, asked for each time Google's button is drawn. */
  signInNonce(): Promise<SignInNonce>;
  signIn(credential: string): Promise<SessionAnswer>;
  signUp(credential: string, tenantName: string): Promise<SignedIn>;
  signOut(): Promise<void>;
  technicians(): Promise<Technicians>;
  invite(email: string, displayName: string): Promise<CreatedInvite>;
  revokeDevice(id: string): Promise<void>;
  policy(): Promise<Policy>;
  setPolicy(change: PolicyChange): Promise<Policy>;
  integrations(): Promise<Integration[]>;
  download(): Promise<Download>;
}

const csrfHeader = 'X-ScreenTail-CSRF';

const fallback: Record<number, string> = {
  401: 'Your session has ended. Sign in again.',
  403: 'That was refused. Sign in again and retry.',
  404: 'That item no longer exists. Refresh the page.',
  429: 'Too many sign-in attempts from this network. Wait a minute and try again.',
  501: 'Sign-in is not set up on this deployment.',
  503: 'ScreenTail could not reach Google. Try again in a minute.',
};

/** The real client, over fetch, against the same origin the page came from. */
export function httpApi(base = ''): AdminApi {
  let csrf: string | undefined;

  async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }
    if (method !== 'GET' && csrf) {
      headers[csrfHeader] = csrf;
    }

    let response: Response;
    try {
      response = await fetch(`${base}${path}`, {
        method,
        headers,
        credentials: 'same-origin',
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw new ApiError(0, 'ScreenTail could not reach the server. Check your connection and try again.');
    }

    if (!response.ok) {
      let message = fallback[response.status] ?? `The server answered ${response.status}. Try again.`;
      let code: string | undefined;
      try {
        const refusal = (await response.json()) as { message?: string; error?: string };
        message = refusal.message ?? message;
        code = refusal.error;
      } catch {
        // An empty body: the sentence above stands.
      }
      const retryAfter = Number(response.headers.get('Retry-After'));
      throw new ApiError(response.status, message, code, Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined);
    }

    if (response.status === 204) {
      return undefined as T;
    }
    return (await response.json()) as T;
  }

  function keep(answer: SessionAnswer): SessionAnswer {
    if (answer.status === 'signed_in') {
      csrf = answer.csrf;
    }
    return answer;
  }

  return {
    async session() {
      try {
        const answer = await call<SessionAnswer>('GET', '/v1/admin/session');
        return answer.status === 'signed_in' ? (keep(answer) as SignedIn & { status: 'signed_in' }) : null;
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) {
          return null;
        }
        throw error;
      }
    },
    signInNonce: () => call<SignInNonce>('POST', '/v1/admin/session/nonce'),
    async signIn(credential) {
      return keep(await call<SessionAnswer>('POST', '/v1/admin/session', { credential }));
    },
    async signUp(credential, tenantName) {
      return keep(await call<SessionAnswer>('POST', '/v1/admin/signup', { credential, tenantName })) as SignedIn;
    },
    async signOut() {
      await call<void>('POST', '/v1/admin/session/end');
      csrf = undefined;
    },
    technicians: () => call<Technicians>('GET', '/v1/admin/technicians'),
    invite: (email, displayName) => call<CreatedInvite>('POST', '/v1/admin/invites', { email, displayName }),
    revokeDevice: (id) => call<void>('POST', `/v1/admin/devices/${encodeURIComponent(id)}/revoke`),
    policy: () => call<Policy>('GET', '/v1/admin/policy'),
    setPolicy: (change) => call<Policy>('PUT', '/v1/admin/policy', change),
    integrations: async () => (await call<{ integrations: Integration[] }>('GET', '/v1/admin/integrations')).integrations,
    download: () => call<Download>('GET', '/v1/admin/download'),
  };
}
