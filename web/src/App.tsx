import { useCallback, useEffect, useState } from 'react';
import { ApiError, httpApi, type AdminApi, type SignedIn } from './api';
import { FirstRun } from './admin/FirstRun';
import { messageOf } from './admin/format';
import { Shell } from './admin/Shell';
import { SignIn } from './admin/SignIn';
import { googleButton, type GoogleButton } from './google';

type View =
  | { kind: 'loading' }
  | { kind: 'signedOut'; error?: string }
  | { kind: 'firstRun'; credential: string; email: string; error?: string }
  | { kind: 'signedIn'; session: SignedIn };

interface AppProps {
  api?: AdminApi;
  /** Google's button, or undefined when the build has no client id (the "not configured" state). */
  button?: GoogleButton;
}

const clientId = (import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined)?.trim();
const defaultApi = httpApi();
const defaultButton = clientId ? googleButton(clientId) : undefined;

/**
 * The web admin (ST-099): sign in with Google, name the organisation on the first sign-in, then the
 * admin shell. Light theme by default (Spec §5 S9), on the shared tokens.
 */
export function App({ api = defaultApi, button = defaultButton }: AppProps) {
  const [view, setView] = useState<View>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    api
      .session()
      .then((session) => live && setView(session ? { kind: 'signedIn', session } : { kind: 'signedOut' }))
      .catch((e: unknown) => live && setView({ kind: 'signedOut', error: messageOf(e) }));
    return () => {
      live = false;
    };
  }, [api]);

  const signIn = useCallback(
    async (credential: string) => {
      setBusy(true);
      try {
        const answer = await api.signIn(credential);
        setView(
          answer.status === 'signed_in'
            ? { kind: 'signedIn', session: answer }
            : { kind: 'firstRun', credential, email: answer.email },
        );
      } catch (e) {
        setView({ kind: 'signedOut', error: messageOf(e) });
      } finally {
        setBusy(false);
      }
    },
    [api],
  );

  async function create(credential: string, email: string, tenantName: string) {
    setBusy(true);
    try {
      setView({ kind: 'signedIn', session: await api.signUp(credential, tenantName) });
    } catch (e) {
      // Google's token lasts an hour. Past that, or refused, the owner signs in again.
      if (e instanceof ApiError && e.status === 401) {
        setView({ kind: 'signedOut', error: 'Your Google sign-in has expired. Sign in again to finish setting up.' });
      } else {
        setView({ kind: 'firstRun', credential, email, error: messageOf(e) });
      }
    } finally {
      setBusy(false);
    }
  }

  async function signOut() {
    try {
      await api.signOut();
    } catch {
      // Signed out here either way; the cookie expires on its own.
    }
    setView({ kind: 'signedOut' });
  }

  return (
    <>
      <a className="skip-link" href="#content">
        Skip to content
      </a>
      {view.kind === 'loading' && (
        <main id="content" className="center-pane">
          <p role="status">Loading ScreenTail admin</p>
        </main>
      )}
      {view.kind === 'signedOut' && (
        <SignIn button={button} busy={busy} error={view.error} onCredential={(c) => void signIn(c)} />
      )}
      {view.kind === 'firstRun' && (
        <FirstRun
          email={view.email}
          busy={busy}
          error={view.error}
          onCreate={(name) => void create(view.credential, view.email, name)}
          onCancel={() => setView({ kind: 'signedOut' })}
        />
      )}
      {view.kind === 'signedIn' && <Shell api={api} session={view.session} onSignOut={() => void signOut()} />}
    </>
  );
}
