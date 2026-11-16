import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError, type Integration, type Integrations } from '../api';
import { fakeApi, integrations } from '../test/fakeApi';
import { IntegrationsPage } from './IntegrationsPage';

const checked = new Date(Date.now() - 60 * 60_000).toISOString();

const notConnected = (provider: string, name: string): Integration => ({
  provider,
  name,
  connected: false,
  siteUrl: null,
  connectedAt: null,
  connectedBy: null,
  lastCheckedAt: null,
  lastResult: null,
  lastError: null,
});

const nothingConnected: Integrations = {
  integrations: [notConnected('connectwise', 'ConnectWise Manage'), notConnected('hudu', 'Hudu')],
  changes: [],
};

const huduConnected: Integrations = {
  integrations: [
    notConnected('connectwise', 'ConnectWise Manage'),
    {
      ...notConnected('hudu', 'Hudu'),
      connected: true,
      siteUrl: 'https://acme.huducloud.example',
      connectedAt: '2026-11-14T12:00:00Z',
      connectedBy: { kind: 'technician', name: 'Dana Tech', device: 'DESKTOP-1' },
    },
  ],
  changes: [],
};

/** A paragraph by its whole text, when a time inside it splits the text across elements. */
function paragraph(region: HTMLElement, text: string) {
  return within(region).getByText((_, el) => el?.tagName === 'P' && el.textContent === text);
}

function type(region: HTMLElement, label: string, value: string) {
  fireEvent.change(within(region).getByLabelText(label), { target: { value } });
}

