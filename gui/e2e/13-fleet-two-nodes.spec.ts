import { execFileSync } from "node:child_process";
import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// Nodes are not a GUI-CRUD entity: a worker registers itself by heartbeating (keyed on its machine name), so the
// only way to place additional nodes in the fleet is to write heartbeat rows into the catalog exactly as
// NodeStore.HeartbeatAsync does. This spec seeds two such nodes into the same test catalog the control plane's
// webServer is pointed at, then asserts they render as online on the Nodes page alongside the in-process worker.

const SEEDED = ["e2e-node-alpha", "e2e-node-beta"] as const;

/**
 * The server and database of the catalog the e2e control plane is pointed at (E2E.catalogDb, which
 * SQLFLOW_E2E_CATALOG_DB overrides). A fixed database name here seeded some other catalog whenever the suite ran
 * against an overridden one, and the Nodes page never showed the seeded rows.
 */
function catalogTarget(): { server: string; database: string } {
  const parts = new Map<string, string>();
  for (const part of E2E.catalogDb.split(";")) {
    const index = part.indexOf("=");
    if (index > 0) {
      parts.set(part.slice(0, index).trim().toLowerCase(), part.slice(index + 1).trim());
    }
  }

  const server = parts.get("server") ?? parts.get("data source");
  const database = parts.get("database") ?? parts.get("initial catalog");
  if (!server || !database) {
    throw new Error("E2E.catalogDb names no server or no database, so the fleet spec cannot seed its nodes into it.");
  }

  return { server, database };
}

/** Upsert two nodes with a fresh last-seen (so they fall inside the control plane's 60s online window). */
function seedTwoNodes(): void {
  const target = catalogTarget();
  const sql = `
SET NOCOUNT ON;
MERGE catalog.[Node] AS t
USING (VALUES ('${SEEDED[0]}', '3.0.0-e2e'), ('${SEEDED[1]}', '3.0.0-e2e')) AS s(Name, Version)
  ON t.Name = s.Name
WHEN MATCHED THEN
  UPDATE SET LastSeenUtc = SYSUTCDATETIME(), Version = s.Version
WHEN NOT MATCHED THEN
  INSERT (Name, FirstSeenUtc, LastSeenUtc, Version, BusyRuns)
  VALUES (s.Name, SYSUTCDATETIME(), SYSUTCDATETIME(), s.Version, 0);
SELECT Name FROM catalog.[Node] ORDER BY LastSeenUtc DESC;`;
  // Trusted connection to the same local catalog the e2e control plane uses (see playwright.config E2E.catalogDb).
  const out = execFileSync(
    "sqlcmd",
    ["-S", target.server, "-d", target.database, "-E", "-C", "-b", "-Q", sql],
    { encoding: "utf8" },
  );
  for (const name of SEEDED) {
    if (!out.includes(name)) {
      throw new Error(`Seed did not register node '${name}'. sqlcmd output:\n${out}`);
    }
  }
}

test.describe("fleet: two seeded nodes", () => {
  test("both seeded nodes register and render online on the Nodes page", async ({ adminPage }) => {
    // adminPage already waited for the dashboard, which proves bootstrap provisioning (migrations + admin) finished,
    // so the Node table exists before we seed.
    seedTwoNodes();

    await adminPage.getByTestId("nav-nodes").click();
    await expect(adminPage.getByTestId("page-nodes")).toBeVisible();

    for (const name of SEEDED) {
      const row = adminPage.getByTestId("table-row").filter({ hasText: name });
      await expect(row).toBeVisible({ timeout: 15_000 });
      await expect(row.getByTestId("online-badge")).toHaveText("online");
    }

    // Evidence: the fleet now shows at least the two seeded nodes plus the in-process worker, all online.
    const onlineBadges = adminPage.getByTestId("online-badge").filter({ hasText: "online" });
    expect(await onlineBadges.count()).toBeGreaterThanOrEqual(SEEDED.length);

    await adminPage.screenshot({ path: "e2e-artifacts/fleet-two-nodes.png", fullPage: true });
  });
});
