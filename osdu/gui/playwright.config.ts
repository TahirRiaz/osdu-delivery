import { defineConfig, devices } from "@playwright/test";

// Run through `npm run e2e`, which passes `--tsconfig tsconfig.node.json`. Without it Playwright applies tsconfig.json,
// whose catch-all `paths` entry (there so the vendored GUI sources resolve this package's node_modules) maps
// "@playwright/test" to its directory; that resolves to the CommonJS entry, and every named import from it fails.
//
// The e2e suite spins up EVERYTHING itself:
//  1. the OSDU Delivery control plane host (dotnet), pointed at a dedicated local test metadata database; bootstrap
//     provisioning applies SQLFlow's catalog migrations and the OSDU module's beside them, seeds roles, and creates the
//     e2e admin the tests sign in with. The chain's source and ingestion tables are a database of their own;
//  2. the GUI dev server, pointed at that control plane via VITE_API_BASE_URL (which wins in dev mode).
// Requirements on the machine: a SQL Server the suite may create a database on, sqlcmd, and the .NET SDK, same as the
// DB-backed xUnit suites. The default connection uses integrated security on a local server; SQLFLOW_E2E_CATALOG_DB
// names any other, a SQL login included (CI runs the suite that way).
//
// Both ports are overridable so the suite can run next to a developer's own environment. This matters most
// for the GUI port: a long-running `npm run dev` server on the default 5173 points at the DEV control plane
// (public/config.json), and reuseExistingServer would silently reuse it, so every API call in the suite
// would land on the wrong backend and fail with rejected e2e tokens.

const apiPort = Number(process.env.SQLFLOW_E2E_API_PORT ?? 5299);
const guiPort = Number(process.env.SQLFLOW_E2E_GUI_PORT ?? 5173);

// Overridable so a run can provision its own catalog next to an existing one: a database left behind by an older
// build cannot always be migrated forward, and pointing the suite at a fresh name is the non-destructive way past it.
// The suite creates whatever database it is given.
const catalogDb = process.env.SQLFLOW_E2E_CATALOG_DB
  ?? "Server=localhost;Database=SQLFlow_E2E;Trusted_Connection=True;TrustServerCertificate=True";

/** The keywords of a SQL Server connection string, lower-cased, each with its last value. */
export function connectionParts(connectionString: string): Map<string, string> {
  const parts = new Map<string, string>();
  for (const pair of connectionString.split(";")) {
    const at = pair.indexOf("=");
    if (at > 0) {
      parts.set(pair.slice(0, at).trim().toLowerCase(), pair.slice(at + 1).trim());
    }
  }

  return parts;
}

/** The first non-empty value among the keys given, the synonyms a connection string may use for one setting. */
export function connectionValue(parts: Map<string, string>, ...keys: string[]): string | undefined {
  return keys.map((key) => parts.get(key)).find((value) => value !== undefined && value !== "");
}

/** The database a connection string names; the chain's flows read three-part names, so one is required. */
export function databaseOf(connectionString: string): string {
  const database = connectionValue(connectionParts(connectionString), "database", "initial catalog");
  if (!database) {
    throw new Error("The e2e connection string names no database, and the chain's flows read three-part names. Add Database=<name>.");
  }

  return database;
}

/**
 * The same connection with another database: the server, the login and every other setting of the catalog connection,
 * so one SQLFLOW_E2E_CATALOG_DB configures all three databases of the estate and each can still be overridden alone.
 */
function inDatabase(connectionString: string, database: string): string {
  const kept = connectionString
    .split(";")
    .filter((pair) => !/^\s*(database|initial catalog)\s*=/i.test(pair) && pair.trim() !== "");
  return [`Database=${database}`, ...kept].join(";");
}

// The build configuration the hosts run from. Unset, `dotnet run` builds them on the way, inside the servers' start-up
// timeout. CI builds the solution in Release first and names it here, so the hosts start from that build instead.
const dotnetConfiguration = process.env.SQLFLOW_E2E_DOTNET_CONFIGURATION ?? "";
if (!/^[A-Za-z0-9_.-]*$/.test(dotnetConfiguration)) {
  throw new Error(`SQLFLOW_E2E_DOTNET_CONFIGURATION must be a build configuration name such as Release, not '${dotnetConfiguration}'.`);
}

/** The `dotnet` arguments that run one of the module's hosts, from the build configuration named above when there is one. */
export function hostRun(project: string): string[] {
  return ["run", "--project", project, ...(dotnetConfiguration ? ["-c", dotnetConfiguration, "--no-build"] : [])];
}

