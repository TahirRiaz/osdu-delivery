// Where ESLint and editors find the GUI's lint configuration. The configuration and the toolchain it runs on live in the
// lint workspace (lint/): typescript-eslint parses through TypeScript's compiler API, which TypeScript 7 no longer
// publishes, so the workspace installs the newest TypeScript typescript-eslint supports, and package.json's overrides
// keep every package the workspace installs on it. The GUI itself is built and type-checked with TypeScript 7.
export { default } from "./lint/config.js";
