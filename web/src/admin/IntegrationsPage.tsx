import { useEffect, useState } from 'react';
import type { AdminApi, Integration } from '../api';
import { messageOf } from './format';
import { Time } from './Time';

/** Integration status (Spec §5 S9): connected or not, and how the last test went. Never a credential. */
export function IntegrationsPage({ api }: { api: AdminApi }) {
  const [rows, setRows] = useState<Integration[]>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let live = true;
    api
      .integrations()
      .then((r) => live && setRows(r))
      .catch((e: unknown) => live && setError(messageOf(e)));
    return () => {
      live = false;
    };
  }, [api]);

  return (
    <>
      <p className="secondary">
        Technicians connect ConnectWise and Hudu from ScreenTail on their PC, in Settings, Integrations. Credentials are
        stored encrypted and are never shown here.
      </p>
      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      {!rows && !error && <p role="status">Loading integrations</p>}
      <div className="grid">
        {rows?.map((row) => (
          <section key={row.provider} className="card" aria-labelledby={`int-${row.provider}`}>
            <h2 id={`int-${row.provider}`} className="section-title">
              {row.name}
            </h2>
            <p className="state">
              <span className={`dot ${row.connected ? (row.lastResult === 'failed' ? 'danger' : 'ok') : 'offline'}`} aria-hidden="true" />
              {row.connected ? 'Connected' : 'Not connected'}
            </p>
            {row.connected ? (
              <dl className="facts">
                <dt>Site</dt>
                <dd>{row.siteUrl}</dd>
                <dt>Connected since</dt>
                <dd>{row.connectedAt ? <Time at={row.connectedAt} /> : <span className="muted">Unknown</span>}</dd>
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
            ) : (
              <p className="hint">Connect it from a technician's PC: Settings, Integrations, {row.name}.</p>
            )}
          </section>
        ))}
      </div>
    </>
  );
}
