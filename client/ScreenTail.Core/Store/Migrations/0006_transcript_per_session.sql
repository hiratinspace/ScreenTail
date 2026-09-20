-- 0006: a transcript id is unique within its session, not across the whole table (ST-027).
--
-- The id was a table-wide PRIMARY KEY and the ids are "t-0001" upwards, counted from when the capture
-- service started. So the first thing a technician said after any service restart collided with a row
-- from an earlier session that retention had not yet removed, the insert failed, the exception left the
-- narration loop — which caught only cancellation — and narration stopped silently for the life of the
-- service. Every session after a restart was recorded without a word of what was said.
--
-- SQLite cannot alter a primary key, so the table is rebuilt. Everything else about it is unchanged.
CREATE TABLE transcript_v2 (
    id          TEXT    NOT NULL,
    session_id  TEXT    NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    ts_ms       INTEGER NOT NULL CHECK (ts_ms >= 0),
    end_ms      INTEGER NOT NULL CHECK (end_ms >= ts_ms),
    speaker     TEXT    NOT NULL,
    text        TEXT    NOT NULL,                 -- scrubbed by the pattern engine before it is stored
    frame_id    TEXT,
    confidence  REAL,
    PRIMARY KEY (session_id, id)
);

INSERT INTO transcript_v2 (id, session_id, ts_ms, end_ms, speaker, text, frame_id, confidence)
SELECT id, session_id, ts_ms, end_ms, speaker, text, frame_id, confidence FROM transcript;

DROP TABLE transcript;
ALTER TABLE transcript_v2 RENAME TO transcript;

-- Recreated with the table: dropping the old one took its index with it.
CREATE INDEX transcript_by_session ON transcript (session_id, ts_ms);
