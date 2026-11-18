// Renders the share card (src/assets/og.png) and the icons (public/favicon.ico, apple-touch-icon.png)
// from the brand files, in the site's own fonts. Run by hand when the brand or the headline changes:
//   node scripts/make-images.mjs
// The outputs are committed, so a build never needs a browser.
import { readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from '@playwright/test';
import sharp from 'sharp';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const brand = resolve(root, '../shared/design/brand');
const font = (p) => pathToFileURL(resolve(root, 'node_modules', p)).href;
const lockup = await readFile(resolve(root, 'src/assets/brand/lockup.svg'), 'utf8');

const html = `<!doctype html><html><head><style>
@font-face { font-family: S; src: url(${font('@fontsource-variable/schibsted-grotesk/files/schibsted-grotesk-latin-wght-normal.woff2')}) format('woff2'); font-weight: 400 900; }
@font-face { font-family: M; src: url(${font('@fontsource/geist-mono/files/geist-mono-latin-500-normal.woff2')}) format('woff2'); }
html, body { margin: 0; }
body { width: 1200px; height: 630px; background: #f7f5fe; font-family: S; color: #141030; position: relative; overflow: hidden;
  background-image: linear-gradient(#ddd8ef 1px, transparent 1px), linear-gradient(90deg, #ddd8ef 1px, transparent 1px); background-size: 60px 60px; background-position: -1px -1px; }
.card { position: absolute; inset: 60px; background: #fff; border: 1px solid #c9c1ea; padding: 64px 72px; display: flex; flex-direction: column; justify-content: space-between; }
.card::after { content: ''; position: absolute; inset: 12px -13px -13px 12px; border: 1px solid #ddd8ef; z-index: -1; }
.logo svg { height: 52px; width: auto; display: block; }
h1 { font-size: 66px; line-height: 1.03; letter-spacing: -0.035em; font-weight: 500; margin: 0; }
h1 span { color: #6a43c4; }
.foot { display: flex; justify-content: space-between; align-items: center; font: 500 20px/1 M; color: #464160; letter-spacing: .02em; }
.pill { display: inline-flex; align-items: center; gap: 12px; background: #141030; color: #fff; border-radius: 999px; padding: 12px 22px; font: 500 20px/1 S; }
.pill i { width: 12px; height: 12px; border-radius: 50%; background: #ff6b6b; }
</style></head><body><div class="card">
<div class="logo">${lockup}</div>
<h1>Fix the problem.<br><span>The note is already written.</span></h1>
<div class="foot"><span>Ticket notes for MSP remote sessions</span><span class="pill"><i></i>Looking for pilot MSPs</span></div>
</div></body></html>`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
// A file page, not setContent: an about:blank page may not load the file:// fonts.
const card = resolve(tmpdir(), `screentrail-og-${process.pid}.html`);
await writeFile(card, html);
await page.goto(pathToFileURL(card).href);
await page.evaluate(() => document.fonts.ready);
await page.screenshot({ path: resolve(root, 'src/assets/og.png') });
await browser.close();
await rm(card);

const icon = await readFile(resolve(brand, 'app-icon-256.svg'));
await writeFile(resolve(root, 'public/apple-touch-icon.png'), await sharp(icon, { density: 300 }).resize(180, 180).png().toBuffer());
// A one-image ICO wrapping a 32 px PNG, which every current browser reads.
const png32 = await sharp(icon, { density: 300 }).resize(32, 32).png().toBuffer();
const header = Buffer.alloc(22);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(1, 4);
header.writeUInt8(32, 6); header.writeUInt8(32, 7); header.writeUInt8(0, 8); header.writeUInt8(0, 9);
header.writeUInt16LE(1, 10); header.writeUInt16LE(32, 12); header.writeUInt32LE(png32.length, 14); header.writeUInt32LE(22, 18);
await writeFile(resolve(root, 'public/favicon.ico'), Buffer.concat([header, png32]));
console.log('wrote src/assets/og.png, public/apple-touch-icon.png, public/favicon.ico');
