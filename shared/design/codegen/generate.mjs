// Turns tokens.json into what each UI consumes (ST-016): WPF resource dictionaries and web CSS variables.
// Same single-source pattern as the session schema: generated files are committed and CI fails if they
// drift from tokens.json (`npm run check`).

import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '../../..');
const tokensFile = resolve(here, '../tokens.json');
const targets = {
  wpfDark: resolve(repoRoot, 'client/ScreenTail.UI/Theme/Tokens.Dark.g.xaml'),
  wpfLight: resolve(repoRoot, 'client/ScreenTail.UI/Theme/Tokens.Light.g.xaml'),
  wpfCommon: resolve(repoRoot, 'client/ScreenTail.UI/Theme/Tokens.Common.g.xaml'),
  css: resolve(repoRoot, 'web/src/styles/tokens.g.css'),
};
const banner = 'Generated from shared/design/tokens.json by shared/design/codegen/generate.mjs. Do not edit by hand.';

const tokens = JSON.parse(await readFile(tokensFile, 'utf8'));
await emit(targets.wpfDark, wpfColors(tokens, 'dark'));
await emit(targets.wpfLight, wpfColors(tokens, 'light'));
await emit(targets.wpfCommon, wpfCommon(tokens));
await emit(targets.css, css(tokens));

async function emit(file, text) {
  await mkdir(dirname(file), { recursive: true });
  await writeFile(file, text.endsWith('\n') ? text : `${text}\n`);
  console.log(`wrote ${relative(repoRoot, file)}`);
}

// Resource keys: "Token.bg.surface" (Color) and "Brush.bg.surface" (SolidColorBrush).
function wpfColors(t, theme) {
  const lines = [
    `<!-- ${banner} Theme: ${theme}. -->`,
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
    '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
    '                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">',
  ];
  for (const [name, def] of Object.entries(t.color)) {
    lines.push(`    <!-- ${def.use} -->`);
    lines.push(`    <Color x:Key="Token.${name}">${def[theme].toUpperCase()}</Color>`);
    lines.push(`    <SolidColorBrush x:Key="Brush.${name}" Color="${def[theme].toUpperCase()}" />`);
  }
  lines.push(`    <sys:String x:Key="Token.theme">${theme}</sys:String>`);
  lines.push(`    <sys:Boolean x:Key="Token.elevation.enabled">${theme === 'dark' ? 'False' : 'True'}</sys:Boolean>`);
  lines.push('</ResourceDictionary>');
  return lines.join('\n');
}

function wpfCommon(t) {
  const lines = [
    `<!-- ${banner} Theme-independent values. -->`,
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
    '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
    '                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">',
    `    <FontFamily x:Key="Font.ui">${xmlEscape(t.font.ui.replaceAll('"', ''))}</FontFamily>`,
    `    <FontFamily x:Key="Font.mono">${xmlEscape(t.font.mono.replaceAll('"', ''))}</FontFamily>`,
  ];
  for (const [k, v] of Object.entries(t.size)) lines.push(`    <sys:Double x:Key="Size.${k}">${v}</sys:Double>`);
  for (const [k, v] of Object.entries(t.weight)) lines.push(`    <FontWeight x:Key="Weight.${k}">${weightName(v)}</FontWeight>`);
  for (const [k, v] of Object.entries(t.lineheight)) lines.push(`    <sys:Double x:Key="LineHeight.${k}">${v}</sys:Double>`);
  for (const [k, v] of Object.entries(t.space)) lines.push(`    <sys:Double x:Key="Space.${k}">${v}</sys:Double>`);
  for (const [k, v] of Object.entries(t.layout)) lines.push(`    <sys:Double x:Key="Layout.${k}">${v}</sys:Double>`);
  lines.push(
    `    <Thickness x:Key="Padding.pane">${t.layout['pane-padding']}</Thickness>`,
    `    <Thickness x:Key="Padding.control">${t.layout['control-padding-x']},${t.layout['control-padding-y']}</Thickness>`,
  );
  for (const [k, v] of Object.entries(t.radius)) lines.push(`    <CornerRadius x:Key="Radius.${k}">${v}</CornerRadius>`);
  for (const [k, v] of Object.entries(t.motion)) {
    if (k.endsWith('-ms')) lines.push(`    <Duration x:Key="Motion.${k.replace('-ms', '')}">0:0:0.${String(v).padStart(3, '0')}</Duration>`);
  }
  for (const [k, v] of Object.entries(t.icon)) lines.push(`    <sys:Double x:Key="Icon.${k}">${v}</sys:Double>`);
  lines.push(`    <sys:Double x:Key="Focus.width">${t.focus.width}</sys:Double>`);
  lines.push('</ResourceDictionary>');
  return lines.join('\n');
}

// Light is the web default (Spec §5 S9); dark applies with [data-theme="dark"] or the OS preference.
function css(t) {
  const colorVars = (theme) => Object.entries(t.color).map(([n, d]) => `  --${cssName(n)}: ${d[theme]};`);
  const shadowVars = (theme) => Object.entries(t.elevation[theme]).map(([n, v]) => `  --shadow-${n}: ${v};`);
  const lines = [
    `/* ${banner} */`,
    ':root {',
    ...colorVars('light'),
    ...shadowVars('light'),
    `  --font-ui: ${t.font.ui};`,
    `  --font-mono: ${t.font.mono};`,
    ...Object.entries(t.size).map(([k, v]) => `  --size-${k}: ${v}px;`),
    ...Object.entries(t.weight).map(([k, v]) => `  --weight-${k}: ${v};`),
    ...Object.entries(t.lineheight).map(([k, v]) => `  --lineheight-${k}: ${v};`),
    ...Object.entries(t.space).map(([k, v]) => `  --space-${k}: ${v}px;`),
    ...Object.entries(t.layout).map(([k, v]) => `  --layout-${k}: ${v}px;`),
    ...Object.entries(t.radius).map(([k, v]) => `  --radius-${k}: ${v}px;`),
    ...Object.entries(t.motion).map(([k, v]) => `  --motion-${k.replace('-ms', '')}: ${typeof v === 'number' ? `${v}ms` : v};`),
    ...Object.entries(t.icon).map(([k, v]) => `  --icon-${k}: ${v}px;`),
    `  --focus-ring: ${t.focus.width}px solid var(--${cssName(t.focus.color)});`,
    '  color-scheme: light;',
    '}',
    ':root[data-theme="dark"] {',
    ...colorVars('dark'),
    ...shadowVars('dark'),
    '  color-scheme: dark;',
    '}',
    '@media (prefers-color-scheme: dark) {',
    '  :root:not([data-theme="light"]) {',
    ...colorVars('dark').map((l) => `  ${l}`),
    ...shadowVars('dark').map((l) => `  ${l}`),
    '    color-scheme: dark;',
    '  }',
    '}',
    '@media (prefers-reduced-motion: reduce) {',
    '  :root { --motion-state: 0ms; --motion-pane: 0ms; }',
    '}',
  ];
  return lines.join('\n');
}

function cssName(token) {
  return token.replaceAll('.', '-');
}

function weightName(v) {
  return { 400: 'Normal', 500: 'Medium', 600: 'SemiBold', 700: 'Bold' }[v] ?? String(v);
}

function xmlEscape(s) {
  return s.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}
