-- 0001: sessions, timeline, frames with the two-stage redaction lifecycle, transcript, audit.
-- Content lives only in this encrypted file (SQLCipher). The CHECK constraints on frames put INV-1's
-- shape into the storage layer: a pending frame can carry nothing readable, and a redacted frame must
-- record when it was redacted.

CREATE TABLE sessions (
    id                        TEXT PRIMARY KEY,
    started_at                TEXT    NOT NULL,   -- ISO 8601 with offset
    remote_tool_kind          TEXT    NOT NULL,
    remote_tool_version       TEXT,
    duration_ms               INTEGER,
    partial_capture           INTEGER NOT NULL DEFAULT 0 CHECK (partial_capture IN (0, 1)),
    frames_purged_unredacted  INTEGER NOT NULL DEFAULT 0 CHECK (frames_purged_unredacted >= 0),
    local_only                INTEGER NOT NULL DEFAULT 0 CHECK (local_only IN (0, 1)),
    policy_version            TEXT,
    draft_json                TEXT,               -- DraftNote as session.v1 JSON; NULL until drafted
    created_at                TEXT    NOT NULL,
    updated_at                TEXT    NOT NULL
);

CREATE TABLE events (
    session_id  TEXT    NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    seq         INTEGER NOT NULL,
    ts_ms       INTEGER NOT NULL CHECK (ts_ms >= 0),
    json        TEXT    NOT NULL,                 -- one SessionEvent as session.v1 JSON
    PRIMARY KEY (session_id, seq)
);

CREATE TABLE frames (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT    NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    ts_ms               INTEGER NOT NULL CHECK (ts_ms >= 0),
    trigger             TEXT    NOT NULL,
    width               INTEGER NOT NULL CHECK (width > 0),
    height              INTEGER NOT NULL CHECK (height > 0),
    cursor_x            INTEGER,
    cursor_y            INTEGER,
    redaction_pending   INTEGER NOT NULL DEFAULT 1 CHECK (redaction_pending IN (0, 1)),
    redacted_at         TEXT,
    ocr_text            TEXT,
    masked_regions_json TEXT    NOT NULL DEFAULT '[]',
    sensitive_context   INTEGER NOT NULL DEFAULT 0 CHECK (sensitive_context IN (0, 1)),
    excluded_by_user    INTEGER NOT NULL DEFAULT 0 CHECK (excluded_by_user IN (0, 1)),
    image               BLOB    NOT NULL,
    -- INV-1: pending frames carry nothing readable; redacted frames say when.
    CHECK (redaction_pending = 0 OR (ocr_text IS NULL AND redacted_at IS NULL)),
    CHECK (redaction_pending = 1 OR redacted_at IS NOT NULL)
);

CREATE INDEX frames_by_session ON frames (session_id, ts_ms);
CREATE INDEX frames_pending    ON frames (redaction_pending, ts_ms);

CREATE TABLE transcript (
    id          TEXT PRIMARY KEY,
    session_id  TEXT    NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    ts_ms       INTEGER NOT NULL CHECK (ts_ms >= 0),
    end_ms      INTEGER NOT NULL CHECK (end_ms >= ts_ms),
    speaker     TEXT    NOT NULL,
    text        TEXT    NOT NULL,                 -- already scrubbed by the pattern engine
    frame_id    TEXT,
    confidence  REAL
);

CREATE INDEX transcript_by_session ON transcript (session_id, ts_ms);

-- Counts and states only, never content (INV-10). ST-045 adds the hash chain.
CREATE TABLE audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    at          TEXT    NOT NULL,
    session_id  TEXT,
    type        TEXT    NOT NULL,
    count       INTEGER
);
