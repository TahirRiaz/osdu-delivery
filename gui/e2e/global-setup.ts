import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * Builds the e2e fixture: a real local git repository holding the sample delivery estate (the recall-welllog
 * flow, its pinned mapping, the captured schema and reference snapshots, and the generated demo drop), which
 * the suite registers as a repo source through the GUI. The control plane then syncs it exactly as it would a
 * customer's remote, so pipelines, runs, schedules, mappings and the delivery pages are all exercised against
 * real documents flowing through the product's own path. Runs use the plan operation, which renders the drop
 * against the snapshots and the ledger without touching an OSDU target.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  const repoDir = join(fixturesDir, "e2e-repo");
  const samplesDir = resolve(here, "..", "..", "samples", "recall-welllog");

  rmSync(repoDir, { recursive: true, force: true });
  mkdirSync(join(repoDir, "flows"), { recursive: true });
  for (const part of ["mappings", "snapshots", "references"]) {
    cpSync(join(samplesDir, part), join(repoDir, part), { recursive: true });
  }

  // The drop lives inside the fixture, so the flow's source location is rewritten to an absolute path with the
  // logSource parameter still in it: a run with logSource=demo reads the demo drop wherever the node runs.
  const dropRoot = join(repoDir, "drops");
  cpSync(join(samplesDir, "out"), dropRoot, { recursive: true });
  const flowYaml = readFileSync(join(samplesDir, "flows", "recall-welllog.yaml"), "utf8")
    .replace("location: samples/recall-welllog/out/{logSource}", `location: ${dropRoot.replace(/\\/g, "/")}/{logSource}`);
  writeFileSync(join(repoDir, "flows", "recall-welllog.yaml"), flowYaml);

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
