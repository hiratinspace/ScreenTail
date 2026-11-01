import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import type { AdminApi, CreatedInvite, Technicians } from '../api';
import { absoluteTime, messageOf } from './format';
import { Time } from './Time';

/** How often the list is read again while the page is open, so a device that activates shows up. */
export const refreshMs = 15_000;

interface Props {
  api: AdminApi;
}

/** Technicians and seats (Spec §5 S9): invite with a code shown once, seats used of total, device status, revoke. */
export function TechniciansPage({ api }: Props) {
  const [data, setData] = useState<Technicians>();
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState('');

  const load = useCallback(async () => {
    try {
      setData(await api.technicians());
      setError(undefined);
    } catch (e) {
      setError(messageOf(e));
    }
  }, [api]);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), refreshMs);
    return () => window.clearInterval(timer);
  }, [load]);

  return (
    <>
      <div className="page-head">
        <p className="seats" aria-live="polite">
          {data ? (
            <>
              <strong>
                {data.seats.used} of {data.seats.total}
              </strong>{' '}
              seats in use
            </>
          ) : (
            'Loading seats'
          )}
        </p>
        <button type="button" className="button secondary" onClick={() => void load()}>
          Refresh
        </button>
      </div>

      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      <p role="status" className="visually-hidden">
        {status}
      </p>

      <InviteForm
        api={api}
        full={data ? data.seats.used >= data.seats.total : false}
        onInvited={() => void load()}
      />

      <section className="card" aria-labelledby="devices-title">
        <h2 id="devices-title" className="section-title">
          Technicians and devices
        </h2>
        {data && <DeviceTable data={data} api={api} onChanged={load} onStatus={setStatus} onError={setError} />}
      </section>

      <section className="card" aria-labelledby="invites-title">
        <h2 id="invites-title" className="section-title">
          Invites
        </h2>
        {data && <InviteTable data={data} />}
      </section>
    </>
  );
}

function InviteForm({ api, full, onInvited }: { api: AdminApi; full: boolean; onInvited: () => void }) {
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [invalid, setInvalid] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [issued, setIssued] = useState<CreatedInvite>();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!/^[^\s@]+@[^\s@]+$/.test(email.trim())) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    setBusy(true);
    setError(undefined);
    try {
      setIssued(await api.invite(email.trim(), name.trim()));
      setEmail('');
      setName('');
      onInvited();
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card" aria-labelledby="invite-title">
      <h2 id="invite-title" className="section-title">
        Invite a technician
      </h2>
      {issued ? (
        <IssuedCode invite={issued} onDone={() => setIssued(undefined)} />
      ) : (
        <form className="inline-form" onSubmit={(e) => void submit(e)} noValidate>
          <div className="field">
            <label htmlFor="invite-email">Work email</label>
            <input
              id="invite-email"
              type="email"
              autoComplete="off"
              required
              maxLength={320}
              value={email}
              aria-invalid={invalid}
              aria-describedby={invalid ? 'invite-email-error' : undefined}
              onChange={(e) => setEmail(e.target.value)}
            />
            {invalid && (
              <p id="invite-email-error" className="field-error">
                Enter the technician's work email address.
              </p>
            )}
          </div>
          <div className="field">
            <label htmlFor="invite-name">Name (optional)</label>
            <input
              id="invite-name"
              type="text"
              autoComplete="off"
              maxLength={200}
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          <button type="submit" className="button primary" disabled={busy}>
            {busy ? 'Creating invite' : 'Create invite'}
          </button>
        </form>
      )}
      {full && !issued && (
        <p className="hint">
          Every seat is in use. The code still works once a seat is free: revoke a device you no longer use.
        </p>
      )}
      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
    </section>
  );
}

/** The code, once. There is no mail provider yet, so the owner hands it on; it is never shown again. */
function IssuedCode({ invite, onDone }: { invite: CreatedInvite; onDone: () => void }) {
  const [copied, setCopied] = useState('');
  const heading = useRef<HTMLParagraphElement>(null);

  useEffect(() => heading.current?.focus(), []);

  async function copy() {
    try {
      await navigator.clipboard.writeText(invite.code);
      setCopied('Code copied.');
    } catch {
      setCopied('Copying is blocked in this browser. Select the code and copy it.');
    }
  }

  return (
    <div className="issued" role="group" aria-labelledby="issued-title">
      <p id="issued-title" ref={heading} tabIndex={-1} className="issued-title">
        Invite code for {invite.email}
      </p>
      <p className="code">
        {invite.code}
      </p>
      <p className="secondary">
        This code is shown once. Give it to the technician: they enter it in ScreenTail on their PC, in Settings, This
        device. It works on one device until {absoluteTime(invite.expiresAt)} (72 hours).
      </p>
      <div className="actions">
        <button type="button" className="button primary" onClick={() => void copy()}>
          Copy code
        </button>
        <button type="button" className="button secondary" onClick={onDone}>
          Done
        </button>
      </div>
      <p role="status" className="hint">
        {copied}
      </p>
    </div>
  );
}

