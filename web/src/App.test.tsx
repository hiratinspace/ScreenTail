import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './api';
import { App } from './App';
import type { GoogleButton } from './google';
import { fakeApi, session } from './test/fakeApi';

/**
 * Google's button, in the test: drawn as a plain button that hands back the token the test chose.
 * Records the nonce each drawing was given, as Google would sign it into the token.
 */
function fakeGoogle(credential = 'google-id-token', nonces: string[] = []): GoogleButton {
  return {
    async render(element, nonce, onCredential) {
      nonces.push(nonce);
      element.replaceChildren();
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = 'Sign in with Google';
      button.onclick = () => onCredential(credential);
      element.appendChild(button);
    },
  };
}

afterEach(() => {
  window.location.hash = '';
  vi.useRealTimers();
});

describe('App', () => {
  it('says plainly that sign-in is not configured when the build has no client id', async () => {
    render(<App api={fakeApi()} />);

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to ScreenTail admin' })).toBeDefined();
    expect(screen.getByText('Sign-in is not configured.')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Sign in with Google' })).toBeNull();
  });

  it('signs up an allowed account: Google, then the organisation name, then the admin shell', async () => {
    const api = fakeApi({
      signIn: async () => ({ status: 'needs_tenant', email: 'pat@acme.example' }),
    });
    render(<App api={api} button={fakeGoogle('the-token')} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with Google' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Set up your organisation' })).toBeDefined();
    expect(screen.getByText('pat@acme.example')).toBeDefined();

    // The name is required, and said so beside the field rather than in a dialog.
    fireEvent.click(screen.getByRole('button', { name: 'Create organisation' }));
    expect(screen.getByText("Enter your organisation's name.")).toBeDefined();
    expect(api.signUp).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Organisation name'), { target: { value: '  Acme IT ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create organisation' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Technicians and seats' })).toBeDefined();
    expect(api.signIn).toHaveBeenCalledWith('the-token');
    expect(api.signUp).toHaveBeenCalledWith('the-token', 'Acme IT');
    expect(screen.getByText('Acme IT')).toBeDefined();
  });

  it("shows the backend's refusal when the account may not sign up", async () => {
    const api = fakeApi({
      signIn: async () => {
        throw new ApiError(403, 'This Google account is not on the list allowed to set up ScreenTail.', 'signup_not_allowed');
      },
    });
    render(<App api={api} button={fakeGoogle()} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with Google' }));

    expect((await screen.findByRole('alert')).textContent).toContain('not on the list allowed to set up ScreenTail');
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  it('goes straight to the shell when a session is already there, and signs out back to sign-in', async () => {
    const api = fakeApi({ session: async () => session });
    render(<App api={api} button={fakeGoogle()} />);

    expect(await screen.findByRole('heading', { level: 1, name: 'Technicians and seats' })).toBeDefined();
    expect(screen.getByText('pat@acme.example')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to ScreenTail admin' })).toBeDefined();
    expect(api.signOut).toHaveBeenCalledOnce();
  });

  it('says so, and stays signed in, when sign-out does not complete', async () => {
    const api = fakeApi({
      session: async () => session,
      signOut: async () => {
        throw new ApiError(0, 'ScreenTail could not reach the server. Check your connection and try again.');
      },
    });
    render(<App api={api} button={fakeGoogle()} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Sign-out did not complete, so you are still signed in.');
    expect(alert.textContent).toContain('could not reach the server');
    expect(screen.getByRole('navigation', { name: 'Admin sections' })).toBeDefined();
    expect(screen.queryByRole('heading', { name: 'Sign in to ScreenTail admin' })).toBeNull();
  });

  it('moves between sections from the navigation, marks the current one and focuses its heading', async () => {
    render(<App api={fakeApi({ session: async () => session })} />);
    const nav = await screen.findByRole('navigation', { name: 'Admin sections' });
    expect(nav.querySelector('[aria-current="page"]')?.textContent).toBe('Technicians and seats');

    fireEvent.click(screen.getByRole('link', { name: 'Policy' }));

    const heading = await screen.findByRole('heading', { level: 1, name: 'Policy' });
    await waitFor(() => expect(document.activeElement).toBe(heading));
    expect(nav.querySelector('[aria-current="page"]')?.textContent).toBe('Policy');
  });

  it("gives Google the backend's nonce, and a fresh one for the next attempt once a token is handed back", async () => {
    const nonces: string[] = [];
    let issued = 0;
    const api = fakeApi({
      signInNonce: async () => ({ nonce: `nonce-${++issued}`, expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 600 }),
      signIn: async () => {
        throw new ApiError(401, 'Google did not confirm this sign-in. Try signing in again.', 'sign_in_failed');
      },
    });
    render(<App api={api} button={fakeGoogle('t', nonces)} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with Google' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Try signing in again');

    await waitFor(() => expect(nonces).toEqual(['nonce-1', 'nonce-2']));
    expect(api.signInNonce).toHaveBeenCalledTimes(2);
  });

  it('draws the button again with a fresh nonce a minute before the old one runs out', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const nonces: string[] = [];
    let issued = 0;
    const api = fakeApi({
      signInNonce: async () => ({ nonce: `nonce-${++issued}`, expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 300 }),
    });
    render(<App api={api} button={fakeGoogle('t', nonces)} />);
    await waitFor(() => expect(nonces).toEqual(['nonce-1']));

    await vi.advanceTimersByTimeAsync(239_000);
    expect(nonces).toEqual(['nonce-1']);
    await vi.advanceTimersByTimeAsync(2_000);
    await waitFor(() => expect(nonces).toEqual(['nonce-1', 'nonce-2']));
    expect(screen.getAllByRole('button', { name: 'Sign in with Google' })).toHaveLength(1);
  });

  it('says why when no nonce can be had, and draws no button to sign in with', async () => {
    const nonces: string[] = [];
    const api = fakeApi({
      signInNonce: async () => {
        throw new ApiError(429, 'Too many sign-in attempts from this network. Wait a minute and try again.');
      },
    });
    render(<App api={api} button={fakeGoogle('t', nonces)} />);

    expect((await screen.findByRole('alert')).textContent).toContain('Too many sign-in attempts');
    expect(nonces).toEqual([]);
    expect(screen.queryByRole('button', { name: 'Sign in with Google' })).toBeNull();
  });

  it('asks for a nonce again once the Retry-After of a 429 has passed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const nonces: string[] = [];
    let asked = 0;
    const api = fakeApi({
      signInNonce: async () => {
        asked += 1;
        if (asked === 1) {
          throw new ApiError(429, 'Too many sign-in attempts from this network. Wait a minute and try again.', undefined, 30);
        }
        return { nonce: 'nonce-after-wait', expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 600 };
      },
    });
    render(<App api={api} button={fakeGoogle('t', nonces)} />);
    expect((await screen.findByRole('alert')).textContent).toContain('Too many sign-in attempts');

    await vi.advanceTimersByTimeAsync(29_000);
    expect(asked).toBe(1);
    await vi.advanceTimersByTimeAsync(2_000);

    await waitFor(() => expect(nonces).toEqual(['nonce-after-wait']));
    expect(screen.getByRole('button', { name: 'Sign in with Google' })).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('takes a spent button away at once, and leaves none when no fresh nonce can be had', async () => {
    const nonces: string[] = [];
    let asked = 0;
    let release: (() => void) | undefined;
    const api = fakeApi({
      signInNonce: async () => {
        asked += 1;
        if (asked === 1) {
          return { nonce: 'nonce-1', expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 600 };
        }
        await new Promise<void>((resolve) => {
          release = resolve;
        });
        throw new ApiError(503, 'ScreenTail could not reach Google. Try again in a minute.');
      },
      signIn: async () => {
        throw new ApiError(401, 'Google did not confirm this sign-in. Try signing in again.', 'sign_in_failed');
      },
    });
    render(<App api={api} button={fakeGoogle('t', nonces)} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with Google' }));

    // Gone while the next nonce is still being fetched, so the spent one cannot be clicked again...
    await waitFor(() => expect(release).toBeDefined());
    expect(screen.queryByRole('button', { name: 'Sign in with Google' })).toBeNull();

    // ...and still gone once that fetch fails.
    release!();
    expect((await screen.findByRole('alert')).textContent).toContain('Try signing in again');
    expect(screen.queryByRole('button', { name: 'Sign in with Google' })).toBeNull();
    expect(nonces).toEqual(['nonce-1']);
  });

  it('takes the button away when its nonce is about to run out and no fresh one can be had', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let asked = 0;
    const api = fakeApi({
      signInNonce: async () => {
        asked += 1;
        if (asked === 1) {
          return { nonce: 'nonce-1', expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 120 };
        }
        throw new ApiError(503, 'ScreenTail could not reach Google. Try again in a minute.');
      },
    });
    render(<App api={api} button={fakeGoogle()} />);
    expect(await screen.findByRole('button', { name: 'Sign in with Google' })).toBeDefined();

    await vi.advanceTimersByTimeAsync(61_000);

    expect((await screen.findByRole('alert')).textContent).toContain('could not reach Google');
    expect(screen.queryByRole('button', { name: 'Sign in with Google' })).toBeNull();
  });

  it('sends an expired Google sign-in back to the start of signup', async () => {
    const api = fakeApi({
      signIn: async () => ({ status: 'needs_tenant', email: 'pat@acme.example' }),
      signUp: async () => {
        throw new ApiError(401, 'Google did not confirm this sign-in.');
      },
    });
    render(<App api={api} button={fakeGoogle()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with Google' }));
    fireEvent.change(await screen.findByLabelText('Organisation name'), { target: { value: 'Acme IT' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create organisation' }));

    expect((await screen.findByRole('alert')).textContent).toContain('Your Google sign-in has expired');
  });
});
