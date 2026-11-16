import { useCallback, useEffect, useRef, useState, type FormEvent, type RefObject } from 'react';
import {
  ApiError,
  type AdminApi,
  type CompanyMappingNow,
  type Integration,
  type IntegrationActor,
  type IntegrationChange,
  type IntegrationCheck,
  type IntegrationProvider,
  type Integrations,
} from '../api';
import { absoluteDate, absoluteTime, messageOf } from './format';
import { Time } from './Time';

const providerNames: Record<IntegrationProvider, string> = { connectwise: 'ConnectWise Manage', hudu: 'Hudu' };

function isProvider(provider: string): provider is IntegrationProvider {
  return provider === 'connectwise' || provider === 'hudu';
}

/**
 * Integrations (Spec §5 S9): the owner connects, replaces, tests and disconnects ConnectWise and Hudu,
 * and sees who connected each and what changed recently. Integration credentials can only be changed by
 * the tenant owner. The page never shows a credential, not even part of one: secret boxes are never
 * filled in from the backend, and are emptied after a save or a cancel.
 */
export function IntegrationsPage({ api }: { api: AdminApi }) {
  const [data, setData] = useState<Integrations>();
  const [error, setError] = useState<string>();
  const latest = useRef(0);

  // Reads can overlap (a test and a disconnect both read the list again); only the newest one's answer
  // is shown, so a slow older answer cannot put back a card that was just disconnected.
  const load = useCallback(async () => {
    const mine = ++latest.current;
    try {
      const answer = await api.integrations();
      if (mine === latest.current) {
        setData(answer);
        setError(undefined);
      }
    } catch (e) {
      if (mine === latest.current) {
        setError(messageOf(e));
      }
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  const names = new Map<string, string>(Object.entries(providerNames));
  data?.integrations.forEach((row) => names.set(row.provider, row.name));

  return (
    <>
      <p className="secondary">
        Connect ConnectWise and Hudu here. Technicians see each connection in ScreenTail on their PC, in Settings,
        Integrations, but cannot change it, and they choose company mappings when they publish. Keys are stored
        encrypted and are never shown again, not even in part.
      </p>
      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      {!data && !error && <p role="status">Loading integrations</p>}
      <div className="grid">
        {data?.integrations.map((row) => <IntegrationCard key={row.provider} row={row} api={api} onChanged={load} />)}
      </div>
      {data && <MappingList mappings={data.mappings ?? []} />}
      {data && <ChangeList changes={data.changes} names={names} />}
    </>
  );
}

type Mode = 'view' | 'form' | 'confirm';

interface Outcome {
  tone: 'ok' | 'danger' | 'waiting';
  text: string;
}

function outcomeOf(check: IntegrationCheck): Outcome {
  return check.ok ? { tone: 'ok', text: `Test worked: ${check.message}` } : { tone: 'danger', text: `Test failed: ${check.message}` };
}

function IntegrationCard({ row, api, onChanged }: { row: Integration; api: AdminApi; onChanged: () => Promise<void> }) {
  const [mode, setMode] = useState<Mode>('view');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [outcome, setOutcome] = useState<Outcome>();
  const heading = useRef<HTMLHeadingElement>(null);
  const provider = isProvider(row.provider) ? row.provider : undefined;
  // A question about disconnecting something no longer connected (removed elsewhere, or a disconnect
  // that failed but did happen) is not asked: the card shows its buttons instead.
  const shown: Mode = mode === 'confirm' && !row.connected ? 'view' : mode;
  const headingId = `int-${row.provider}`;

  async function check(which: IntegrationProvider, waiting = `Testing the connection to ${row.name}.`) {
    setBusy(true);
    setError(undefined);
    setOutcome({ tone: 'waiting', text: waiting });
    try {
      setOutcome(outcomeOf(await api.checkIntegration(which)));
    } catch (e) {
      setOutcome(undefined);
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
    await onChanged();
  }

  function close() {
    setMode('view');
    heading.current?.focus();
  }

  async function saved(which: IntegrationProvider) {
    close();
    await check(which, `${row.name} is saved. Testing the connection.`);
  }

  async function disconnect(which: IntegrationProvider) {
    setBusy(true);
    setError(undefined);
    try {
      await api.disconnectIntegration(which);
      close();
      setOutcome({ tone: 'waiting', text: `${row.name} is disconnected.` });
    } catch (e) {
      setError(messageOf(e));
      // Already gone (404), or the server could not be reached: the question no longer helps, so the
      // card goes back to its buttons, and the list read below says what is connected.
      if (e instanceof ApiError && (e.status === 404 || e.status === 0)) {
        close();
      }
    } finally {
      setBusy(false);
    }
    await onChanged();
  }

  return (
    <section className="card" aria-labelledby={headingId}>
      <h2 id={headingId} ref={heading} tabIndex={-1} className="section-title">
        {row.name}
      </h2>
      <p className="state">
        <span className={`dot ${row.connected ? (row.lastResult === 'failed' ? 'danger' : 'ok') : 'offline'}`} aria-hidden="true" />
        {row.connected ? 'Connected' : 'Not connected'}
      </p>
      {row.connected && <ConnectedLine row={row} />}
      {row.connected && (
        <dl className="facts">
          <dt>Site</dt>
          <dd>{row.siteUrl}</dd>
          <dt>Last test</dt>
          <dd>
            {row.lastCheckedAt ? (
              <>
                {row.lastResult === 'ok' ? 'Worked' : 'Failed'}, <Time at={row.lastCheckedAt} />
              </>
            ) : (
              <span className="muted">Not tested yet</span>
            )}
          </dd>
          {row.lastError && (
            <>
              <dt>What went wrong</dt>
              <dd>{row.lastError}</dd>
            </>
          )}
        </dl>
      )}

      {provider && shown === 'form' && (
        <ConnectForm row={row} provider={provider} api={api} onSaved={() => void saved(provider)} onCancel={close} />
      )}

      {provider && shown === 'confirm' && (
        <DisconnectQuestion row={row} provider={provider} busy={busy} onKeep={close} onDisconnect={() => void disconnect(provider)} />
      )}

      {provider && shown === 'view' && (
        <div className="actions">
          {row.connected ? (
            <>
              <button type="button" className="button secondary" disabled={busy} onClick={() => void check(provider)}>
                {busy ? 'Testing' : 'Test connection'}
              </button>
              <button type="button" className="button secondary" disabled={busy} onClick={() => setMode('form')}>
                Replace
              </button>
              <button type="button" className="button secondary" disabled={busy} onClick={() => setMode('confirm')}>
                Disconnect
              </button>
            </>
          ) : (
            <button type="button" className="button primary" disabled={busy} onClick={() => setMode('form')}>
              Connect
            </button>
          )}
        </div>
      )}

      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      <div role="status" className="outcome">
        {outcome && (
          <p className="state">
            <span className={`dot ${outcome.tone}`} aria-hidden="true" />
            {outcome.text}
          </p>
        )}
      </div>
    </section>
  );
}

/** "Name", "Name (DEVICE)", or who it was when the name is not known. */
function actorName(actor: IntegrationActor, startOfSentence = false): string {
  if (actor.kind === 'screentail') {
    return 'ScreenTail';
  }
  const unknown = actor.kind === 'owner' ? 'the owner' : 'a technician';
  let name = actor.name ?? unknown;
  if (startOfSentence && actor.name === null) {
    name = name.charAt(0).toUpperCase() + name.slice(1);
  }
  return actor.device ? `${name} (${actor.device})` : name;
}

function ConnectedLine({ row }: { row: Integration }) {
  const by = row.connectedBy ? actorName(row.connectedBy) : undefined;
  const on = row.connectedAt ? (
    <time dateTime={row.connectedAt} title={absoluteTime(row.connectedAt)}>
      {absoluteDate(row.connectedAt)}
    </time>
  ) : undefined;
  if (!by && !on) {
    return null;
  }
  return (
    <p className="secondary">
      Connected
      {by && ` by ${by}`}
      {on && <> on {on}</>}
    </p>
  );
}

type Field = 'site' | 'companyId' | 'publicKey' | 'privateKey' | 'apiKey';

const siteExample: Record<IntegrationProvider, string> = {
  connectwise: 'https://na.myconnectwise.net',
  hudu: 'https://acme.huducloud.com',
};

/** The address as typed, with https:// put in front when no scheme was typed. */
function siteFrom(typed: string): string {
  const site = typed.trim();
  return site === '' || /^[a-z][a-z0-9+.-]*:\/\//i.test(site) ? site : `https://${site}`;
}

function isHttps(site: string): boolean {
  try {
    return new URL(site).protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Connect or replace. The site box is filled in with the stored site when replacing; the secret boxes are
 * never filled in, are not kept in React state, and are emptied when the form saves or is cancelled.
 */
function ConnectForm({
  row,
  provider,
  api,
  onSaved,
  onCancel,
}: {
  row: Integration;
  provider: IntegrationProvider;
  api: AdminApi;
  onSaved: () => void;
  onCancel: () => void;
}) {
  const [site, setSite] = useState(row.siteUrl ?? '');
  const [problems, setProblems] = useState<Partial<Record<Field, string>>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const siteBox = useRef<HTMLInputElement>(null);
  const companyId = useRef<HTMLInputElement>(null);
  const publicKey = useRef<HTMLInputElement>(null);
  const privateKey = useRef<HTMLInputElement>(null);
  const apiKey = useRef<HTMLInputElement>(null);
  const id = (field: Field) => `${provider}-${field}`;
  const connectWise = provider === 'connectwise';

  useEffect(() => siteBox.current?.focus(), []);

  function clearSecrets() {
    for (const box of [companyId, publicKey, privateKey, apiKey]) {
      if (box.current) {
        box.current.value = '';
      }
    }
  }

  function check(): { found: Partial<Record<Field, string>>; secret: string } {
    const found: Partial<Record<Field, string>> = {};
    if (!isHttps(siteFrom(site))) {
      found.site = `Enter the site's https address, like ${siteExample[provider]}.`;
    }
    if (!connectWise) {
      const key = apiKey.current?.value.trim() ?? '';
      if (key === '') {
        found.apiKey = 'Paste the API key from Hudu, Admin, API Keys.';
      }
      return { found, secret: key };
    }

    const parts: Array<[Field, RefObject<HTMLInputElement>, string]> = [
      ['companyId', companyId, "Enter the company ID, the one typed on ConnectWise's sign-in screen."],
      ['publicKey', publicKey, "Enter the public key, from the API member's API Keys tab."],
      ['privateKey', privateKey, 'Enter the private key. It is shown once, when the API key is created.'],
    ];
    const values = parts.map(([field, box, missing]) => {
      const value = box.current?.value.trim() ?? '';
      if (value === '') {
        found[field] = missing;
      } else if (/[\s+:]/.test(value)) {
        found[field] = "The company ID and keys cannot contain spaces, '+' or ':'.";
      }
      return value;
    });
    const [company = '', pub = '', priv = ''] = values;
    return { found, secret: `${company.toLowerCase()}+${pub}:${priv}` };
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const { found, secret } = check();
    setProblems(found);
    if (Object.keys(found).length > 0) {
      setError('Fix the fields marked below, then save again.');
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      await api.connectIntegration(provider, siteFrom(site), secret);
    } catch (e) {
      setError(messageOf(e));
      setBusy(false);
      return;
    }
    clearSecrets();
    setBusy(false);
    onSaved();
  }

  function cancel() {
    clearSecrets();
    onCancel();
  }

  function described(field: Field, hint?: string) {
    return problems[field] ? `${id(field)}-error` : hint ? `${id(field)}-hint` : undefined;
  }

  function problem(field: Field) {
    return problems[field] ? (
      <p id={`${id(field)}-error`} className="field-error">
        {problems[field]}
      </p>
    ) : null;
  }

  /** A plain function, not a component, so a re-render never remounts the box and loses what was typed. */
  function secretBox(field: Field, label: string, box: RefObject<HTMLInputElement>, hint?: string) {
    return (
      <div className="field">
        <label htmlFor={id(field)}>{label}</label>
        <input
          id={id(field)}
          ref={box}
          type="password"
          autoComplete="off"
          spellCheck={false}
          maxLength={4096}
          aria-invalid={problems[field] !== undefined}
          aria-describedby={described(field, hint)}
        />
        {hint && !problems[field] && (
          <p id={`${id(field)}-hint`} className="hint">
            {hint}
          </p>
        )}
        {problem(field)}
      </div>
    );
  }

  const siteHint = connectWise
    ? `Your ConnectWise Manage address, for example ${siteExample.connectwise}.`
    : `Your Hudu address, for example ${siteExample.hudu}.${row.connected ? ' Moving Hudu to another site forgets the company mappings made on this one.' : ''}`;

  return (
    <form className="connect-form" onSubmit={(e) => void submit(e)} noValidate aria-labelledby={id('site') + '-title'}>
      <p id={id('site') + '-title'} className="question">
        {row.connected ? `Replace the ${row.name} key` : `Connect ${row.name}`}
      </p>
      {row.connected && <p className="hint">The key you enter takes the place of the stored one, which is never shown.</p>}
      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      <div className="field">
        <label htmlFor={id('site')}>Site</label>
        <input
          id={id('site')}
          ref={siteBox}
          type="url"
          inputMode="url"
          autoComplete="off"
          spellCheck={false}
          maxLength={500}
          value={site}
          aria-invalid={problems.site !== undefined}
          aria-describedby={described('site', siteHint)}
          onChange={(e) => setSite(e.target.value)}
        />
        {!problems.site && (
          <p id={`${id('site')}-hint`} className="hint">
            {siteHint}
          </p>
        )}
        {problem('site')}
      </div>
      {connectWise ? (
        <>
          <div className="field">
            <label htmlFor={id('companyId')}>Company ID</label>
            <input
              id={id('companyId')}
              ref={companyId}
              type="text"
              autoComplete="off"
              spellCheck={false}
              maxLength={200}
              aria-invalid={problems.companyId !== undefined}
              aria-describedby={described('companyId')}
            />
            {problem('companyId')}
          </div>
          {secretBox('publicKey', 'Public key', publicKey)}
          {secretBox('privateKey', 'Private key', privateKey, 'From System, Members, API Members, API Keys in ConnectWise Manage.')}
        </>
      ) : (
        secretBox(
          'apiKey',
          'API key',
          apiKey,
          'From Hudu, Admin, API Keys. It needs to read companies and create articles and uploads, nothing more.',
        )
      )}
      <div className="actions">
        <button type="submit" className="button primary" disabled={busy}>
          {busy ? 'Saving' : 'Save and test'}
        </button>
        <button type="button" className="button ghost" disabled={busy} onClick={cancel}>
          Cancel
        </button>
      </div>
    </form>
  );
}

function DisconnectQuestion({
  row,
  provider,
  busy,
  onKeep,
  onDisconnect,
}: {
  row: Integration;
  provider: IntegrationProvider;
  busy: boolean;
  onKeep: () => void;
  onDisconnect: () => void;
}) {
  const question = useRef<HTMLParagraphElement>(null);
  const questionId = `${provider}-disconnect-title`;

  useEffect(() => question.current?.focus(), []);

  return (
    <div className="banner danger" role="group" aria-labelledby={questionId}>
      <p id={questionId} ref={question} tabIndex={-1} className="question">
        Disconnect {row.name}?
      </p>
      <p>
        {provider === 'hudu'
          ? 'Its key is deleted from ScreenTail and the company mappings made on it are forgotten. Notes cannot be published to Hudu until you connect it again.'
          : `Its key is deleted from ScreenTail, and notes cannot be published to ${row.name} until you connect it again.`}
      </p>
      <div className="actions">
        <button type="button" className="button secondary" disabled={busy} onClick={onKeep}>
          Keep it
        </button>
        <button type="button" className="button danger" disabled={busy} onClick={onDisconnect}>
          {busy ? 'Disconnecting' : `Disconnect ${row.name}`}
        </button>
      </div>
    </div>
  );
}

function describeChange(change: IntegrationChange): string {
  const company = change.psaCompany ?? 'a company';
  switch (change.action) {
    case 'connected':
      return 'Connected';
    case 'replaced':
      return 'Credential replaced';
    case 'disconnected':
      return 'Disconnected';
    case 'mapped':
      return `Mapped ${company} to ${change.docCompanyName ?? change.docCompanyId ?? 'a company'}`;
    case 'unmapped':
      return `Forgot the mapping for ${company}`;
    case 'mappings_cleared':
      return 'Company mappings cleared';
    case 'mappings_pruned': {
      const count = change.removed ?? 0;
      return `${count} older mapping ${count === 1 ? 'change' : 'changes'} removed`;
    }
    case 'device_slowed':
      return `${change.by.device ?? 'A device'} was slowed down after 30 mapping changes in 10 minutes`;
    default:
      return 'Changed';
  }
}

/** Every PSA company's mapping as it stands, so a burst of later changes never hides one. */
function MappingList({ mappings }: { mappings: CompanyMappingNow[] }) {
  return (
    <section className="card" aria-labelledby="mappings-title">
      <h2 id="mappings-title" className="section-title">
        Company mappings
      </h2>
      {mappings.length === 0 ? (
        <p className="empty">No company mappings yet. Each PSA company a technician maps shows here, with who chose it.</p>
      ) : (
        <table className="table" aria-labelledby="mappings-title">
          <thead>
            <tr>
              <th scope="col">PSA company</th>
              <th scope="col">Documentation company</th>
              <th scope="col">Chosen by</th>
              <th scope="col">When</th>
            </tr>
          </thead>
          <tbody>
            {mappings.map((m) => (
              <tr key={m.psaCompany}>
                <td>{m.psaCompany}</td>
                <td>{m.docCompanyName}</td>
                <td>{m.setBy ? actorName(m.setBy, true) : 'Matched by name'}</td>
                <td>
                  <Time at={m.setAt} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

/** How many recent changes show at first, and how many more each "Show more" adds. */
const changesPage = 20;

function ChangeList({ changes, names }: { changes: IntegrationChange[]; names: Map<string, string> }) {
  const [shown, setShown] = useState(changesPage);
  const visible = changes.slice(0, shown);
  return (
    <section className="card" aria-labelledby="changes-title">
      <h2 id="changes-title" className="section-title">
        Recent changes
      </h2>
      {changes.length === 0 ? (
        <p className="empty">No changes yet. Connections, replaced keys and company mappings show here as they are made.</p>
      ) : (
        <table className="table" aria-labelledby="changes-title">
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Integration</th>
              <th scope="col">Change</th>
              <th scope="col">By</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((change, i) => (
              <tr key={`${change.at}-${i}`}>
                <td>
                  <Time at={change.at} />
                </td>
                <td>{names.get(change.provider) ?? change.provider}</td>
                <td>{describeChange(change)}</td>
                <td>{actorName(change.by, true)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {changes.length > shown && (
        <button type="button" className="button secondary" onClick={() => setShown(shown + changesPage)}>
          Show {Math.min(changesPage, changes.length - shown)} more
        </button>
      )}
    </section>
  );
}
