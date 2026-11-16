import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { fakeApi } from '../test/fakeApi';
import { DownloadPage } from './DownloadPage';

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
