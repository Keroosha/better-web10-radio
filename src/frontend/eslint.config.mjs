// @ts-check
// Flat config for the whole frontend monorepo.
// FIRST-PARTY PACKAGES ONLY (eslint, @eslint/js, typescript-eslint) — no third-party
// community plugins, per the project's supply-chain constraint.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    // Mock files are read-only design reference, not authored source.
    // Build artifacts and deps are never linted.
    ignores: ['**/dist/**', '**/node_modules/**', '**/coverage/**', 'web-stage/mocks/**'],
  },
  js.configs.recommended,
  tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    rules: {
      // SPEC §10: no authored `any` / `unknown`, no untyped payloads. Build must fail.
      '@typescript-eslint/no-explicit-any': 'error',
      'no-restricted-syntax': [
        'error',
        {
          selector: 'TSUnknownKeyword',
          message: 'Authored `unknown` is banned (SPEC §10). Use a named domain type from @web10/shared.',
        },
        {
          selector: 'TSAnyKeyword',
          message: 'Authored `any` is banned (SPEC §10). Use a named domain type from @web10/shared.',
        },
      ],
      // TypeScript already checks for undefined identifiers; the core rule is redundant
      // and produces false positives on DOM/browser globals in .tsx files.
      'no-undef': 'off',
    },
  },
);
