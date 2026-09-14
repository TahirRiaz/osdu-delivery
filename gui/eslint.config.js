// Lint for the GUI: `npm run lint` must pass with no errors and no warnings, as `npm run build` must.
import js from "@eslint/js";
import { defineConfig, globalIgnores } from "eslint/config";
import reactHooks from "eslint-plugin-react-hooks";
import { reactRefresh } from "eslint-plugin-react-refresh";
import globals from "globals";
import tseslint from "typescript-eslint";

export default defineConfig([
  // Build output, the TypeScript project build cache, playwright's reports, and the fixture repository e2e generates.
  globalIgnores(["dist", ".tsbuild", "node_modules", "playwright-report", "test-results", "e2e/.fixtures"]),
  {
    // The single-page app: browser globals, React's rules of hooks, and components that Vite's fast refresh can swap.
    files: ["src/**/*.{ts,tsx}"],
    extends: [js.configs.recommended, tseslint.configs.recommended, reactHooks.configs.flat.recommended, reactRefresh.configs.vite()],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
  },
  {
    // The e2e suite and the tool configs run in Node.
    files: ["e2e/**/*.ts", "vite.config.ts", "playwright.config.ts", "eslint.config.js"],
    extends: [js.configs.recommended, tseslint.configs.recommended],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.node,
    },
  },
]);
