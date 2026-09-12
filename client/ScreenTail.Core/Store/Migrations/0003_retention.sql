-- 0003: retention (ST-044, INV-12). ended_at is when a session finished (finalize); raw_purged_at is when
-- the retention job removed its frames, OCR text, transcript and events, leaving the note and audit rows.
ALTER TABLE sessions ADD COLUMN ended_at TEXT;
ALTER TABLE sessions ADD COLUMN raw_purged_at TEXT;

CREATE INDEX sessions_by_ended ON sessions (ended_at);
