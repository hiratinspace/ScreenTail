-- ST-098: the draft as it was first written, kept beside the draft as edited, so the edit ratio
-- measures the technician's changes against what the model produced. Set once, on the first save;
-- later saves leave it alone. Purged with the draft (INV-12).
ALTER TABLE sessions ADD COLUMN original_draft_json TEXT;
