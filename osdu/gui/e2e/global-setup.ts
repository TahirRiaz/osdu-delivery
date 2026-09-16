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

  // Every flow of the estate, each without its schedule. A fire would be a real run against the sample's OSDU target
  // whenever a suite crossed its cron, and the specs expect flows that join no schedule.
  for (const flow of CHAIN) {
    writeFileSync(
      join(repoDir, "flows", `${flow}.yaml`),
      withoutSchedule(readFileSync(join(samplesDir, "flows", `${flow}.yaml`), "utf8"), flow),
    );
  }

  // The cache flow comes along without its schedule. The suite never refreshes it (that would need an OSDU target): the
  // repository sync projects what it declares, and the seed spec imports the sample references as its first version.
  mkdirSync(join(repoDir, "caches"), { recursive: true });
  writeFileSync(
    join(repoDir, "caches", "osdu-reference-cache.yaml"),
    withoutSchedule(readFileSync(join(samplesDir, "caches", "osdu-reference-cache.yaml"), "utf8"), "osdu-reference-cache"),
  );

  // A delivery flow that takes no records through the API, so the specs have a real refusal to show: the wellbore flow
  // with its source.submissions block removed. Everything else about it is the shipped document, so the refusal the GUI
  // renders is the product's own, not a fixture's invention.
  writeFileSync(join(repoDir, "flows", `${NO_SUBMISSIONS}.yaml`), withoutSubmissions(readFileSync(join(samplesDir, "flows", "recall-wellbore.yaml"), "utf8")));

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

/** The fixture's delivery flow that takes no records through the API. */
export const NO_SUBMISSIONS = "wellbore-no-submissions";

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
 * The wellbore flow renamed, with its source.submissions block removed: a delivery flow that reads the same ingestion
 * tables but takes no records through the API. Both edits are checked, so a shipped document that stops carrying either
 * fails the setup rather than leaving the suite asserting a refusal that never comes.
 */
function withoutSubmissions(yaml: string): string {
  const text = withoutSchedule(yaml, "recall-wellbore").replace(/^name: recall-wellbore$/m, `name: ${NO_SUBMISSIONS}`);
  if (!new RegExp(`^name: ${NO_SUBMISSIONS}$`, "m").test(text)) {
    throw new Error("The wellbore flow no longer declares 'name: recall-wellbore', so the fixture cannot rename it.");
  }

  const stripped = text.replace(/^ {2}submissions:\r?\n(?:[ \t]{4,}.*\r?\n)*/m, "");
  if (/^ {2}submissions:/m.test(stripped) || stripped === text) {
    throw new Error("The wellbore flow no longer declares a source.submissions block, so the fixture has no flow that refuses records.");
  }

  return stripped;
}
