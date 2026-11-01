import { useEffect, useRef, useState } from 'react';
import type { SignInNonce } from '../api';
import type { GoogleButton } from '../google';
import { messageOf } from './format';

interface SignInProps {
  /** Google's button, or undefined when this build has no client id. */
  button: GoogleButton | undefined;
  /** Asks the backend for a fresh one-time nonce, which Google signs into the token (ADR-0009). */
  nextNonce: () => Promise<SignInNonce>;
  /** Called with Google's ID token. */
  onCredential: (credential: string) => void;
  busy: boolean;
  error: string | undefined;
}

/** Sign up / Sign in (Spec §5 S9): Google's button, and nothing else to type. */
export function SignIn({ button, nextNonce, onCredential, busy, error }: SignInProps) {
  const slot = useRef<HTMLDivElement>(null);
  const [loadError, setLoadError] = useState<string>();
  const latest = useRef(onCredential);

  useEffect(() => {
    latest.current = onCredential;
  }, [onCredential]);

  // Each drawing of the button has a nonce of its own, good for one sign-in. A new round asks for a
  // fresh one: after Google hands back a token (that nonce is spent, whatever the backend says of it)
  // and shortly before the nonce runs out, so a page left open still signs in.
  const [round, setRound] = useState(0);

  useEffect(() => {
    const element = slot.current;
    if (!button || !element) {
      return;
    }
    let live = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    nextNonce()
      .then(async ({ nonce, expiresInSeconds }) => {
        if (!live) {
          return;
        }
        await button.render(element, nonce, (credential) => {
          setRound((r) => r + 1);
          latest.current(credential);
        });
        if (!live) {
          return;
        }
        setLoadError(undefined);
        const refreshIn = Math.max(expiresInSeconds - 60, expiresInSeconds / 2, 30);
        timer = setTimeout(() => setRound((r) => r + 1), refreshIn * 1000);
      })
      .catch((e: unknown) => {
        if (live) {
          setLoadError(messageOf(e));
        }
      });
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [button, nextNonce, round]);

  return (
    <main id="content" className="center-pane" aria-labelledby="signin-title">
      <section className="card narrow">
        <h1 id="signin-title" className="title">
          Sign in to ScreenTail admin
        </h1>
        <p className="secondary">
          Owners sign in with their Google account. The first sign-in of an invited account sets up your organisation.
        </p>

        {button ? (
          <>
            <div ref={slot} className="google-slot" aria-busy={busy} />
            {busy && (
              <p role="status" className="secondary">
                Checking your sign-in with ScreenTail.
              </p>
            )}
          </>
        ) : (
          <div className="banner warning" role="status">
            <p>
              <strong>Sign-in is not configured.</strong> This build has no Google client id. Set{' '}
              <code>VITE_GOOGLE_CLIENT_ID</code> and rebuild, as <code>docs/dev/web-admin.md</code> describes.
            </p>
          </div>
        )}

        {(error ?? loadError) && (
          <div className="banner danger" role="alert">
            <p>{error ?? loadError}</p>
          </div>
        )}
      </section>
    </main>
  );
}
