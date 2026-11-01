import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { fakeApi } from '../test/fakeApi';
import { DownloadPage } from './DownloadPage';
import { IntegrationsPage } from './IntegrationsPage';

const checked = new Date(Date.now() - 60 * 60_000).toISOString();

describe('IntegrationsPage', () => {
  it('says what is connected and how its last test went', async () => {
    const api = fakeApi({
      integrations: async () => [
        { provider: 'connectwise', name: 'ConnectWise Manage', connected: true, siteUrl: 'https://na.myconnectwise.example', connectedAt: checked, lastCheckedAt: checked, lastResult: 'failed', lastError: 'ConnectWise refused the key.' },
        { provider: 'hudu', name: 'Hudu', connected: false, siteUrl: null, connectedAt: null, lastCheckedAt: null, lastResult: null, lastError: null },
      ],
    });
    render(<IntegrationsPage api={api} />);

    const cw = await screen.findByRole('region', { name: 'ConnectWise Manage' });
    expect(within(cw).getByText('Connected')).toBeDefined();
    expect(within(cw).getByText('https://na.myconnectwise.example')).toBeDefined();
    expect(within(cw).getByText('ConnectWise refused the key.')).toBeDefined();
    expect(within(cw).getByText(/Failed/)).toBeDefined();
    const hudu = screen.getByRole('region', { name: 'Hudu' });
    expect(within(hudu).getByText('Not connected')).toBeDefined();
  });
});

describe('DownloadPage', () => {
  it('says there is no download yet when none is configured', async () => {
    render(<DownloadPage api={fakeApi()} />);

    expect(await screen.findByText(/No client download is published/)).toBeDefined();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('links to the configured installer', async () => {
    const api = fakeApi({ download: async () => ({ url: 'https://downloads.example/ScreenTail.msi', message: 'Install ScreenTail.' }) });
    render(<DownloadPage api={api} />);

    const link = await screen.findByRole('link', { name: 'Download ScreenTail for Windows' });
    expect(link.getAttribute('href')).toBe('https://downloads.example/ScreenTail.msi');
  });
});
