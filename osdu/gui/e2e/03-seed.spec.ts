import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";

// Seeds the estate THROUGH the product: saves the templates the sample mappings pin, imports the sample references as the
// cache's first version, registers the fixture git repo as a source from the Repos page in the GUI, then watches the
// control plane's managed sync pull it and the pipelines appear in the catalog. Everything after this spec runs against
// real synced data.

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

function fixtureMeta(): { repoDir: string; headSha: string } {
  const metaPath = join(import.meta.dirname, ".fixtures", "meta.json");
  return JSON.parse(readFileSync(metaPath, "utf8")) as { repoDir: string; headSha: string };
}

test.describe.serial("seed the estate via repo source sync", () => {
  // Templates live in the catalog, not the repository, so the ones the sample mappings pin are saved first: every plan
  // the later specs run renders against them.
  test("save the templates the sample mappings pin", async ({ request }) => {
    const session = await adminSession(request);
    const templates = [
      { kind: "osdu:wks:work-product-component--WellLog:1.4.0", file: "osdu_wks_work-product-component--WellLog_1.4.0.json", version: "26a3c3441882db4f" },
      { kind: "osdu:wks:master-data--Wellbore:1.3.0", file: "osdu_wks_master-data--Wellbore_1.3.0.json", version: "58d6bdbd9d066a06" },
    ];
    for (const template of templates) {
      const schema: unknown = JSON.parse(
        readFileSync(join(moduleRoot, "samples", "recall-welllog", "templates", template.file), "utf8"),
      );
      const response = await request.post(`${E2E.apiBaseUrl}/api/v1/delivery/templates`, {
        headers: { Authorization: `Bearer ${session.token}` },
        data: { kind: template.kind, schema, origin: `file ${template.file}` },
      });
      expect(response.status(), await response.text()).toBe(200);
      const saved = (await response.json()) as { template: { kind: string; version: string }; outcome: string };
      expect(saved.template.version).toBe(template.version);
      expect(["created", "unchanged"]).toContain(saved.outcome);
    }
  });

  // Cache versions live in the catalog too. A refresh would search the sample's OSDU target, so the suite imports the
  // sample reference files as the cache's first version through the OSDU Delivery CLI host, the offline path an operator
  // uses. Importing the same files again writes nothing, so a rerun against the same catalog keeps one version.
  test("import the sample references as the first version of the cache", () => {
    test.setTimeout(420_000);
    const meta = fixtureMeta();
    const output = execFileSync(
      "dotnet",
      [
        "run", "--project", join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host"), "--",
        "cache", "import", `${meta.repoDir}/caches/osdu-reference-cache.yaml`,
        "--from-dir", `${meta.repoDir}/references`,
        "--db", "${env:SQLFLOW_E2E_CACHE_DB}",
        "--json",
      ],
      { encoding: "utf8", timeout: 400_000, env: { ...process.env, SQLFLOW_E2E_CACHE_DB: E2E.catalogDb } },
    );
    expect(output).toContain("osdu-reference-cache");
  });

  test("register the fixture repo as a source and watch it sync", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();

    const meta = fixtureMeta();
    await adminPage.getByTestId("open-register-source").click();
    await adminPage.getByTestId("source-name").fill("e2e-repo");
    await adminPage.getByTestId("source-remote-url").fill(meta.repoDir);
    await adminPage.getByTestId("register-source-submit").click();

    // The row appears; force an immediate pull and wait for the FIXTURE's head commit specifically, so a sha
    // left over from a previous suite run can never satisfy this.
    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByTestId("source-sync-now").click();
    await expect(row.getByText(meta.headSha.slice(0, 10)).first()).toBeVisible({ timeout: 120_000 });
  });

  test("the synced pipeline appears in the catalog", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();
    await adminPage.getByTestId("filter-name").fill("recall-welllog");
    const row = adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" });
    await expect(row.first()).toBeVisible({ timeout: 60_000 });
  });

  test("the synced repo appears on the repos page with its pipelines", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" });
    await expect(row.first()).toBeVisible({ timeout: 30_000 });
    await row.first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible();
    // The project accordions start collapsed; a search opens the matching one and surfaces the flow row.
    await adminPage.getByTestId("repo-pipeline-search").fill("recall-welllog");
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first()).toBeVisible();
  });
});
