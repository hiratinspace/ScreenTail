// Runs after `astro build`. It proves the built site needs nothing the Content-Security-Policy in
// public/_headers forbids, so a dependency that slips a script or a tracker into the output fails the
// build here instead of reaching visitors.
//
//   - no inline <script>, no <style> element, no style="" or on*="" attribute
//   - every script, stylesheet, image, font and icon is a file on this site
//   - CSS loads nothing from another site
//   - no em dashes in what a visitor reads (the owner's rule, 2026-11-13)
//   - with PRODUCTION=1 (the deploy job), no placeholder domain or address is left
import { readdir, readFile } from 'node:fs/promises';
import { join, relative, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(root, 'dist');
const production = process.env.PRODUCTION === '1';
const problems = [];

async function* files(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* files(path);
    else yield path;
  }
}

// Resource-loading attributes. <a href> is a link a visitor follows, not a load, so it may leave.
const resourceAttr = /<(script|link|img|source|iframe|embed|object|video|audio|track|use|image)\b[^>]*?\s(?:src|href|srcset|data|poster|xlink:href)\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi;
const external = /^(?:[a-z][a-z0-9+.-]*:)?\/\//i;

const htmlFiles = [];
const cssFiles = [];
for await (const file of files(dist)) {
  if (file.endsWith('.html')) htmlFiles.push(file);
  else if (file.endsWith('.css')) cssFiles.push(file);
}
if (htmlFiles.length === 0) problems.push('dist has no HTML: did astro build run?');

for (const file of htmlFiles) {
  const name = relative(dist, file);
  const html = await readFile(file, 'utf8');

  for (const m of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    const attrs = m[1] ?? '';
    const isJsonLd = /type\s*=\s*["']?application\/ld\+json/i.test(attrs);
    if (!/\ssrc\s*=/.test(attrs) && !isJsonLd) problems.push(`${name}: inline <script> (the CSP allows only script files)`);
    if (isJsonLd) problems.push(`${name}: JSON-LD would need a CSP hash; add it there first`);
  }
  if (/<style\b/i.test(html)) problems.push(`${name}: <style> element (the CSP allows only stylesheet files)`);
  if (/\sstyle\s*=\s*["']/i.test(html)) problems.push(`${name}: style="" attribute (the CSP forbids inline styles)`);
  if (/\son[a-z]+\s*=\s*["']/i.test(html)) problems.push(`${name}: inline event handler attribute`);
  if (/javascript:/i.test(html)) problems.push(`${name}: javascript: URL`);

  for (const m of html.matchAll(resourceAttr)) {
    // rel="canonical" names this page's own public address; nothing is loaded from it.
    if (/\srel\s*=\s*["']?canonical/i.test(m[0])) continue;
    const value = (m[2] ?? '').replace(/^["']|["']$/g, '');
    for (const part of value.split(',')) {
      const url = part.trim().split(/\s+/)[0] ?? '';
      if (external.test(url) || url.startsWith('data:')) problems.push(`${name}: <${m[1]}> loads ${url.slice(0, 80)} from outside the site`);
    }
  }

  const text = html.replace(/<script[\s\S]*?<\/script>/gi, '').replace(/<[^>]+>/g, ' ');
  if (text.includes('\u2014')) problems.push(`${name}: an em dash in the page text`);
}

for (const file of cssFiles) {
  const name = relative(dist, file);
  const css = await readFile(file, 'utf8');
  for (const m of css.matchAll(/url\(\s*(['"]?)([^'")]+)\1\s*\)/gi)) {
    const url = m[2] ?? '';
    if (external.test(url)) problems.push(`${name}: url(${url.slice(0, 80)}) from outside the site`);
  }
  if (/@import\s+(?:url\()?\s*['"]?(?:https?:)?\/\//i.test(css)) problems.push(`${name}: @import from outside the site`);
}

if (production) {
  for await (const file of files(dist)) {
    if (!/\.(html|txt|xml)$/.test(file)) continue;
    const body = await readFile(file, 'utf8');
    if (body.includes('screentrail.example')) problems.push(`${relative(dist, file)}: still has the placeholder domain; set SITE_URL and the SITE_*_EMAIL variables`);
  }
}

if (problems.length > 0) {
  console.error(`check-dist: ${problems.length} problem(s)\n  ${problems.join('\n  ')}`);
  process.exit(1);
}
console.log(`check-dist: ${htmlFiles.length} pages and ${cssFiles.length} stylesheets need nothing the CSP forbids${production ? ', and no placeholder is left' : ''}.`);
