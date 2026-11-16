import { vi, type Mock } from 'vitest';
import type { AdminApi, Integrations, Policy, SignedIn, Technicians } from '../api';

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

/** ConnectWise connected by the owner, Hudu not connected, and a few changes, newest first. */
export const integrations: Integrations = {
  integrations: [
    {
      provider: 'connectwise',
      name: 'ConnectWise Manage',
      connected: true,
      siteUrl: 'https://na.myconnectwise.example',
      connectedAt: '2026-11-15T10:00:00Z',
      connectedBy: { kind: 'owner', name: 'Pat Owner', device: null },
      lastCheckedAt: null,
      lastResult: null,
      lastError: null,
    },
    {
      provider: 'hudu',
      name: 'Hudu',
      connected: false,
      siteUrl: null,
      connectedAt: null,
      connectedBy: null,
      lastCheckedAt: null,
      lastResult: null,
      lastError: null,
    },
  ],
  changes: [
    {
      at: recently,
      provider: 'hudu',
      action: 'mapped',
      by: { kind: 'technician', name: 'Tomas Ortiz', device: 'TECH-LAPTOP' },
      psaCompany: 'Acme Dental',
      docCompanyId: '7',
      docCompanyName: 'Acme Dental Group',
    },
    {
      at: '2026-11-15T10:00:00Z',
      provider: 'connectwise',
      action: 'connected',
      by: { kind: 'owner', name: 'Pat Owner', device: null },
      psaCompany: null,
      docCompanyId: null,
      docCompanyName: null,
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
    invite: vi.fn(async (email: string) => ({ id: 'i-2', email, code: 'ABCDE-FGHJK-MNPQR-STUVW', expiresAt: later })),
    revokeDevice: vi.fn(async () => undefined),
    policy: vi.fn(async () => defaults),
    setPolicy: vi.fn(async (change) => ({ ...defaults, ...change, version: 'p20261031120000000' })),
    integrations: vi.fn(async () => integrations),
    connectIntegration: vi.fn(async () => undefined),
    disconnectIntegration: vi.fn(async () => undefined),
    checkIntegration: vi.fn(async () => ({ ok: true, message: 'ScreenTail reached the site and the key works.', checkedAt: recently })),
    download: vi.fn(async () => ({ url: null, message: 'No client download is published for this deployment yet.' })),
  };
  for (const [key, value] of Object.entries(overrides)) {
    (api as Record<string, unknown>)[key] = vi.fn(value as (...args: unknown[]) => unknown);
  }
  return api as unknown as FakeApi;
}
