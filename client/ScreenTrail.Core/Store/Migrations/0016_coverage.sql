-- 0016: the coverage banner's denominator (ST-079, spec v0.7.3 S4).
--
-- missed_visits holds each remote session the service saw and did not capture: the remote tool's kind
-- (the sessions' own vocabulary, so History's tool filter applies to both), when the tool first came to
-- the front and last left it, and why no session began, as one word from a fixed list. Nothing about the
-- window: no title, no process, no ticket (INV-10). The CHECK holds the reason to that list, so a word
-- that is not one cannot be stored. The retention purge deletes a row once it ended before the cutoff,
-- with the raw data, and "delete everything" takes the file (INV-12).
--
-- pass_through_sessions names the captured sessions that ended with nothing in them and were discarded
-- rather than drafted (EmptySession): they stay discarded rows in History and are left out of the
-- captured count, as a missed visit that brief is never recorded. A table of its own rather than a
-- column, so the store's downgrade tests can reapply this migration over a sessions table that kept it.
--
-- The moment this migration was applied (schema_migrations.applied_at for 16) is when counting began:
-- the banner never reports a period before it, since the misses before it were never counted.
CREATE TABLE IF NOT EXISTS missed_visits (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    remote_tool_kind  TEXT NOT NULL,
    started_at        TEXT NOT NULL,   -- ISO 8601, UTC
    ended_at          TEXT NOT NULL,   -- ISO 8601, UTC
    reason            TEXT NOT NULL CHECK (reason IN ('auto_start_off', 'tool_switched_off', 'start_held', 'no_indicator', 'busy')),
    CHECK (ended_at >= started_at)
);

CREATE INDEX IF NOT EXISTS missed_visits_by_started ON missed_visits (started_at);
CREATE INDEX IF NOT EXISTS missed_visits_by_ended ON missed_visits (ended_at);

CREATE TABLE IF NOT EXISTS pass_through_sessions (
    session_id  TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE
);
