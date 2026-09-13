import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { expect, test } from "./helpers";

// Templates and the mapping builder (docs/delivery/mapping-templates.md). Runs after the seed (03), which saves the two
// templates the sample mappings pin and syncs the fixture repository, so both templates are pinned by a synced mapping
// and the repository's cache is in the catalog. Browsing OSDU runs on a node through a flow's OSDU connection, which the
// suite does not have, so everything here works from the saved templates and a schema file.

const WELLBORE_KIND = "osdu:wks:master-data--Wellbore:1.3.0";
const WELLBORE_VERSION = "a110ad82c3b60a1e";
const WELLBORE_FILE = "osdu_wks_master-data--Wellbore_1.3.0.json";
const WELLLOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLLOG_VERSION = "26a3c3441882db4f";

/** The row of a data table holding the element with the given test id. */
function rowWith(page: Page, table: string, testId: string): Locator {
  return page.getByTestId(table).getByTestId("table-row").filter({ has: page.getByTestId(testId) });
}

async function openTemplates(page: Page): Promise<void> {
  await page.getByTestId("nav-delivery-templates").click();
  await expect(page.getByTestId("page-delivery-templates")).toBeVisible();
}

test.describe.serial("templates and the mapping builder", () => {
  test("the saved templates say which mappings pin them, and a pinned version is not deleted", async ({ adminPage }) => {
    await openTemplates(adminPage);

    const rows = adminPage.getByTestId("templates-saved-table").getByTestId("table-row");
    const wellbore = rows.filter({ hasText: WELLBORE_KIND });
    await expect(wellbore).toHaveCount(1, { timeout: 30_000 });
    await expect(wellbore).toContainText(WELLBORE_VERSION);
    await expect(wellbore).toContainText("1 mapping");
    await expect(rows.filter({ hasText: WELLLOG_KIND })).toContainText(WELLLOG_VERSION);

    await wellbore.click();
    const sheet = adminPage.getByTestId("templates-saved-sheet");
    await expect(sheet.getByTestId("templates-view-kind")).toHaveText(WELLBORE_KIND, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION);
    await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
    await expect(sheet.getByTestId("templates-view-saved")).toContainText("Pinned by 1 synced mapping");

    // The variables a mapping fills are listed; what OSDU Delivery and OSDU write themselves shows only on request.
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toHaveCount(0);
    await sheet.getByTestId("templates-view-show-written").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toBeVisible();

    await sheet.getByTestId("templates-view-tab-schema").click();
    await expect(sheet.getByTestId("templates-view-schema-json")).toBeVisible({ timeout: 15_000 });

    // The synced Wellbore mapping pins this version, so the delete is refused and names the mapping that holds it.
    await sheet.getByTestId("templates-delete").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByText(/pinned by mapping\(s\) Wellbore@1\.0\.0/).first()).toBeVisible({ timeout: 15_000 });
    await expect(rows.filter({ hasText: WELLBORE_KIND })).toHaveCount(1);
  });

  test("a schema file is laid out as a template, and saving a version already saved changes nothing", async ({ adminPage }) => {
    await openTemplates(adminPage);
    await adminPage.getByTestId("templates-tab-import").click();

    const schema = readFileSync(join(import.meta.dirname, "..", "..", "samples", "recall-welllog", "templates", WELLBORE_FILE), "utf8");
    await adminPage.getByTestId("templates-import-kind").fill(WELLBORE_KIND);
    await adminPage.getByTestId("templates-import-json").fill(schema);
    await adminPage.getByTestId("templates-import-preview").click();

    // A version is the schema's content, so the same file lays out as the version the catalog already holds.
    const sheet = adminPage.getByTestId("templates-import-sheet");
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
    await sheet.getByTestId("templates-import-save").click();
    await expect(adminPage.getByText(/was already saved, so nothing changed/).first()).toBeVisible({ timeout: 15_000 });
  });

  test("the builder starts a mapping from a saved template, with what the cache holds prefilled", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-mapping-builder").click();
    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-empty")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-repo").click();
    await adminPage.getByRole("option").filter({ hasText: "e2e-repo" }).first().click();
    await expect(adminPage.getByTestId("mapping-builder-repo-cache")).toContainText(/holds \d+ types?/, { timeout: 15_000 });

    await adminPage.getByTestId("mapping-builder-template").click();
    await adminPage.getByRole("option").filter({ hasText: WELLLOG_KIND }).first().click();
    // The name starts as the template's entity; this draft takes its own, so it is never taken for the synced mapping.
    await expect(adminPage.getByTestId("mapping-builder-name")).toHaveValue("WellLog");
    await adminPage.getByTestId("mapping-builder-name").fill("WellLogDraft");
    await adminPage.getByTestId("mapping-builder-system").fill("recall");
    await adminPage.getByTestId("mapping-builder-start").click();

    // A well log points at its wellbore, which the cache holds, so that entry starts filled from the cache.
    const wellbore = rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.WellboreID");
    await expect(wellbore).toBeVisible({ timeout: 15_000 });
    await expect(wellbore.getByTestId("mapping-builder-prefilled")).toBeVisible();

    // The check writes the YAML and refuses the draft as it stands: it has no key, and the cache entry finds by no column.
    await expect(adminPage.getByTestId("mapping-builder-invalid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-issue").first()).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-yaml")).toContainText(WELLLOG_VERSION);
    await expect(adminPage.getByTestId("mapping-builder-propose-blocked")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-key-input").fill("log_id");
    await adminPage.getByTestId("mapping-builder-key-add").click();
    await expect(adminPage.getByTestId("mapping-builder-key-chip-log_id")).toBeVisible();
  });

  test("a synced mapping opens in the builder and passes the check against the repository's cache", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-documents").click();
    await expect(adminPage.getByTestId("page-delivery-documents")).toBeVisible();
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: "WellLog@1.4.0" }).first();
    await expect(row).toContainText(WELLLOG_VERSION, { timeout: 30_000 });
    await row.click();

    const detail = adminPage.getByTestId("delivery-mapping-detail");
    await expect(detail.getByTestId("delivery-mapping-template")).toContainText(WELLLOG_VERSION, { timeout: 15_000 });
    await detail.getByTestId("delivery-mapping-open-builder").click();

    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-opened-from")).toContainText("mappings/WellLog@1.4.0.yaml", { timeout: 30_000 });
    // The sample mapping renders its fixtures against the synced cache exactly, so it loads, passes and can be proposed.
    await expect(adminPage.getByTestId("mapping-builder-valid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-propose")).toBeEnabled();
  });
});
