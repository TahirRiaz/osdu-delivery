import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * Builds the e2e fixture: a real local git repository holding the sample delivery estate (the recall-welllog flow, its
 * pinned mappings, the cache flow that declares what the mappings read, and the captured reference files), which the
 * suite registers as a repo source through the GUI. The control plane then syncs it exactly as it would a customer's
 * remote, so pipelines, runs, schedules, mappings and the delivery pages are all exercised against real documents flowing
 * through the product's own path. The templates those mappings pin, and the cache versions, live in the catalog, not the
 * repository, so the seed spec saves the templates through the API and imports the reference files as the cache's first
 * version through the CLI host. Runs use the plan operation, which renders records against the templates, the cache and
 * the ledger without touching an OSDU target.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  const repoDir = join(fixturesDir, "e2e-repo");
  const samplesDir = resolve(here, "..", "..", "samples", "recall-welllog");

  rmSync(repoDir, { recursive: true, force: true });
  mkdirSync(join(repoDir, "flows"), { recursive: true });
  for (const part of ["mappings", "references"]) {
    cpSync(join(samplesDir, part), join(repoDir, part), { recursive: true });
  }

  writeFileSync(
    join(repoDir, "flows", "recall-welllog.yaml"),
    withoutSchedule(readFileSync(join(samplesDir, "flows", "recall-welllog.yaml"), "utf8"), "recall-welllog"),
  );

  // The cache flow comes along without its schedule. The suite never refreshes it (that would need an OSDU target): the
  // repository sync projects what it declares, and the seed spec imports the sample references as its first version.
  mkdirSync(join(repoDir, "caches"), { recursive: true });
  writeFileSync(
    join(repoDir, "caches", "osdu-reference-cache.yaml"),
    withoutSchedule(readFileSync(join(samplesDir, "caches", "osdu-reference-cache.yaml"), "utf8"), "osdu-reference-cache"),
  );

  // The metadata sync flow comes along without its schedule too. It is a retrieval the suite never runs.
  writeFileSync(
    join(repoDir, "flows", "osdu-cache-sync.yaml"),
    withoutSchedule(readFileSync(join(samplesDir, "flows", "osdu-cache-sync.yaml"), "utf8"), "osdu-cache-sync"),
  );

  // A flow that takes records in the request: wellbore master data through the storage service, which streams no payload
  // files. Manual submission is wellbore data, so this is what the Submit records spec previews a record against. The
  // records it is sent are written under the fixture for the flow to read, so it needs no source data of its own.
  const submittedRoot = join(repoDir, "submitted");
  mkdirSync(submittedRoot, { recursive: true });
  writeFileSync(
    join(repoDir, "flows", "wellbore-records.yaml"),
    [
      "flowType: delivery",
      "name: wellbore-records",
      "parameters:",
      "  site:",
      "    required: true",
      "    description: The site the wellbores belong to.",
      "source:",
      `  location: ${submittedRoot.replace(/\\/g, "/")}/{site}`,
      "  lastModified: update_date",
      "  manualSubmission: true",
      "render:",
      "  mapping: Wellbore@1.0.0",
      "  parameters:",
      "    dataPartition: opendes",
      "change:",
      "  detect: renderedHash",
      "  onUnchanged: skip",
      "target:",
      "  endpoint: https://osdu.example.test",
      "  headers:",
      "    data-partition-id: opendes",
      "  protocol: osduRecord",
      "reliability:",
      "  concurrency: 1",
      "",
    ].join("\n"),
  );
  // git keeps no empty folders; the marker keeps the location the flow declares present in the synced repository.
  writeFileSync(join(submittedRoot, ".gitkeep"), "");

  const git = (...args: string[]) =>
    execFileSync("git", args, { cwd: repoDir, stdio: "pipe" }).toString("utf8").trim();

  git("init", "-b", "main");
  git("config", "user.email", "e2e@sqlflow.test");
  git("config", "user.name", "OSDU Delivery E2E");
  git("add", "-A");
  git("commit", "-m", "e2e fixture: the recall-welllog delivery estate");
  const headSha = git("rev-parse", "HEAD");

  // Tests read the repo path and the exact commit to expect from this meta file (globalSetup runs in a
  // separate process from the specs). Waiting on THIS sha makes re-runs deterministic: a stale synced sha from
  // a previous suite run never satisfies the seed assertions.
  writeFileSync(
    join(fixturesDir, "meta.json"),
    JSON.stringify({ repoDir: repoDir.replace(/\\/g, "/"), headSha }, null, 2),
  );

  if (!existsSync(join(repoDir, ".git"))) {
    throw new Error(`Fixture repo was not initialized at ${repoDir}.`);
  }
}

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
