-- 0005: the outbox (ST-064). Work that has to reach the network, kept until it does.
--
-- A technician finishes a job on a train and closes the laptop; the draft has to survive that. So the
-- queue is a table in the encrypted store rather than a list in memory, and it is encrypted for the same
-- reason the rest of the store is: a queued publish holds the note text until it lands.
--
-- state is one of 'pending', 'uncertain', 'done', 'failed'. 'uncertain' is the one that matters: the
-- request went and no answer came back, so whether it happened is unknown. Nothing retries those — a
-- second copy of a note in a customer's ticket is worse than one a technician can see is stuck — and they
-- leave the state only by asking the provider whether the thing exists.
CREATE TABLE outbox (
    id              TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    kind            TEXT NOT NULL,

    -- What makes this piece of work the same piece of work. Finalize can run twice, and this is what
    -- stops that becoming two drafts of one session.
    idempotency_key TEXT NOT NULL,
    payload         TEXT NOT NULL,
    state           TEXT NOT NULL,
    attempts        INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL,
    due_at          TEXT NOT NULL,
    last_attempt_at TEXT,
    last_error      TEXT,
    remote_id       TEXT
);

-- One live piece of work per key. Partial, so a failed attempt can be queued again by hand without
-- having to delete the row that records why it failed.
CREATE UNIQUE INDEX outbox_live_key ON outbox (idempotency_key) WHERE state <> 'failed';

-- The drain's query: the oldest pending item that is due.
CREATE INDEX outbox_due ON outbox (state, due_at, created_at);

-- Retention deletes an outbox row with the session it belongs to (INV-12). ON DELETE CASCADE covers a
-- discarded session; the retention job covers a purged one, and this is the index it uses.
CREATE INDEX outbox_by_session ON outbox (session_id);
