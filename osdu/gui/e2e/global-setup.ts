import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { E2E, databaseOf } from "../playwright.config";

/**
 * Builds the e2e fixture: a real local git repository holding the sample delivery estate, which the suite registers as a
 * repo source through the GUI. The control plane then syncs it exactly as it would a customer's remote, so pipelines,
 * runs, schedules, mappings and the delivery pages are all exercised against real documents flowing through the
 * product's own path.
 *
 * The estate is the whole chain, not the delivery flows alone: a record reaches a delivery flow through its ingestion
 * tables, which the pre and ingestion flows load from the sample files. The seed spec runs those flows through the CLI
 * host, so the tables a plan reads are made the way production makes them.
 *
 * The repository is laid out per source: one top-level folder for the source, holding its flows, the mappings they pin,
 * the cache they resolve against and the drop-off folder the pre flows load from. That folder is what the catalog and
 * the GUI call a project, so the Repos page shows one project per source.
 *
 * Templates are not repository content. They are catalog objects, captured from OSDU's schema service through the
 * Templates page, so the seed spec saves the bundled schemas in `osdu/samples/templates` through the API instead. Cache
 * versions are catalog objects too, so the seed spec imports the sample records as the cache's first version.
 *
 * Runs use the plan operation, which renders records against the templates, the cache and the ledger without touching an
 * OSDU target.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  const repoDir = join(fixturesDir, "e2e-repo");
  const samplesDir = resolve(here, "..", "..", "samples", "wells");

  // The repository holds one folder per source, which is what the catalog and the GUI call a project: everything the
  // wells source needs (its flows, the mappings they pin, the cache they read and the drop-off folder the pre flows
  // load from) sits under `wells/`, and a second source would be a folder beside it rather than more files mixed into
  // the same `flows/` and `mappings/`.
  const sourceDir = join(repoDir, SOURCE);

  rmSync(repoDir, { recursive: true, force: true });
  mkdirSync(join(sourceDir, "flows"), { recursive: true });

  // The mappings the flows pin and the sample files the pre flows read. The data folder is the source's drop-off point:
  // it is what makes the chain runnable, because without it a pre flow has nothing to land.
  for (const part of ["mappings", "data"]) {
    cpSync(join(samplesDir, part), join(sourceDir, part), { recursive: true });
  }

  // Every flow of the estate, each without its schedule and with its tables in the sample database. A fire would be a
  // real run against the sample's OSDU target whenever a suite crossed its cron, and the specs expect flows that join no
  // schedule. The ingestion and delivery flows name their tables in OsduSample, while the pre flows write wherever
  // OSDU_SAMPLE_DB points; unless both name the same database, lineage never links a pre flow to what reads it, and the
  // waves the chain runs in are wrong. That database is neither the catalog nor the module's: source data is the
  // volume in an estate, and it has a database of its own.
  const sampleDatabase = databaseOf(E2E.sampleDb);
  for (const flow of CHAIN) {
    const shipped = readFileSync(join(samplesDir, "flows", `${flow}.yaml`), "utf8");
    writeFileSync(
      join(sourceDir, "flows", `${flow}.yaml`),
      withoutTheLegalCheck(inSampleDatabase(withoutSchedule(shipped, flow), flow, sampleDatabase)),
    );
  }

  // The document that defines the cache the source's mappings resolve against, under the source that needs it. That
  // document is the whole of what a repository holds about a cache: the cache itself lives in the module's database,
  // captured there by a run. The flow comes along without its schedule, because the suite never refreshes it (that
  // would need an OSDU target), and the seed spec imports the sample records from osdu/samples as its first version.
  mkdirSync(join(sourceDir, "cache"), { recursive: true });
  writeFileSync(
    join(sourceDir, "cache", `${CACHE}.yaml`),
    withoutSchedule(readFileSync(join(samplesDir, "cache", `${CACHE}.yaml`), "utf8"), CACHE),
  );

  const git = (...args: string[]) =>
    execFileSync("git", args, { cwd: repoDir, stdio: "pipe" }).toString("utf8").trim();

  git("init", "-b", "main");
  git("config", "user.email", "e2e@sqlflow.test");
  git("config", "user.name", "OSDU Delivery E2E");
  git("add", "-A");
  git("commit", "-m", "e2e fixture: the wells delivery estate");
  const headSha = git("rev-parse", "HEAD");

  // Tests read the repo path, the source folder inside it, the exact commit to expect and the database the chain loads
  // into from this meta file (globalSetup runs in a separate process from the specs). Waiting on THIS sha makes re-runs
  // deterministic: a stale synced sha from a previous suite run never satisfies the seed assertions.
  writeFileSync(
    join(fixturesDir, "meta.json"),
    JSON.stringify({
      repoDir: repoDir.replace(/\\/g, "/"),
      sourceDir: sourceDir.replace(/\\/g, "/"),
      headSha,
      sampleDb: E2E.sampleDb,
      osduDb: E2E.osduDb,
    }, null, 2),
  );

  if (!existsSync(join(repoDir, ".git"))) {
    throw new Error(`Fixture repo was not initialized at ${repoDir}.`);
  }
}

/**
 * The source the fixture estate belongs to, and the folder it occupies in the repository. Every file of the estate
 * lives under it, so the catalog and the GUI see one project named after the source rather than a repository whose top
 * level is a pile of file kinds.
 */
