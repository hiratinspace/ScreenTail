-- 0014: the tray's count of publishes not confirmed is read from an index (ST-093, review of #274).
--
-- CountUnconfirmedPublishesAsync runs at start and whenever a publish record is written or a session is
-- discarded. With nothing on publish_unconfirmed_at it read every session row ever kept, under the
-- store's gate, for a count that is nearly always zero, and session rows outlive retention. Partial, so
-- the index holds the marked rows alone and stays a handful of entries however large the store grows.
CREATE INDEX IF NOT EXISTS sessions_publish_unconfirmed ON sessions (publish_unconfirmed_at)
    WHERE publish_unconfirmed_at IS NOT NULL;
