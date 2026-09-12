-- 0003: retention (ST-044, INV-12). ended_at is when a session finished (finalize); raw_purged_at is when
-- the retention job removed its frames, OCR text, transcript and events, leaving the note and audit rows.
ALTER TABLE sessions ADD COLUMN ended_at TEXT;
ALTER TABLE sessions ADD COLUMN raw_purged_at TEXT;

-- Sessions that finished before this migration would carry ended_at = NULL, and a NULL there would exempt
-- their raw data from retention for good. Backfill from updated_at, which finalize was the last to touch.
UPDATE sessions SET ended_at = updated_at WHERE duration_ms IS NOT NULL;

-- Retention sorts and filters on "when did this session's data stop changing": ended_at when it finished,
-- otherwise when it was created, so a session that never finishes still ages out.
CREATE INDEX sessions_by_retention ON sessions (COALESCE(ended_at, created_at));
