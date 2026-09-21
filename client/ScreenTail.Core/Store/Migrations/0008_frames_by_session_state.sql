-- 0008: the index the session queries actually use (ST-041; 2026-09-20 review).
--
-- Every query that asks about one session's frames filters on `session_id AND redaction_pending`, and
-- every one of them was answered by `frames_pending (redaction_pending, ts_ms)`. That index leads on a
-- column with two values, one of which -- 0, redacted -- is nearly every row in the store. So the planner
-- searched it for `redaction_pending = 0`, got back the whole store, and then read each row from the
-- table to find out which session it belonged to. Confirmed with EXPLAIN QUERY PLAN against this schema
-- before it was written:
--
--   SELECT ... WHERE session_id = ? AND redaction_pending = 0
--     -> SEARCH frames USING INDEX frames_pending (redaction_pending=?)
--
-- Each of those row lookups decrypts a page. The cost is in the size of the store rather than the size
-- of the session, so it grows with retention: listing sessions took 5.4 seconds over 4,000 frames, with
-- the store's single gate held throughout, which stalls the capture writes behind it.
--
-- Two changes. A composite index that leads on the session, so the same queries become a search of just
-- that session's rows and two of them become covering. And `frames_pending` rebuilt as a partial index
-- over pending rows only: it is what the redaction worker actually wants ("the oldest frame still
-- waiting"), it stays a handful of entries instead of one per frame ever captured, and being partial it
-- cannot be chosen for `redaction_pending = 0` again.
--
-- `frames_by_session (session_id, ts_ms)` stays. It orders a session's frames by time without a sort,
-- which the composite cannot do with `redaction_pending` sitting between the two columns.
DROP INDEX IF EXISTS frames_pending;

CREATE INDEX frames_pending ON frames (ts_ms) WHERE redaction_pending = 1;

CREATE INDEX frames_by_session_state ON frames (session_id, redaction_pending, ts_ms);
