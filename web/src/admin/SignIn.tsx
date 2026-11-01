import { useEffect, useRef, useState } from 'react';
import type { GoogleButton } from '../google';

interface SignInProps {
  /** Google's button, or undefined when this build has no client id. */
  button: GoogleButton | undefined;
  /** Called with Google's ID token. */
  onCredential: (credential: string) => void;
  busy: boolean;
  error: string | undefined;
}

/** Sign up / Sign in (Spec §5 S9): Google's button, and nothing else to type. */
export function SignIn({ button, onCredential, busy, error }: SignInProps) {
  const slot = useRef<HTMLDivElement>(null);
  const [loadError, setLoadError] = useState<string>();
  const latest = useRef(onCredential);

  useEffect(() => {
    latest.current = onCredential;
  }, [onCredential]);

  useEffect(() => {
    if (!button || !slot.current) {
      return;
    }
    button.render(slot.current, (credential) => latest.current(credential)).catch((e: unknown) => {
      setLoadError(e instanceof Error ? e.message : 'Google sign-in could not be loaded.');
    });
  }, [button]);

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