export const E2E = {
  apiBaseUrl: `http://localhost:${apiPort}`,
  guiBaseUrl: `http://localhost:${guiPort}`,
  adminUsername: "e2e-admin",
  adminPassword: "e2e-admin-password-123456",
  bootstrapSecret: "e2e-bootstrap-secret-0123456789-PADDING",
  // The metadata database: SQLFlow's catalog schema (pipelines, runs, schedules, lineage, sources, users) and the
  // delivery module's osdu schema beside it (the ledger, the mappings, the templates and the partition caches).
  catalogDb,
  // Where the module's osdu schema is. The catalog's database, which is the shape the product ships and the one every
  // spec here runs against; SQLFLOW_E2E_OSDU_DB points the suite at an estate that keeps it apart instead, which is
  // what an estate on Azure SQL has to do. A node is given this connection either way, since a node opens no catalog
  // connection at all.
  osduDb: process.env.SQLFLOW_E2E_OSDU_DB ?? catalogDb,
  // Where the chain's source and ingestion tables live, which is where the volume is in a real estate. The pre and
  // ingestion flows create their own schemas in it; the seed spec creates the database itself.
  sampleDb: process.env.SQLFLOW_E2E_SAMPLE_DB ?? inDatabase(catalogDb, "OsduSample_E2E"),
} as const;

export default defineConfig({
  testDir: "./e2e",
  testMatch: /.*\.spec\.ts$/, // never pick up compiled .js siblings
  globalSetup: "./e2e/global-setup.ts",
  fullyParallel: false, // one control plane + one catalog: keep mutations ordered
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  timeout: 60_000,
  use: {
    baseURL: E2E.guiBaseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: [
    {
      command: ["dotnet", ...hostRun("../hosts/SqlFlow.Delivery.ControlPlane.Host")].join(" "),
      // Readiness, not liveness: liveness is up before bootstrap has created a thing, and an estate of three fresh
      // databases takes long enough to provision that the first test would spend its timeout retrying a login against
      // an estate with no admin yet. Readiness answers when the catalog is reachable and every module database is
      // verified, which is the state every spec assumes.
      url: `${E2E.apiBaseUrl}/health/ready`,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      env: {
        ASPNETCORE_URLS: E2E.apiBaseUrl,
        ASPNETCORE_ENVIRONMENT: "Production",
        // The host reads the nearest .sqlflow/env at its content root or above, which in a developer's checkout is their
        // own: real credentials and a live OSDU partition. This estate is hermetic and must never pick those up, so it
        // opts out and declares below exactly what it resolves.
        SQLFLOW_LOCAL_ENV_FILE: "false",
        // The catalog connection reaches the control plane as a reference, the way a deployment's does: it can carry a
        // password, and a literal secret never goes into configuration.
        ControlPlane__Catalog__ConnectionReference: "${env:SQLFLOW_E2E_CATALOG_CONNECTION}",
        SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
        // The module is given no connection of its own, which is what puts its osdu schema in the catalog's database:
        // the shipped shape, and the one the host has to keep working. Only an estate that separated them declares it.
        ...(process.env.SQLFLOW_E2E_OSDU_DB
          ? {
            Osdu__Database__Connection: "${env:SQLFLOW_E2E_OSDU_CONNECTION}",
            SQLFLOW_E2E_OSDU_CONNECTION: E2E.osduDb,
          }
          : {}),
        // The sample flows read their ingestion tables through this reference. Without it a plan cannot open the tables
        // the seed loaded, and every delivery run fails on a connection it cannot resolve.
        OSDU_SAMPLE_DB: E2E.sampleDb,
        ControlPlane__Jwt__SigningKey: "e2e-signing-key-0123456789abcdef-0123456789abcdef-PADDING",
        ControlPlane__Jwt__BootstrapSecret: E2E.bootstrapSecret,
        // The suite provisions a fresh, dedicated test catalog, so it opts into database creation explicitly.
        // Production startup leaves this off, refusing to create/initialise a database it was pointed at by mistake.
        ControlPlane__Bootstrap__AllowCreate: "true",
        ControlPlane__Bootstrap__AdminUsername: E2E.adminUsername,
        ControlPlane__Bootstrap__AdminPasswordReference: E2E.adminPassword,
        ControlPlane__Cors__AllowedOrigins__0: E2E.guiBaseUrl,
        // The suite polls aggressively; the production rate limit would trip on an innocent run.
        ControlPlane__RateLimit__PermitPerWindow: "1000000",
        // Tight loops so the suite is not waiting on production cadences.
        ControlPlane__Scheduler__PollSeconds: "1",
        ControlPlane__ManagedSync__PollSeconds: "1",
      },
    },
    {
      // The CLI port wins over vite.config.ts, so an overridden gui port really binds (strictPort keeps the
      // failure loud if even that port is taken).
      command: `npm run dev -- --port ${guiPort} --strictPort`,
      url: E2E.guiBaseUrl,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: {
        VITE_API_BASE_URL: E2E.apiBaseUrl,
      },
    },
  ],
});
