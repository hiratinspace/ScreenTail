// Prints the production Wrangler configuration: wrangler.jsonc with the real domain as a custom domain
// and the pilot form's real sender and inbox in place of the placeholders. Used by site-deploy.yml;
// fails if any value is missing, so a half-configured deploy stops here.
import { readFile } from 'node:fs/promises';

const need = (name) => {
  const v = process.env[name];
  if (!v || v.includes('screentrail.example')) {
    console.error(`deploy-config: ${name} is not set (docs/dev/site.md, "First deploy")`);
    process.exit(1);
  }
  return v;
};
const siteUrl = new URL(need('SITE_URL'));
const from = need('PILOT_FROM');
const to = need('PILOT_TO');

// wrangler.jsonc has whole-line // comments only, and no trailing commas.
const text = (await readFile(new URL('../wrangler.jsonc', import.meta.url), 'utf8'))
  .split('\n').filter((line) => !/^\s*\/\//.test(line)).join('\n');
const config = JSON.parse(text);
delete config.$schema;
config.routes = [{ pattern: siteUrl.hostname, custom_domain: true }];
config.send_email = [{ name: 'PILOT_MAIL', destination_address: to, allowed_sender_addresses: [from] }];
config.vars = { ...config.vars, PILOT_FROM: from, PILOT_TO: to };
// Paths in the printed file are read from site/, where the workflow runs Wrangler.
process.stdout.write(`${JSON.stringify(config, null, 2)}\n`);
