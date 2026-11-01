/**
 * Google Identity Services in the page (ST-099, ADR-0009): Google's own button, which hands back an ID
 * token that the backend checks. The page never sees a password, and holds the token only long enough
 * to send it.
 */

export interface GoogleButton {
  /**
   * Draws Google's button into the element; calls back with the ID token when someone signs in. The
   * nonce is the backend's, and Google signs it into the token, which the backend then accepts once.
   */
  render(element: HTMLElement, nonce: string, onCredential: (credential: string) => void): Promise<void>;
}

interface GoogleIdentity {
  accounts: {
    id: {
      initialize(options: {
        client_id: string;
        nonce?: string;
        callback: (response: { credential?: string }) => void;
        ux_mode?: 'popup';
        auto_select?: boolean;
        cancel_on_tap_outside?: boolean;
      }): void;
      renderButton(element: HTMLElement, options: Record<string, string | number>): void;
    };
  };
}

declare global {
  interface Window {
    google?: GoogleIdentity;
  }
}

const scriptUrl = 'https://accounts.google.com/gsi/client';

let loading: Promise<GoogleIdentity> | undefined;

function load(): Promise<GoogleIdentity> {
  if (window.google?.accounts?.id) {
    return Promise.resolve(window.google);
  }
  loading ??= new Promise<GoogleIdentity>((resolve, reject) => {
    const script = document.createElement('script');
    script.src = scriptUrl;
    script.async = true;
    script.defer = true;
    script.onload = () => (window.google ? resolve(window.google) : reject(new Error('Google sign-in did not load.')));
    script.onerror = () => {
      loading = undefined;
      reject(new Error('Google sign-in could not be loaded. Check your connection, or whether a blocker stops accounts.google.com.'));
    };
    document.head.appendChild(script);
  });
  return loading;
}

/** Google's button for this client id, loaded from Google the first time it is drawn. */
export function googleButton(clientId: string): GoogleButton {
  return {
    async render(element, nonce, onCredential) {
      const google = await load();
      // Initialised again for each nonce: the last call is the one Google uses.
      google.accounts.id.initialize({
        client_id: clientId,
        nonce,
        ux_mode: 'popup',
        auto_select: false,
        cancel_on_tap_outside: true,
        callback: (response) => {
          if (response.credential) {
            onCredential(response.credential);
          }
        },
      });
      // Drawn again in place of the old one, not beside it.
      element.replaceChildren();
      google.accounts.id.renderButton(element, {
        type: 'standard',
        theme: 'outline',
        size: 'large',
        text: 'signin_with',
        shape: 'rectangular',
        logo_alignment: 'left',
        width: 280,
      });
    },
  };
}
