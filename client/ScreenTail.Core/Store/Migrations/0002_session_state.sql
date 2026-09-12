-- 0002: persist the session state machine (ST-020) so a crashed service can recover orphans.
-- Values are the wire names in ScreenTail.Shared.Ipc.CaptureStates plus 'discarded'.
ALTER TABLE sessions ADD COLUMN state TEXT NOT NULL DEFAULT 'recording';
ALTER TABLE sessions ADD COLUMN state_reason TEXT;
ALTER TABLE sessions ADD COLUMN state_changed_at TEXT;

CREATE INDEX sessions_by_state ON sessions (state);
