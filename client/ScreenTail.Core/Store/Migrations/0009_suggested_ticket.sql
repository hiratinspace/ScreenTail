-- 0009: the ticket number read off the window when the session started (ST-077).
--
-- Digits only, and only when the title or the clipboard introduced them as a ticket. Never the title
-- itself (INV-10). Review pre-selects the ticket with a Suggested badge; a technician can pick another.
ALTER TABLE sessions ADD COLUMN suggested_ticket TEXT;
