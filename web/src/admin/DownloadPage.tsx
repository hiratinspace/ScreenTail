import { useEffect, useState } from 'react';
import type { AdminApi, Download } from '../api';
import { messageOf } from './format';

/** Download client (Spec §5 S9): the deployment's link, or a plain statement that there is none yet. */
export function DownloadPage({ api }: { api: AdminApi }) {
  const [download, setDownload] = useState<Download>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let live = true;
    api
      .download()
      .then((d) => live && setDownload(d))
      .catch((e: unknown) => live && setError(messageOf(e)));
    return () => {
      live = false;
    };
  }, [api]);

  return (
    <section className="card" aria-label="Windows client">
      {error && (
        <div className="banner danger" role="alert">
          <p>{error}</p>
        </div>
      )}
      {!download && !error && <p role="status">Loading the download link</p>}
      {download && (
        <>
          <p>{download.message}</p>
          {download.url ? (
            <p>
              <a className="button primary" href={download.url} rel="noopener">
                Download ScreenTail for Windows
              </a>
            </p>
          ) : (
            <div className="banner info">
              <p>No download link is set for this deployment. The operator sets Admin:ClientDownloadUrl.</p>
            </div>
          )}
          <ol className="steps">
            <li>Install ScreenTail on the technician's Windows PC.</li>
            <li>Create an invite for them on Technicians and give them the code.</li>
            <li>They enter the code in ScreenTail, in Settings, This device. The device appears on Technicians.</li>
          </ol>
        </>
      )}
    </section>
  );
}
