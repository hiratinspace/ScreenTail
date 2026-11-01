import { useEffect, useRef, useState } from 'react';
import type { AdminApi, SignedIn } from '../api';
import { DownloadPage } from './DownloadPage';
import { IntegrationsPage } from './IntegrationsPage';
import { PolicyPage } from './PolicyPage';
import { TechniciansPage } from './TechniciansPage';

export const sections = [
  { id: 'technicians', label: 'Technicians and seats' },
  { id: 'policy', label: 'Policy' },
  { id: 'integrations', label: 'Integrations' },
  { id: 'download', label: 'Download client' },
] as const;

export type SectionId = (typeof sections)[number]['id'];

function fromHash(): SectionId {
  const id = window.location.hash.replace(/^#/, '');
  return sections.find((s) => s.id === id)?.id ?? 'technicians';
}

interface ShellProps {
  api: AdminApi;
  session: SignedIn;
  onSignOut: () => void;
}

/** The admin shell (Spec §5 S9): the tenant and owner, the sections, and one section at a time. No marketing chrome. */
export function Shell({ api, session, onSignOut }: ShellProps) {
  const [section, setSection] = useState<SectionId>(fromHash);
  const heading = useRef<HTMLHeadingElement>(null);
  const first = useRef(true);

  useEffect(() => {
    const follow = () => setSection(fromHash());
    window.addEventListener('hashchange', follow);
    return () => window.removeEventListener('hashchange', follow);
  }, []);

  // A new section moves focus to its heading, so a keyboard or screen-reader user lands on what changed.
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    heading.current?.focus();
  }, [section]);

  const current = sections.find((s) => s.id === section) ?? sections[0];

  return (
    <div className="shell">
      <header className="topbar">
        <span className="wordmark">ScreenTail admin</span>
        <span className="tenant">
          {session.tenant.name}
        </span>
        <span className="spacer" />
        <span className="muted small">{session.owner.email}</span>
        <button type="button" className="button ghost" onClick={onSignOut}>
          Sign out
        </button>
      </header>
      <nav className="sidenav" aria-label="Admin sections">
        <ul>
          {sections.map((s) => (
            <li key={s.id}>
              <a
                href={`#${s.id}`}
                aria-current={s.id === section ? 'page' : undefined}
                onClick={() => setSection(s.id)}
              >
                {s.label}
              </a>
            </li>
          ))}
        </ul>
      </nav>
      <main id="content" className="pane">
        <h1 ref={heading} tabIndex={-1} className="title">
          {current.label}
        </h1>
        {section === 'technicians' && <TechniciansPage api={api} />}
        {section === 'policy' && <PolicyPage api={api} />}
        {section === 'integrations' && <IntegrationsPage api={api} />}
        {section === 'download' && <DownloadPage api={api} />}
      </main>
    </div>
  );
}
