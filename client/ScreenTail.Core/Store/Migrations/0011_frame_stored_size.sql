-- 0011: the size of the image a frame row holds, beside the size it was captured at (ST-075).
--
-- width and height are the capture's, and every masked region is in those pixels. The stored image is
-- smaller: redaction downscales it to a 1600 px long edge. A Review blur is drawn in the capture's pixels
-- and painted on the stored image, so it has to be scaled between the two, and guessing the stored size
-- from today's downscale rule would be wrong for any frame written under another one.
--
-- NULL for a frame written before this column existed. Those were all downscaled by the one rule there
-- has ever been, which is what a reader falls back to.
ALTER TABLE frames ADD COLUMN stored_width INTEGER CHECK (stored_width IS NULL OR stored_width > 0);
ALTER TABLE frames ADD COLUMN stored_height INTEGER CHECK (stored_height IS NULL OR stored_height > 0);
