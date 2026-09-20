-- 0007: where the audit log is supposed to end (ST-045).
--
-- The hash chain catches a row that was edited, removed from the middle, or reordered: every hash after
-- it stops matching. It does not catch rows removed from the end. Delete the last fifty and what remains
-- is a shorter chain that verifies perfectly, which is exactly the edit somebody covering a purge would
-- make — and the export would say "all 120 audit rows verify" about a log that had 170 (weaknesses P1-5).
--
-- So the end is written down separately: how many rows there have been, and the hash of the last one.
-- One row, enforced by the CHECK, updated in the same gate as every append.
--
-- This is not a signature either. Someone holding the store key can edit this table too — but they now
-- have to edit two places consistently rather than delete from one, and the claim the chain makes has
-- always been that tampering cannot be made to look like it never happened.
CREATE TABLE audit_head (
    id     INTEGER PRIMARY KEY CHECK (id = 1),
    rows   INTEGER NOT NULL,
    hash   TEXT    NOT NULL,
    at     TEXT    NOT NULL
);

-- Seeded from whatever is already there, so an existing store starts attested from today rather than
-- reporting itself broken. Rows written before this migration are covered from here on; what happened to
-- the log before it is what the chain alone could say, which is the position this migration improves.
INSERT INTO audit_head (id, rows, hash, at)
SELECT 1,
       (SELECT COUNT(*) FROM audit_log),
       COALESCE((SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1), ''),
       COALESCE((SELECT at FROM audit_log ORDER BY id DESC LIMIT 1), '')
WHERE EXISTS (SELECT 1 FROM audit_log);
