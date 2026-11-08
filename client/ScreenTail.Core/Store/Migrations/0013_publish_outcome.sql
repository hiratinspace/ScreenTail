-- 0013: what the last publish of a session left on the ticket (owner decision O2, review of #265).
--
-- Not a session state: History's filters, discard and retention read sessions.state, and none of them
-- should change for this. publish_outcomes is a JSON object from destination (ticket_note, time_entry,
-- kb_article) to "landed" or "unknown", for the destinations that were not plain failures; never a word
-- of the note (INV-10). publish_unconfirmed_at is when one of them first became unknown, and is cleared
-- once none is. A publish_session for a session with it set is refused unless the UI sends
-- publish_again, so a reopened Review or a restarted service cannot put the note on the ticket twice.
ALTER TABLE sessions ADD COLUMN publish_unconfirmed_at TEXT;
ALTER TABLE sessions ADD COLUMN publish_outcomes TEXT;
