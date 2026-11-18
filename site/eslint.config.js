import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default defineConfig([
  globalIgnores(['dist', '.astro', '.wrangler', 'e2e-results', 'e2e-report', 'node_modules']),
  {
    files: ['**/*.{ts,mjs,js}'],
    extends: [js.configs.recommended, tseslint.configs.recommended],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    rules: {
      // The site's CSP requires Trusted Types: nothing may write HTML from a string.
      'no-restricted-properties': ['error',
        { property: 'innerHTML', message: 'Build DOM nodes instead (the CSP requires Trusted Types).' },
        { property: 'outerHTML', message: 'Build DOM nodes instead (the CSP requires Trusted Types).' },
        { property: 'insertAdjacentHTML', message: 'Build DOM nodes instead (the CSP requires Trusted Types).' },
      ],
      'no-eval': 'error',
      'no-implied-eval': 'error',
    },
  },
]);
