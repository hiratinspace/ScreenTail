import { useState, type FormEvent } from 'react';

interface FirstRunProps {
  email: string;
  busy: boolean;
  error: string | undefined;
  onCreate: (tenantName: string) => void;
  onCancel: () => void;
}

/** The first sign-in of an allowed account: name the organisation, and become its owner. */
export function FirstRun({ email, busy, error, onCreate, onCancel }: FirstRunProps) {
  const [name, setName] = useState('');
  const [invalid, setInvalid] = useState(false);

  function submit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    onCreate(trimmed);
  }

  return (
    <main id="content" className="center-pane" aria-labelledby="firstrun-title">
      <section className="card narrow">
        <h1 id="firstrun-title" className="title">
          Set up your organisation
        </h1>
        <p className="secondary">
          You are signed in with Google as <strong>{email}</strong>. You will be the owner of the organisation you create
          here. Next you invite your technicians.
        </p>
        <form onSubmit={submit} noValidate>
          <div className="field">
            <label htmlFor="tenant-name">Organisation name</label>
            <input
              id="tenant-name"
              name="tenant-name"
              type="text"
              autoComplete="organization"
              maxLength={200}
              required
              value={name}
              aria-invalid={invalid}
              aria-describedby={invalid ? 'tenant-name-error' : 'tenant-name-hint'}
              onChange={(e) => setName(e.target.value)}
            />
            {invalid ? (
              <p id="tenant-name-error" className="field-error">
                Enter your organisation's name.
              </p>
            ) : (
              <p id="tenant-name-hint" className="hint">
                The name technicians see when they activate ScreenTail.
              </p>
            )}
          </div>
          {error && (
            <div className="banner danger" role="alert">
              <p>{error}</p>
            </div>
          )}
          <div className="actions">
            <button type="submit" className="button primary" disabled={busy}>
              {busy ? 'Creating organisation' : 'Create organisation'}
            </button>
            <button type="button" className="button secondary" onClick={onCancel} disabled={busy}>
              Use another account
            </button>
          </div>
        </form>
      </section>
    </main>
  );
}