function DeviceTable({
  data,
  api,
  onChanged,
  onStatus,
  onError,
}: {
  data: Technicians;
  api: AdminApi;
  onChanged: () => Promise<void>;
  onStatus: (s: string) => void;
  onError: (s: string | undefined) => void;
}) {
  const [confirming, setConfirming] = useState<string>();
  const [busy, setBusy] = useState(false);

  if (data.technicians.length === 0) {
    return <p className="empty">No technician has activated a device yet. Invite one above.</p>;
  }

  async function revoke(id: string, name: string) {
    setBusy(true);
    try {
      await api.revokeDevice(id);
      onStatus(`${name} is revoked. Its seat is free.`);
      onError(undefined);
      setConfirming(undefined);
      await onChanged();
    } catch (e) {
      onError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <table className="table" aria-labelledby="devices-title">
      <thead>
        <tr>
          <th scope="col">Technician</th>
          <th scope="col">Device</th>
          <th scope="col">Activated</th>
          <th scope="col">Last seen</th>
          <th scope="col">Status</th>
          <th scope="col">
            <span className="visually-hidden">Actions</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {data.technicians.flatMap((tech) =>
          tech.devices.length === 0
            ? [
                <tr key={tech.id}>
                  <th scope="row">
                    <Person name={tech.displayName} email={tech.email} />
                  </th>
                  <td colSpan={5} className="muted">
                    No device
                  </td>
                </tr>,
              ]
            : tech.devices.map((device) => (
                <tr key={device.id}>
                  <th scope="row">
                    <Person name={tech.displayName} email={tech.email} />
                  </th>
                  <td>{device.name}</td>
                  <td>
                    <Time at={device.activatedAt} />
                  </td>
                  <td>{device.lastSeenAt ? <Time at={device.lastSeenAt} /> : <span className="muted">Never</span>}</td>
                  <td>
                    {device.revokedAt ? (
                      <span className="state">
                        <span className="dot offline" aria-hidden="true" />
                        Revoked
                      </span>
                    ) : (
                      <span className="state">
                        <span className="dot ok" aria-hidden="true" />
                        Active
                      </span>
                    )}
                  </td>
                  <td className="row-actions">
                    {!device.revokedAt &&
                      (confirming === device.id ? (
                        <>
                          <button
                            type="button"
                            className="button danger"
                            disabled={busy}
                            onClick={() => void revoke(device.id, device.name)}
                          >
                            Confirm revoke {device.name}
                          </button>
                          <button type="button" className="button ghost" onClick={() => setConfirming(undefined)}>
                            Cancel
                          </button>
                        </>
                      ) : (
                        <button
                          type="button"
                          className="button secondary"
                          aria-label={`Revoke ${device.name} (${tech.displayName})`}
                          onClick={() => setConfirming(device.id)}
                        >
                          Revoke
                        </button>
                      ))}
                  </td>
                </tr>
              )),
        )}
      </tbody>
    </table>
  );
}

function Person({ name, email }: { name: string; email: string }) {
  return (
    <span className="person">
      <span>{name}</span>
      {name !== email && <span className="muted small">{email}</span>}
    </span>
  );
}

const inviteState: Record<string, string> = { pending: 'Waiting for activation', accepted: 'Activated', expired: 'Expired' };

function InviteTable({ data }: { data: Technicians }) {
  if (data.invites.length === 0) {
    return <p className="empty">No invites yet. Create one above for each technician.</p>;
  }
  return (
    <table className="table" aria-labelledby="invites-title">
      <thead>
        <tr>
          <th scope="col">Email</th>
          <th scope="col">Name</th>
          <th scope="col">Created</th>
          <th scope="col">Expires</th>
          <th scope="col">Status</th>
        </tr>
      </thead>
      <tbody>
        {data.invites.map((invite) => (
          <tr key={invite.id}>
            <th scope="row">{invite.email}</th>
            <td>{invite.displayName}</td>
            <td>
              <Time at={invite.createdAt} />
            </td>
            <td>{invite.status === 'pending' ? <Time at={invite.expiresAt} /> : <span className="muted">-</span>}</td>
            <td>
              <span className="state">
                <span
                  className={`dot ${invite.status === 'accepted' ? 'ok' : invite.status === 'expired' ? 'offline' : 'waiting'}`}
                  aria-hidden="true"
                />
                {inviteState[invite.status]}
              </span>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
