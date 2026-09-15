import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The OSDU Delivery GUI is SQLFlow's GUI with the OSDU Delivery module installed. SQLFlow's sources are compiled in place
// from the vendored tree, never copied: `@/` resolves into sqlflow/gui/src for both trees (so SQLFlow's own `@/` imports
// keep working), `@osdu/` into this module's src, and every package either tree imports resolves from this build's
// node_modules. That gives one copy of React, the router and the query client at runtime, whether or not sqlflow/gui
// has an install of its own.
//
// Like SQLFlow's GUI this is a separate-origin SPA: the API base URL comes from public/config.json at runtime
// (VITE_API_BASE_URL wins in the dev server), and port 5173 matches the control plane's development CORS allowlist.

const guiRoot = fileURLToPath(new URL(".", import.meta.url));
const moduleSrc = path.resolve(guiRoot, "src");
const sqlflowGui = path.resolve(guiRoot, "../../sqlflow/gui");
const sqlflowSrc = path.resolve(sqlflowGui, "src");

const forwardSlashes = (value: string) => value.replace(/\\/g, "/");

function isInside(file: string, directory: string): boolean {
  const relative = path.relative(directory, file);
  return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
}

/** A package import ("react", "@tanstack/react-query/x"), as opposed to a path, an alias, or a virtual module. */
function isPackageImport(source: string): boolean {
  return !(
    source.startsWith(".")
    || source.startsWith("/")
    || source.startsWith("\0")
    || source.startsWith("@/")
    || source.startsWith("@osdu/")
    || source.includes(":")
  );
}

/**
 * Resolves the packages SQLFlow's sources import as if this build's entry imported them. Without it, node resolution
 * from sqlflow/gui/src would look for sqlflow/gui/node_modules: failing when that folder does not exist, and bundling a
 * second React when it does.
 */
function vendoredPackagesFromThisBuild(): Plugin {
  const anchor = path.join(guiRoot, "index.html");
  return {
    name: "osdu-gui:vendored-packages",
    enforce: "pre",
    async resolveId(source, importer, options) {
      if (importer === undefined || !isPackageImport(source)) {
        return null;
      }

      const importerFile = path.resolve(importer.split("?")[0]);
      if (!isInside(importerFile, sqlflowSrc)) {
        return null;
      }

      return this.resolve(source, anchor, { ...options, skipSelf: true });
    },
  };
}

export default defineConfig({
  plugins: [vendoredPackagesFromThisBuild(), react(), tailwindcss()],
  resolve: {
    alias: [
      { find: /^@osdu\//, replacement: `${forwardSlashes(moduleSrc)}/` },
      { find: /^@\//, replacement: `${forwardSlashes(sqlflowSrc)}/` },
    ],
  },
  server: {
    port: 5173,
    strictPort: true,
    fs: {
      // The vendored SQLFlow sources live outside this folder.
      allow: [guiRoot, sqlflowGui],
    },
    watch: {
      // The e2e suite builds its fixture repository under e2e/.fixtures and runs flows that write work files there while
      // the dev server is up; none of it is source the GUI loads.
      ignored: ["**/e2e/**", "**/test-results/**", "**/playwright-report/**"],
    },
  },
  build: {
    outDir: "dist",
    sourcemap: true,
    chunkSizeWarningLimit: 4096, // monaco is deliberately bundled (no CDN), and it is large
  },
});
