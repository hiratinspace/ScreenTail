import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { defaults, fakeApi } from '../test/fakeApi';
import { PolicyPage } from './PolicyPage';

describe('PolicyPage', () => {
  it('saves every field of the policy, the locked ones included', async () => {
    const api = fakeApi();
    render(<PolicyPage api={api} />);

    fireEvent.change(await screen.findByLabelText('Keep raw session data for (days)'), { target: { value: '3' } });
    fireEvent.click(screen.getByLabelText(/^Local-only:/));
    fireEvent.click(screen.getByLabelText('Lock this setting so technicians cannot change it'));
    fireEvent.change(screen.getByLabelText('Update channel'), { target: { value: 'pilot' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    await waitFor(() => expect(api.setPolicy).toHaveBeenCalledOnce());
    expect(api.setPolicy).toHaveBeenCalledWith({
      retentionDays: 3,
      localOnly: true,
      localOnlyLocked: true,
      captureAllWindows: false,
      confirmAllWindows: false,
      updateChannel: 'pilot',
      updateChannelLocked: false,
      updateCheckHours: 24,
      updateChecksUnderLocalOnly: false,
    });
    expect(await screen.findByText(/Policy saved as version p20261031120000000/)).toBeDefined();
  });

  it('does not save every-window capture until it is confirmed (INV-5)', async () => {
    const api = fakeApi();
    render(<PolicyPage api={api} />);

    fireEvent.click(await screen.findByLabelText('Every window during a session'));
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    expect(api.setPolicy).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('Fix the fields marked below');

    fireEvent.click(screen.getByLabelText(/I understand that every window/));
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    await waitFor(() => expect(api.setPolicy).toHaveBeenCalledOnce());
    expect(api.setPolicy.mock.calls[0]![0]).toMatchObject({ captureAllWindows: true, confirmAllWindows: true });
  });

  it('does not ask again for a confirmation already given', async () => {
    const api = fakeApi({ policy: async () => ({ ...defaults, version: 'p1', captureAllWindows: true }) });
    render(<PolicyPage api={api} />);

    expect((await screen.findByLabelText('Every window during a session')) as HTMLInputElement).toHaveProperty('checked', true);
    expect(screen.queryByLabelText(/I understand that every window/)).toBeNull();
  });

  it('refuses a retention outside 1 to 365 days beside the field', async () => {
    const api = fakeApi();
    render(<PolicyPage api={api} />);

    const retention = await screen.findByLabelText('Keep raw session data for (days)');
    fireEvent.change(retention, { target: { value: '400' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));

    expect(retention.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText('Enter a whole number of days from 1 to 365.')).toBeDefined();
    expect(api.setPolicy).not.toHaveBeenCalled();
  });

  it("shows the backend's refusal in its words", async () => {
    const api = fakeApi({
      setPolicy: async () => {
        throw new Error('The update channel is stable or pilot.');
      },
    });
    render(<PolicyPage api={api} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Save policy' }));

    expect((await screen.findByRole('alert')).textContent).toBe('The update channel is stable or pilot.');
  });
});
