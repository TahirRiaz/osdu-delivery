import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { E2E } from "../playwright.config";

/**
 * Builds the e2e fixture: a real local git repository holding the sample delivery estate, which the suite registers as a
 * repo source through the GUI. The control plane then syncs it exactly as it would a customer's remote, so pipelines,
 * runs, schedules, mappings and the delivery pages are all exercised against real documents flowing through the
 * product's own path.
 *
 * The estate is the whole chain, not the delivery flows alone: a record reaches a delivery flow through its ingestion
 * tables, which the pre and ingestion flows load from the sample files. The seed spec runs those flows through the CLI
 * host, so the tables a plan reads are made the way production makes them. The templates the mappings pin and the cache
 * versions live in the catalog rather than the repository, so the seed spec saves the templates through the API and
 * imports the reference files as the cache's first version.
 *
 * Runs use the plan operation, which renders records against the templates, the cache and the ledger without touching an
 * OSDU target.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  const repoDir = join(fixturesDir, "e2e-repo");
  const samplesDir = resolve(here, "..", "..", "samples", "recall-welllog");

  rmSync(repoDir, { recursive: true, force: true });
  mkdirSync(join(repoDir, "flows"), { recursive: true });

  // The mappings the flows pin, the reference records the cache is imported from, and the sample files the pre flows
  // read. The data folders are what makes the chain runnable: without them a pre flow has nothing to land.
  for (const part of ["mappings", "references", "data"]) {
    cpSync(join(samplesDir, part), join(repoDir, part), { recursive: true });
  }

  // Every flow of the estate, each without its schedule and with its tables in the sample database. A fire would be a
  // real run against the sample's OSDU target whenever a suite crossed its cron, and the specs expect flows that join no
  // schedule. The ingestion and delivery flows name their tables in OsduSample, while the pre flows write wherever
  // OSDU_SAMPLE_DB points; unless both name the same database, lineage never links a pre flow to what reads it, and the
  // waves the chain runs in are wrong.
  const sampleDatabase = databaseOf(E2E.sampleDb);
  for (const flow of CHAIN) {
    const shipped = readFileSync(join(samplesDir, "flows", `${flow}.yaml`), "utf8");
    writeFileSync(join(repoDir, "flows", `${flow}.yaml`), inSampleDatabase(withoutSchedule(shipped, flow), flow, sampleDatabase));
  }

  // The cache flow comes along without its schedule. The suite never refreshes it (that would need an OSDU target): the
  // repository sync projects what it declares, and the seed spec imports the sample references as its first version.
  mkdirSync(join(repoDir, "caches"), { recursive: true });
  writeFileSync(
    join(repoDir, "caches", "osdu-reference-cache.yaml"),
    withoutSchedule(readFileSync(join(samplesDir, "caches", "osdu-reference-cache.yaml"), "utf8"), "osdu-reference-cache"),
  );

  const git = (...args: string[]) =>
    execFileSync("git", args, { cwd: repoDir, stdio: "pipe" }).toString("utf8").trim();

  git("init", "-b", "main");
  git("config", "user.email", "e2e@sqlflow.test");
  git("config", "user.name", "OSDU Delivery E2E");
  git("add", "-A");
  git("commit", "-m", "e2e fixture: the recall delivery estate");
  const headSha = git("rev-parse", "HEAD");

  // Tests read the repo path, the exact commit to expect and the database the chain loads into from this meta file
  // (globalSetup runs in a separate process from the specs). Waiting on THIS sha makes re-runs deterministic: a stale
  // synced sha from a previous suite run never satisfies the seed assertions.
  writeFileSync(
    join(fixturesDir, "meta.json"),
    JSON.stringify({ repoDir: repoDir.replace(/\\/g, "/"), headSha, sampleDb: E2E.sampleDb }, null, 2),
  );

  if (!existsSync(join(repoDir, ".git"))) {
    throw new Error(`Fixture repo was not initialized at ${repoDir}.`);
  }
}

/** The delivery flows of the fixture estate, and the pre and ingestion flows that fill the tables they read. */
export const CHAIN = [
  "recall-welllog-pre",
  "recall-welllog-curves-pre",
  "recall-welllog-ing",
  "recall-welllog-curves-ing",
  "recall-welllog",
  "recall-wellbore-pre",
  "recall-wellbore-aliases-pre",
  "recall-wellbore-ing",
  "recall-wellbore-aliases-ing",
  "recall-wellbore",
  "osdu-cache-sync",
] as const;

/** The flows that load the ingestion tables, in the order they have to run: the pre flows land files, the ing flows key them. */
export const LOADING_FLOWS = [
  "recall-welllog-pre",
  "recall-welllog-curves-pre",
  "recall-wellbore-pre",
  "recall-wellbore-aliases-pre",
  "recall-welllog-ing",
  "recall-welllog-curves-ing",
  "recall-wellbore-ing",
  "recall-wellbore-aliases-ing",
] as const;

/**
 * A sample flow without its top-level schedule block. The suite triggers every run itself: a fire would be a real run
 * against the sample's OSDU target whenever a suite crossed its cron, and the specs expect flows that join no schedule,
 * so the runs board and the schedules page show only what the suite created.
 */
function withoutSchedule(yaml: string, flow: string): string {
  const stripped = yaml.replace(/^schedule:\r?\n(?:[ \t].*\r?\n)*/m, "");
  if (/^schedule:/m.test(stripped)) {
    throw new Error(`The fixture flow '${flow}' still declares a schedule block; the e2e suite needs flows that join no schedule.`);
  }

  return stripped;
}

/** The database the shipped ingestion and delivery flows name their tables in. */
const SHIPPED_DATABASE = "OsduSample";

/**
 * A sample flow whose tables live in the given database rather than the shipped OsduSample: every
 * `object: OsduSample.<schema>.<table>` becomes the bracketed three-part name in that database. Any other mention of
 * OsduSample fails the setup, so a shipped document that names it somewhere new cannot leave the estate reading a
 * database the suite never loaded.
 */
function inSampleDatabase(yaml: string, flow: string, database: string): string {
  const quoted = `[${database.replace(/]/g, "]]")}]`;
  const rewritten = yaml.replace(
    /^([ \t]*)object:[ \t]*OsduSample\.(\w+)\.(\w+)[ \t]*$/gm,
    (_match, indent: string, schema: string, table: string) => `${indent}object: "${quoted}.[${schema}].[${table}]"`,
  );
  if (rewritten.includes(`${SHIPPED_DATABASE}.`)) {
    throw new Error(
      `The fixture flow '${flow}' still names ${SHIPPED_DATABASE} somewhere other than an object: <database>.<schema>.<table> value, so the e2e estate would read a database the suite never loaded.`,
    );
  }

  return rewritten;
}

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
function databaseOf(connectionString: string): string {
  const database = connectionValue(connectionParts(connectionString), "database", "initial catalog");
  if (!database) {
    throw new Error("The e2e sample database connection string names no database, and the chain's flows read three-part names. Add Database=<name>.");
  }

  return database;
}
