// Every file in examples/valid must pass session.v1.json; every file in examples/invalid must fail.
// Run with `npm test` in shared/schema. The C# side runs the same examples (ScreenTail.Tests/Schema).

import { readdir, readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const schema = JSON.parse(await readFile(join(root, 'session.v1.json'), 'utf8'));

const ajv = new Ajv2020({ allErrors: true, strict: true, allowUnionTypes: true });
addFormats(ajv);
const validate = ajv.compile(schema);

let failures = 0;
for (const expectation of ['valid', 'invalid']) {
  const dir = join(root, 'examples', expectation);
  for (const file of (await readdir(dir)).filter((f) => f.endsWith('.json')).sort()) {
    const document = JSON.parse(await readFile(join(dir, file), 'utf8'));
    const passed = validate(document);
    if (passed === (expectation === 'valid')) {
      console.log(`ok    ${expectation}/${file}`);
    } else {
      failures++;
      console.error(`FAIL  ${expectation}/${file}: expected ${expectation}`);
      if (!passed) console.error(ajv.errorsText(validate.errors, { separator: '\n      ' }));
    }
  }
}

if (failures > 0) {
  console.error(`${failures} example(s) did not match their expectation.`);
  process.exit(1);
}
