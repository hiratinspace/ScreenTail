// Turns the app's website renders into the site's screenshots (src/assets/screens/).
// The renders come from CI: the `gallery-screenshots` artifact of a Client (Windows) run holds
// site-*-light.png, drawn at 1280 by 800 and twice the pixels (ShellWindow.Renders.cs, SiteAsync).
//   node scripts/crop-screens.mjs <folder with the site-*-light.png files>
// The Review screen and the capture panel are kept whole. The three small cards are the same
// 620 by 465 region of three screens, so they are one size and their text stays readable.
import { copyFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const from = process.argv[2];
if (!from) throw new Error('Give the folder that holds the site-*-light.png renders.');
const out = resolve(dirname(fileURLToPath(import.meta.url)), '../src/assets/screens');
// In render pixels (two per unit): the content pane beside the rail, from its top-left corner.
const region = { left: 586, top: 26, width: 1240, height: 930 };

await copyFile(resolve(from, 'site-review-light.png'), resolve(out, 'review.png'));
await copyFile(resolve(from, 'site-panel-light.png'), resolve(out, 'panel.png'));
for (const [render, name] of [['review-published', 'receipt'], ['history', 'sessions'], ['settings', 'settings']]) {
  await sharp(resolve(from, `site-${render}-light.png`)).extract(region).png().toFile(resolve(out, `detail-${name}.png`));
}
console.log('Wrote review.png, panel.png and three detail crops to src/assets/screens.');
