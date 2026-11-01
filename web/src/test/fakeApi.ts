import { vi, type Mock } from 'vitest';
import type { AdminApi, Policy, SignedIn, Technicians } from '../api';

export const session: SignedIn = {
  csrf: 'csrf-value',
  expiresAt: '2026-11-01T08:00:00Z',
  owner: { email: 'pat@acme.example', displayName: 'Pat Owner' },
  tenant: { id: 't-1', name: 'Acme IT', seats: 3 },
};

export const defaults: Policy = {
  version: 'default',
  retentionDays: 7,
  localOnly: false,
  localOnlyLocked: false,
  captureAllWindows: false,
  updateChannel: 'stable',
  updateChannelLocked: false,
  updateCheckHours: 24,
  updateChecksUnderLocalOnly: false,
};

const recently = new Date(Date.now() - 5 * 60_000).toISOString();
const later = new Date(Date.now() + 70 * 3600_000).toISOString();

export const technicians: Technicians = {
  seats: { used: 1, total: 3 },
  technicians: [
    {
      id: 'u-1',
      email: 't.ortiz@acme.example',
      displayName: 'Tomas Ortiz',
      disabledAt: null,
      devices: [{ id: 'd-1', name: 'TECH-LAPTOP', activatedAt: recently, lastSeenAt: recently, revokedAt: null }],
    },
  ],
  invites: [
    {
      id: 'i-1',
      email: 'new@acme.example',
      displayName: 'New Tech',
      createdAt: recently,
      expiresAt: later,
      acceptedAt: null,
      status: 'pending',
    },
  ],
};

export type FakeApi = { [K in keyof AdminApi]: Mock<AdminApi[K]> };

/** The backend, as each test says it answers. Every call is a mock the test can inspect. */
export function fakeApi(overrides: Partial<{ [K in keyof AdminApi]: AdminApi[K] }> = {}): FakeApi {
  const api = {
    session: vi.fn(async () => null),
    signInNonce: vi.fn(async () => ({ nonce: 'nonce-1', expiresAt: later, expiresInSeconds: 600 })),
    signIn: vi.fn(async () => ({ status: 'signed_in' as const, ...session })),
    signUp: vi.fn(async () => session),
    signOut: vi.fn(async () => undefined),
    technicians: vi.fn(async () => technicians),
    invite: vi.fn(async (email: string) => ({ id: 'i-2', email, code: 'ABCDE-FGHJK', expiresAt: later })),
    revokeDevice: vi.fn(async () => undefined),
    policy: vi.fn(async () => defaults),
    setPolicy: vi.fn(async (change) => ({ ...defaults, ...change, version: 'p20261031120000000' })),
    integrations: vi.fn(async () => []),
    download: vi.fn(async () => ({ url: null, message: 'No client download is published for this deployment yet.' })),
  };
  for (const [key, value] of Object.entries(overrides)) {
    (api as Record<string, unknown>)[key] = vi.fn(value as (...args: unknown[]) => unknown);
  }
  return api as unknown as FakeApi;
}
