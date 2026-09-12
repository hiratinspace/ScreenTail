# ST-001 legibility: 4K frame downscaled to <= 1600 px

Recall = share of ground-truth words Tesseract reads back. For the screen capture, the native-resolution OCR is the reference.
Open the saved `legibility-*-q80.jpg` files at 100% zoom to judge legibility by eye as well.

| Source | Display scale | 9 pt text height native -> out (px) | Out size | JPEG q70 / q80 / q90 (KB) | Recall native | Recall out q80 |
|---|---|---|---|---|---|---|
| synthetic 3840x2160 | 1x | 12 -> 5 | 1600x900 | 200 / 246 / 329 | 89 % | 0 % |
| synthetic 3840x2160 | 1.25x | 15 -> 6.3 | 1600x900 | 225 / 272 / 360 | 97 % | 0 % |
| synthetic 3840x2160 | 1.5x | 18 -> 7.5 | 1600x900 | 213 / 257 / 341 | 99 % | 6 % |
| synthetic 3840x2160 | 2x | 24 -> 10 | 1600x900 | 175 / 208 / 273 | 99 % | 80 % |
