-- 0004: the audit log becomes append-only in a way that can be checked (ST-045, INV-10).
--
-- Until now the log was a table anyone with the store key could edit without leaving a mark. An audit log
-- that cannot show it is intact is a log that proves nothing in the one conversation it exists for: a
-- customer asking what was captured on their machine. Each row now carries the hash of the row before it,
-- so removing, reordering or editing any of them breaks the chain from that point on.
--
-- detail holds a short label and never content: a mask kind, a suppression reason, a destination host. The
-- application constrains what may go in it; this column exists so those things do not have to be squeezed
-- into the type name or left out.
ALTER TABLE audit_log ADD COLUMN detail TEXT;
ALTER TABLE audit_log ADD COLUMN prev_hash TEXT;
ALTER TABLE audit_log ADD COLUMN hash TEXT;

-- Rows written before this migration have no hashes and cannot be given honest ones: whatever we computed
-- now would attest to nothing, because there was no chain when they were written. They are left unhashed
-- and verification reports where the chain begins, rather than pretending the earlier rows were covered.
CREATE INDEX audit_by_session ON audit_log (session_id, id);