describe('IntegrationsPage', () => {
  it('says what is connected, by whom, and how its last test went', async () => {
    const api = fakeApi({
      integrations: async () => ({
        integrations: [
          {
            provider: 'connectwise',
            name: 'ConnectWise Manage',
            connected: true,
            siteUrl: 'https://na.myconnectwise.example',
            connectedAt: '2026-11-15T12:00:00Z',
            connectedBy: { kind: 'owner', name: 'Pat Owner', device: null },
            lastCheckedAt: checked,
            lastResult: 'failed',
            lastError: 'ConnectWise refused the key.',
          },
          huduConnected.integrations[1]!,
        ],
        changes: [],
      }),
    });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    expect(within(cw).getByText('Connected')).toBeDefined();
    expect(paragraph(cw, 'Connected by Pat Owner on Nov 15, 2026')).toBeDefined();
    expect(within(cw).getByText('https://na.myconnectwise.example')).toBeDefined();
    expect(within(cw).getByText('ConnectWise refused the key.')).toBeDefined();
    expect(within(cw).getByText(/Failed/)).toBeDefined();
    const hudu = screen.getByRole('region', { name: 'Hudu' });
    expect(paragraph(hudu, 'Connected by Dana Tech (DESKTOP-1) on Nov 14, 2026')).toBeDefined();
    expect(within(hudu).getByRole('button', { name: 'Replace' })).toBeDefined();
    expect(within(hudu).getByRole('button', { name: 'Disconnect' })).toBeDefined();
    expect(screen.getByText(/Technicians see each connection/)).toBeDefined();
  });

  it('shows just the date when who connected is not known, and Connect when nothing is connected', async () => {
    const api = fakeApi({
      integrations: async () => ({
        integrations: [
          { ...notConnected('connectwise', 'ConnectWise Manage'), connected: true, siteUrl: 'https://na.myconnectwise.example', connectedAt: '2026-11-01T12:00:00Z' },
          notConnected('hudu', 'Hudu'),
        ],
        changes: [],
      }),
    });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    expect(paragraph(cw, 'Connected on Nov 1, 2026')).toBeDefined();
    const hudu = screen.getByRole('region', { name: 'Hudu' });
    expect(within(hudu).getByText('Not connected')).toBeDefined();
    expect(within(hudu).getByRole('button', { name: 'Connect' })).toBeDefined();
    expect(within(hudu).queryByRole('button', { name: 'Disconnect' })).toBeNull();
    expect(screen.getByText(/No changes yet/)).toBeDefined();
  });

  it('shows every current company mapping with who chose it, however many changes came after', async () => {
    const api = fakeApi({
      integrations: async () => ({
        integrations: integrations.integrations,
        changes: Array.from({ length: 30 }, (_, i) => ({
          at: checked,
          provider: 'hudu',
          action: 'unmapped' as const,
          by: { kind: 'technician' as const, name: null, device: 'DESKTOP-2' },
          psaCompany: `Other ${i}`,
          docCompanyId: null,
          docCompanyName: null,
        })),
        mappings: [
          { psaCompany: 'Acme Dental', docCompanyId: '7', docCompanyName: 'Acme Dental Group', setAt: checked, setBy: { kind: 'technician', name: 'Tomas Ortiz', device: 'TECH-LAPTOP' } },
          { psaCompany: 'Borough Legal', docCompanyId: '9', docCompanyName: 'Borough Legal', setAt: checked, setBy: null },
        ],
      }),
    });
    render(<IntegrationsPage api={api} />);

    const table = await screen.findByRole('table', { name: 'Company mappings' });
    const rows = within(table).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell').slice(0, 3).map((c) => c.textContent));
    expect(rows).toEqual([
      ['Acme Dental', 'Acme Dental Group', 'Tomas Ortiz (TECH-LAPTOP)'],
      ['Borough Legal', 'Borough Legal', 'Matched by name'],
    ]);
  });

  it('shows the recent changes twenty at a time, so none of the latest hundred drop out of sight', async () => {
    const changes = Array.from({ length: 45 }, (_, i) => ({
      at: checked,
      provider: 'hudu',
      action: 'unmapped' as const,
      by: { kind: 'technician' as const, name: null, device: 'DESKTOP-2' },
      psaCompany: `Company ${i}`,
      docCompanyId: null,
      docCompanyName: null,
    }));
    const api = fakeApi({ integrations: async () => ({ integrations: integrations.integrations, changes }) });
    render(<IntegrationsPage api={api} />);

    const table = await screen.findByRole('table', { name: 'Recent changes' });
    expect(within(table).getAllByRole('row')).toHaveLength(21);
    fireEvent.click(screen.getByRole('button', { name: 'Show 20 more' }));
    expect(within(table).getAllByRole('row')).toHaveLength(41);
    fireEvent.click(screen.getByRole('button', { name: 'Show 5 more' }));
    expect(within(table).getAllByRole('row')).toHaveLength(46);
    expect(within(table).getByText('Forgot the mapping for Company 44').textContent).toBe('Forgot the mapping for Company 44');
    expect(screen.queryByRole('button', { name: /more$/ })).toBeNull();
  });

  it('lists recent changes in words, with who made each', async () => {
    const api = fakeApi({
      integrations: async () => ({
        integrations: integrations.integrations,
        changes: [
          ...integrations.changes,
          { at: checked, provider: 'hudu', action: 'unmapped', by: { kind: 'technician', name: null, device: 'DESKTOP-2' }, psaCompany: 'Beta Law', docCompanyId: null, docCompanyName: null },
          { at: checked, provider: 'hudu', action: 'mappings_cleared', by: { kind: 'owner', name: 'Pat Owner', device: null }, psaCompany: null, docCompanyId: null, docCompanyName: null },
          { at: checked, provider: 'connectwise', action: 'replaced', by: { kind: 'owner', name: null, device: null }, psaCompany: null, docCompanyId: null, docCompanyName: null },
          { at: checked, provider: 'hudu', action: 'disconnected', by: { kind: 'owner', name: 'Pat Owner', device: null }, psaCompany: null, docCompanyId: null, docCompanyName: null },
          { at: checked, provider: 'hudu', action: 'mappings_pruned', by: { kind: 'screentail', name: 'ScreenTail', device: null }, psaCompany: null, docCompanyId: null, docCompanyName: null, removed: 12 },
        ],
      }),
    });
    render(<IntegrationsPage api={api} />);

    const table = await screen.findByRole('table', { name: 'Recent changes' });
    const rows = within(table).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell').slice(1).map((c) => c.textContent));
    expect(rows).toEqual([
      ['Hudu', 'Mapped Acme Dental to Acme Dental Group', 'Tomas Ortiz (TECH-LAPTOP)'],
      ['ConnectWise Manage', 'Connected', 'Pat Owner'],
      ['Hudu', 'Forgot the mapping for Beta Law', 'A technician (DESKTOP-2)'],
      ['Hudu', 'Company mappings cleared', 'Pat Owner'],
      ['ConnectWise Manage', 'Credential replaced', 'The owner'],
      ['Hudu', 'Disconnected', 'Pat Owner'],
      ['Hudu', '12 older mapping changes removed', 'ScreenTail'],
    ]);
  });

  it('connects ConnectWise from three fields, tests it, reads the list again, and keeps no key on the page', async () => {
    const api = fakeApi({ integrations: async () => nothingConnected });
    api.checkIntegration.mockImplementation(async () => ({ ok: false, message: 'ScreenTail could not reach the site.', checkedAt: checked }));
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Connect' }));
    await waitFor(() => expect(document.activeElement).toBe(within(cw).getByLabelText('Site')));
    expect(within(cw).getByLabelText('Private key').getAttribute('type')).toBe('password');
    expect(within(cw).getByLabelText('Public key').getAttribute('type')).toBe('password');
    expect(within(cw).getByLabelText('Private key').getAttribute('autocomplete')).toBe('off');

    type(cw, 'Site', ' na.myconnectwise.example ');
    type(cw, 'Company ID', ' AcmeIT ');
    type(cw, 'Public key', 'pubKEY123');
    type(cw, 'Private key', 'privKEY456zq');
    fireEvent.click(within(cw).getByRole('button', { name: 'Save and test' }));

    await waitFor(() =>
      expect(api.connectIntegration).toHaveBeenCalledWith('connectwise', 'https://na.myconnectwise.example', 'acmeit+pubKEY123:privKEY456zq'),
    );
    await waitFor(() => expect(api.checkIntegration).toHaveBeenCalledWith('connectwise'));
    expect(await within(cw).findByText('Test failed: ScreenTail could not reach the site.')).toBeDefined();
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(2));

    expect(within(cw).queryByLabelText('Private key')).toBeNull();
    expect(document.querySelectorAll('input[type="password"]')).toHaveLength(0);
    for (const piece of ['privKEY456zq', 'pubKEY123', '56zq']) {
      expect(document.body.innerHTML).not.toContain(piece);
    }
  });

  it('connects Hudu with one key, and warns that moving it forgets the company mappings', async () => {
    const api = fakeApi({ integrations: async () => huduConnected });
    render(<IntegrationsPage api={api} />);

    const hudu = await screen.findByRole('region', { name: 'Hudu' });
    fireEvent.click(within(hudu).getByRole('button', { name: 'Replace' }));
    expect((within(hudu).getByLabelText('Site') as HTMLInputElement).value).toBe('https://acme.huducloud.example');
    expect(within(hudu).getByText(/Moving Hudu to another site forgets the company mappings made on this one\./)).toBeDefined();
    expect(within(hudu).queryByLabelText('Company ID')).toBeNull();

    type(hudu, 'Site', 'https://new.huducloud.example');
    type(hudu, 'API key', 'hudu-key-9876');
    fireEvent.click(within(hudu).getByRole('button', { name: 'Save and test' }));

    await waitFor(() => expect(api.connectIntegration).toHaveBeenCalledWith('hudu', 'https://new.huducloud.example', 'hudu-key-9876'));
    expect(await within(hudu).findByText('Test worked: ScreenTail reached the site and the key works.')).toBeDefined();
    expect(document.body.innerHTML).not.toContain('hudu-key-9876');
  });

  it('says what is missing before it saves anything', async () => {
    const api = fakeApi({ integrations: async () => nothingConnected });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Connect' }));
    type(cw, 'Site', 'http://na.myconnectwise.example');
    type(cw, 'Public key', 'has space');
    fireEvent.click(within(cw).getByRole('button', { name: 'Save and test' }));

    expect(within(cw).getByRole('alert').textContent).toBe('Fix the fields marked below, then save again.');
    expect(within(cw).getByText("Enter the site's https address, like https://na.myconnectwise.net.")).toBeDefined();
    expect(within(cw).getByText("Enter the company ID, the one typed on ConnectWise's sign-in screen.")).toBeDefined();
    expect(within(cw).getByText("The company ID and keys cannot contain spaces, '+' or ':'.")).toBeDefined();
    expect(within(cw).getByLabelText('Company ID').getAttribute('aria-invalid')).toBe('true');
    expect(api.connectIntegration).not.toHaveBeenCalled();
  });

  it("shows the server's sentence when the save is refused, and does not test", async () => {
    const api = fakeApi({ integrations: async () => nothingConnected });
    api.connectIntegration.mockImplementation(async () => {
      throw new ApiError(400, 'That site is on a private network. Use the address your PSA gives you on the internet.', 'private_site_url');
    });
    render(<IntegrationsPage api={api} />);

    const hudu = await screen.findByRole('region', { name: 'Hudu' });
    fireEvent.click(within(hudu).getByRole('button', { name: 'Connect' }));
    type(hudu, 'Site', 'https://hudu.internal.example');
    type(hudu, 'API key', 'k');
    fireEvent.click(within(hudu).getByRole('button', { name: 'Save and test' }));

    expect((await within(hudu).findByRole('alert')).textContent).toBe(
      'That site is on a private network. Use the address your PSA gives you on the internet.',
    );
    expect(within(hudu).getByLabelText('Site')).toBeDefined();
    expect(api.checkIntegration).not.toHaveBeenCalled();
  });

  it('empties the secret boxes on Cancel', async () => {
    const api = fakeApi({ integrations: async () => nothingConnected });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Connect' }));
    type(cw, 'Private key', 'typed-then-cancelled');
    fireEvent.click(within(cw).getByRole('button', { name: 'Cancel' }));
    expect(document.body.innerHTML).not.toContain('typed-then-cancelled');

    fireEvent.click(within(cw).getByRole('button', { name: 'Connect' }));
    expect((within(cw).getByLabelText('Private key') as HTMLInputElement).value).toBe('');
  });

  it('disconnects only after a second, explicit click, then reads the list again', async () => {
    const api = fakeApi();
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect' }));
    expect(api.disconnectIntegration).not.toHaveBeenCalled();
    expect(within(cw).getByRole('group', { name: 'Disconnect ConnectWise Manage?' })).toBeDefined();
    expect(within(cw).getByText(/notes cannot be published to ConnectWise Manage until you connect it again/)).toBeDefined();
    await waitFor(() => expect(document.activeElement?.textContent).toBe('Disconnect ConnectWise Manage?'));

    fireEvent.click(within(cw).getByRole('button', { name: 'Keep it' }));
    expect(within(cw).queryByRole('group')).toBeNull();
    expect(api.disconnectIntegration).not.toHaveBeenCalled();

    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect' }));
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect ConnectWise Manage' }));

    await waitFor(() => expect(api.disconnectIntegration).toHaveBeenCalledWith('connectwise'));
    expect(await within(cw).findByText('ConnectWise Manage is disconnected.')).toBeDefined();
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(2));
  });

  it('goes back to its buttons when a disconnect fails but the list says it is gone', async () => {
    const api = fakeApi();
    api.disconnectIntegration.mockImplementation(async () => {
      throw new ApiError(500, 'The server answered 500. Try again.');
    });
    api.integrations.mockResolvedValueOnce(integrations).mockResolvedValue(nothingConnected);
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect' }));
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect ConnectWise Manage' }));

    expect((await within(cw).findByRole('alert')).textContent).toBe('The server answered 500. Try again.');
    expect(await within(cw).findByRole('button', { name: 'Connect' })).toBeDefined();
    expect(within(cw).queryByRole('group')).toBeNull();
  });

  it.each([
    [404, 'There is nothing connected to remove.'],
    [0, 'ScreenTail could not reach the server. Check your connection and try again.'],
  ])('closes the question when a disconnect answers %i', async (status, message) => {
    const api = fakeApi();
    api.disconnectIntegration.mockImplementation(async () => {
      throw new ApiError(status, message);
    });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect' }));
    fireEvent.click(within(cw).getByRole('button', { name: 'Disconnect ConnectWise Manage' }));

    expect((await within(cw).findByRole('alert')).textContent).toBe(message);
    await waitFor(() => expect(within(cw).queryByRole('group')).toBeNull());
    expect(within(cw).getByRole('button', { name: 'Test connection' })).toBeDefined();
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(2));
  });

  it('shows the newest read of the list when two answer out of order', async () => {
    const both: Integrations = {
      integrations: [integrations.integrations[0]!, { ...huduConnected.integrations[1]! }],
      changes: [],
    };
    const withError = (text: string): Integrations => ({
      ...both,
      integrations: both.integrations.map((row) => ({ ...row, lastCheckedAt: checked, lastResult: 'failed', lastError: text })),
    });
    let answerOlder: (value: Integrations) => void = () => undefined;
    let answerNewer: (value: Integrations) => void = () => undefined;
    const api = fakeApi();
    api.integrations
      .mockResolvedValueOnce(both)
      .mockImplementationOnce(() => new Promise<Integrations>((resolve) => (answerOlder = resolve)))
      .mockImplementationOnce(() => new Promise<Integrations>((resolve) => (answerNewer = resolve)));
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    const hudu = screen.getByRole('region', { name: 'Hudu' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Test connection' }));
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(2));
    fireEvent.click(within(hudu).getByRole('button', { name: 'Test connection' }));
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(3));

    answerNewer(withError('newest'));
    await waitFor(() => expect(within(cw).getAllByText('newest').length).toBeGreaterThan(0));
    answerOlder(withError('stale'));

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(within(cw).queryByText('stale')).toBeNull();
    expect(within(cw).getByText('newest')).toBeDefined();
  });

  it('says the session ended when a save is answered 401', async () => {
    const api = fakeApi({ integrations: async () => nothingConnected });
    api.connectIntegration.mockImplementation(async () => {
      throw new ApiError(401, 'Your session has ended. Sign in again.');
    });
    render(<IntegrationsPage api={api} />);

    const hudu = await screen.findByRole('region', { name: 'Hudu' });
    fireEvent.click(within(hudu).getByRole('button', { name: 'Connect' }));
    type(hudu, 'Site', 'https://acme.huducloud.example');
    type(hudu, 'API key', 'k');
    fireEvent.click(within(hudu).getByRole('button', { name: 'Save and test' }));

    expect((await within(hudu).findByRole('alert')).textContent).toBe('Your session has ended. Sign in again.');
    expect(api.checkIntegration).not.toHaveBeenCalled();
  });

  it('says that disconnecting Hudu forgets its company mappings', async () => {
    render(<IntegrationsPage api={fakeApi({ integrations: async () => huduConnected })} />);

    const hudu = await screen.findByRole('region', { name: 'Hudu' });
    fireEvent.click(within(hudu).getByRole('button', { name: 'Disconnect' }));
    expect(within(hudu).getByText(/the company mappings made on it are forgotten/)).toBeDefined();
  });

  it('tests the stored connection and shows the result', async () => {
    const api = fakeApi();
    api.checkIntegration.mockImplementation(async () => ({ ok: false, message: 'ConnectWise refused the key.', checkedAt: checked }));
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Test connection' }));

    await waitFor(() => expect(api.checkIntegration).toHaveBeenCalledWith('connectwise'));
    expect(within(cw).getByRole('status').textContent).toBe('Test failed: ConnectWise refused the key.');
    await waitFor(() => expect(api.integrations).toHaveBeenCalledTimes(2));
  });

  it('shows a test that could not run as an error', async () => {
    const api = fakeApi();
    api.checkIntegration.mockImplementation(async () => {
      throw new ApiError(404, 'ConnectWise Manage is not connected. Connect it first.', 'not_connected');
    });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    fireEvent.click(within(cw).getByRole('button', { name: 'Test connection' }));

    expect((await within(cw).findByRole('alert')).textContent).toBe('ConnectWise Manage is not connected. Connect it first.');
  });
});
