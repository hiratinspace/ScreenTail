-- 0015: the ticket a session went to and that ticket's company (spec v0.6.9 S4, contract v11).
--
-- The inbox rail says the company and the ticket beside the note's title. The company is the PSA's name
-- for the ticket's company, as the ticket search returned it; the device keeps it only once a publish may
-- have reached that ticket, never from a search alone. Shown on screen, never logged (INV-10). A discard
-- clears both with the note and the suggested ticket.
ALTER TABLE sessions ADD COLUMN published_ticket TEXT;
ALTER TABLE sessions ADD COLUMN published_company TEXT;
