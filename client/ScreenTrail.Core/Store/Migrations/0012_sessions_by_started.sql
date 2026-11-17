-- 0012: the History list is read newest first from an index (ST-049).
--
-- ListSessionsAsync orders every session row by started_at and takes a page. With nothing on started_at
-- the planner read every row ever kept, pulled each draft's title and counted each session's frames,
-- sorted the lot and only then took the page, all under the store's gate. Session rows outlive
-- retention, so that cost only grew. With the index the planner walks it in order and stops at the limit.
CREATE INDEX IF NOT EXISTS sessions_by_started ON sessions (started_at DESC);
