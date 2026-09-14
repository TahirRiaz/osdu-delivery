import { readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "./helpers";

// The features added on top of the read-only operate GUI: the preview-first scan (discover a repo's flows without
// importing), the project grouping on a repo (root folder = a source's pipelines), and schedule catch-up. These run
// after the seed (03), so the fixture repo is already synced as "e2e-repo" with its recall-welllog pipeline.

function fixtureMeta(): { repoDir: string; headSha: string } {
  const metaPath = join(import.meta.dirname, ".fixtures", "meta.json");
  return JSON.parse(readFileSync(metaPath, "utf8")) as { repoDir: string; headSha: string };
}

test.describe.serial("new features", () => {
  test("discover previews a repo's flows without importing", async ({ adminPage }) => {
    const meta = fixtureMeta();
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("open-register-source").click();

    // Fill the remote and discover: a read-only clone + parse, nothing written to the catalog.
    await adminPage.getByTestId("source-remote-url").fill(meta.repoDir);
    await adminPage.getByTestId("source-branch").fill("main");
    await adminPage.getByTestId("discover-flows").click();

    // The fixture's single flow appears with its path; parse succeeded (no error chip), and preview opens.
    const flows = adminPage.getByTestId("discovered-flows");
    await expect(flows).toBeVisible({ timeout: 60_000 });
    const row = adminPage.getByTestId("discovered-flow-row").filter({ hasText: "flows/recall-welllog.yaml" });
    await expect(row.first()).toBeVisible();
    await expect(row.first().getByTestId("discovered-flow-error")).toHaveCount(0);
    await row.first().getByTestId("discovered-flow-preview").click();

    // Cancel: discover is preview-only, so no second source is registered.
    await adminPage.getByTestId("register-source-cancel").click();
    await expect(adminPage.getByTestId("register-source-dialog")).toHaveCount(0);
  });

  test("the synced repo shows its pipelines grouped by project", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible({ timeout: 15_000 });

    // Pipelines are grouped into project (root-folder) sections; the fixture flow sits under the flows folder, so it is
    // under one project accordion. The accordions start collapsed, so opening the "flows" one reveals its flows.
    await expect(adminPage.getByTestId("repo-projects")).toBeVisible({ timeout: 15_000 });
    const rootFolder = adminPage.getByTestId("repo-project").filter({ hasText: "flows" });
    await expect(rootFolder).toBeVisible();
    await rootFolder.getByText("flows").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first()).toBeVisible();
  });

  test("a YAML file in the repo opens a preview of its document and its schedules", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first().click();
    await expect(adminPage.getByTestId("repo-projects")).toBeVisible({ timeout: 15_000 });

    // Every file of a folder is listed, the flow's own document included, and a YAML file opens.
    const flowsFolder = adminPage.getByTestId("repo-project").filter({ hasText: "flows" });
    await flowsFolder.getByText("flows").click();
    const flowFile = flowsFolder.getByTestId("project-file-row").filter({ hasText: "flows/recall-welllog.yaml" });
    await expect(flowFile).toHaveAttribute("data-previewable", "true");
    await flowFile.click();

    const sheet = adminPage.getByTestId("repo-file-sheet");
    await expect(sheet).toBeVisible();
    await expect(sheet.getByTestId("repo-file-yaml").getByText("recall-welllog").first()).toBeVisible({ timeout: 30_000 });
    await expect(sheet.getByTestId("repo-file-flow-link")).toBeVisible();

    // A flow file carries the schedules that run the flow; the fixture flows join none.
    await sheet.getByTestId("repo-file-tab-schedules").click();
    await expect(sheet.getByTestId("paged-table")).toBeVisible();
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toHaveCount(0);

    // Anything that is not YAML is listed only.
    const referencesFolder = adminPage.getByTestId("repo-project").filter({ hasText: "references" });
    await referencesFolder.getByText("references").click();
    await expect(referencesFolder.getByTestId("project-file-row").filter({ hasText: "references/README.md" }))
      .toHaveAttribute("data-previewable", "false");
  });

  test("a catch-up schedule says it backfills missed occurrences", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    await expect(adminPage.getByTestId("page-schedules")).toBeVisible();

    await adminPage.getByTestId("open-create-schedule").click();
    await adminPage.getByTestId("schedule-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("schedule-flow").fill("recall");
    await adminPage.getByRole("option", { name: "recall-welllog" }).click();

    // A far-out interval so it never actually fires during the suite, with catch-up on.
    await adminPage.getByTestId("schedule-trigger-interval").click();
    await adminPage.getByTestId("schedule-interval").fill("21600");
    await adminPage.getByTestId("schedule-catchup").click();
    await adminPage.getByTestId("create-schedule-submit").click();

    const row = adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    // The list keeps to one line per schedule, so catch-up is told where the trigger is explained: its tooltip.
    await row.getByText("Every 6 hours").hover();
    await expect(adminPage.getByRole("tooltip")).toContainText("Missed occurrences are backfilled", { timeout: 10_000 });
    await adminPage.keyboard.press("Escape");

    // Clean up so the suite leaves no schedule behind.
    await row.getByTestId("schedule-delete").click();
    await expect(adminPage.getByTestId("confirm-dialog")).toBeVisible();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }))
      .toHaveCount(0, { timeout: 15_000 });
  });
});
