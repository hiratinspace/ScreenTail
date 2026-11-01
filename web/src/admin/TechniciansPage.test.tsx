import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeApi, technicians } from '../test/fakeApi';
import { TechniciansPage } from './TechniciansPage';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TechniciansPage', () => {
  it('shows seats used of total, each device and each invite', async () => {
    render(<TechniciansPage api={fakeApi()} />);

    expect((await screen.findByText(/seats in use/)).textContent).toBe('1 of 3 seats in use');
    const devices = screen.getByRole('table', { name: 'Technicians and devices' });
    expect(within(devices).getByRole('rowheader', { name: /Tomas Ortiz/ })).toBeDefined();
    expect(within(devices).getByText('TECH-LAPTOP')).toBeDefined();
    expect(within(devices).getByText('Active')).toBeDefined();
    expect(screen.getByText('Waiting for activation')).toBeDefined();
  });

  it('shows an invite code once, copies it, and does not show it again after Done', async () => {
    const writeText = vi.fn(async () => undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    const api = fakeApi();
    render(<TechniciansPage api={api} />);

    fireEvent.change(await screen.findByLabelText('Work email'), { target: { value: ' tech@acme.example ' } });
    fireEvent.change(screen.getByLabelText('Name (optional)'), { target: { value: 'New Tech' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create invite' }));

    expect(await screen.findByText('ABCDE-FGHJK')).toBeDefined();
    expect(api.invite).toHaveBeenCalledWith('tech@acme.example', 'New Tech');
    expect(screen.getByText(/shown once/)).toBeDefined();
    await waitFor(() => expect(document.activeElement?.textContent).toBe('Invite code for tech@acme.example'));

    fireEvent.click(screen.getByRole('button', { name: 'Copy code' }));
    expect(await screen.findByText('Code copied.')).toBeDefined();
    expect(writeText).toHaveBeenCalledWith('ABCDE-FGHJK');

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(screen.queryByText('ABCDE-FGHJK')).toBeNull();
    expect(screen.getByLabelText('Work email')).toBeDefined();
  });

  it('asks for an address before it creates an invite', async () => {
    const api = fakeApi();
    render(<TechniciansPage api={api} />);

    fireEvent.change(await screen.findByLabelText('Work email'), { target: { value: 'not an address' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create invite' }));

    expect(screen.getByText("Enter the technician's work email address.")).toBeDefined();
    expect(screen.getByLabelText('Work email').getAttribute('aria-invalid')).toBe('true');
    expect(api.invite).not.toHaveBeenCalled();
  });

  it('revokes a device only after a second, explicit click, then reads the list again', async () => {
    const api = fakeApi();
    render(<TechniciansPage api={api} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke TECH-LAPTOP (Tomas Ortiz)' }));
    expect(api.revokeDevice).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm revoke TECH-LAPTOP' }));

    await waitFor(() => expect(api.revokeDevice).toHaveBeenCalledWith('d-1'));
    await waitFor(() => expect(api.technicians).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('TECH-LAPTOP is revoked. Its seat is free.')).toBeDefined();
  });

  it('shows a device that activated since the page opened, on Refresh', async () => {
    const api = fakeApi({ technicians: async () => ({ ...technicians, seats: { used: 0, total: 3 }, technicians: [] }) });
    render(<TechniciansPage api={api} />);
    expect(await screen.findByText(/No technician has activated a device yet/)).toBeDefined();

    api.technicians.mockImplementation(async () => technicians);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText('TECH-LAPTOP')).toBeDefined();
    expect(screen.getByText(/seats in use/).textContent).toBe('1 of 3 seats in use');
  });

  it('reads the list again on its own while the page is open', async () => {
    vi.useFakeTimers();
    try {
      const api = fakeApi();
      render(<TechniciansPage api={api} />);
      await vi.advanceTimersByTimeAsync(0);
      expect(api.technicians).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(15_000);

      expect(api.technicians).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });
});
