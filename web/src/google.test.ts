import { afterEach, describe, expect, it, vi } from 'vitest';
import { googleButton } from './google';

afterEach(() => {
  delete window.google;
});

describe('googleButton', () => {
  it('hands Google the nonce with the client id, and draws the button in place of the last one', async () => {
    const initialize = vi.fn();
    const renderButton = vi.fn((element: HTMLElement) => element.appendChild(document.createElement('iframe')));
    window.google = { accounts: { id: { initialize, renderButton } } };
    const slot = document.createElement('div');
    const button = googleButton('client-id.apps.googleusercontent.com');

    await button.render(slot, 'nonce-a', () => undefined);
    await button.render(slot, 'nonce-b', () => undefined);

    expect(initialize).toHaveBeenCalledTimes(2);
    expect(initialize.mock.calls[0]![0]).toMatchObject({ client_id: 'client-id.apps.googleusercontent.com', nonce: 'nonce-a', ux_mode: 'popup' });
    expect(initialize.mock.calls[1]![0]).toMatchObject({ nonce: 'nonce-b' });
    expect(slot.children).toHaveLength(1);
  });

  it('passes the ID token Google returns to the page, and ignores an answer without one', async () => {
    let callback: ((response: { credential?: string }) => void) | undefined;
    window.google = {
      accounts: {
        id: {
          initialize: (options) => {
            callback = options.callback;
          },
          renderButton: () => undefined,
        },
      },
    };
    const received: string[] = [];

    await googleButton('cid').render(document.createElement('div'), 'n', (credential) => received.push(credential));
    callback!({});
    callback!({ credential: 'the-id-token' });

    expect(received).toEqual(['the-id-token']);
  });
});