export const SOURCE = "wells";

/** The cache the source's mappings resolve against: the flow file `<SOURCE>/cache/<CACHE>.yaml`, and the folder of sample records beside it. */
export const CACHE = "osdu-cache";

/** What globalSetup leaves behind for the specs, which run in a process of their own and so cannot be told directly. */
export interface FixtureMeta {
  /** The fixture git repository, which the suite registers as a repo source. */
  repoDir: string;
  /** The source's folder inside it (`<repoDir>/<SOURCE>`): where its flows, mappings, cache and data are. */
  sourceDir: string;
  /** The commit the sync has to reach before the seed assertions hold. */
  headSha: string;
  sampleDb: string;
  osduDb: string;
}

/** The delivery flows of the fixture estate, and the pre and ingestion flows that fill the tables they read. */
export const CHAIN = [
  "wells-welllog-pre",
  "wells-welllog-curves-pre",
  "wells-welllog-ing",
  "wells-welllog-curves-ing",
  "wells-welllog",
  "wells-wellbore-pre",
  "wells-wellbore-aliases-pre",
  "wells-wellbore-ing",
  "wells-wellbore-aliases-ing",
  "wells-wellbore",
  "wells-document-pre",
  "wells-document-ing",
  "wells-trajectory-pre",
  "wells-trajectory-stations-pre",
  "wells-trajectory-ing",
  "wells-trajectory-stations-ing",
  // The same estate in the shape a source takes: two interfaces, each with a ledger of its own. It is synced and read,
  // never run, so it adds a multi-interface source to the catalog without delivering anything twice.
  "wells-source",
  "osdu-download",
] as const;

/** The flows that load the ingestion tables, in the order they have to run: the pre flows land files, the ing flows key them. */
export const LOADING_FLOWS = [
  "wells-welllog-pre",
  "wells-welllog-curves-pre",
  "wells-wellbore-pre",
  "wells-wellbore-aliases-pre",
  "wells-welllog-ing",
  "wells-welllog-curves-ing",
  "wells-wellbore-ing",
  "wells-wellbore-aliases-ing",
  "wells-document-pre",
  "wells-document-ing",
  "wells-trajectory-pre",
  "wells-trajectory-stations-pre",
  "wells-trajectory-ing",
  "wells-trajectory-stations-ing",
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

/**
 * A flow whose runs ask no legal service. Every run this suite triggers stops short of OSDU: a plan never opens the
 * target, and an intake, which is how records reach the ledger without anything being sent, would otherwise ask the
 * legal service about the mapping's tags before it plans. The check is what the target is for, so turning it off here
 * is what keeps the suite off the network; a flow that declares no protocolOptions is left as it is, because no spec
 * intakes one.
 */
function withoutTheLegalCheck(yaml: string): string {
  return yaml.replace(/^( *)protocolOptions: *$/m, (line, indent: string) => `${line}
${indent}  validateLegalTags: false`);
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
