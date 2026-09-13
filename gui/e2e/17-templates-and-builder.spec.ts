import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { expect, test } from "./helpers";

// Templates and the mapping builder (docs/delivery/mapping-templates.md). Runs after the seed (03), which saves the two
// templates the sample mappings pin and syncs the fixture repository, so both templates are pinned by a synced mapping
// and the repository's cache is in the catalog. Browsing OSDU reads the Open Group's public data definitions repository
// through the control plane, which the suite cannot reach, so the browse test stands in for those three answers and lets
// everything after them (the preview, the save) run against the real control plane.

const WELLBORE_KIND = "osdu:wks:master-data--Wellbore:1.3.0";
const WELLBORE_VERSION = "a110ad82c3b60a1e";
const WELLBORE_FILE = "osdu_wks_master-data--Wellbore_1.3.0.json";

const DATA_DEFINITIONS = "https://community.opengroup.org/osdu/data/data-definitions";
const RELEASE = {
  name: "v0.30.0",
  commit: "99f8fc88d8ad838b5738ac5ad92ac643538b5766",
  publishedUtc: "2026-07-17T06:55:57Z",
  webUrl: `${DATA_DEFINITIONS}/-/tree/v0.30.0/Generated`,
};
const WELLBORE_URL = `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json`;

/** A record schema as the control plane lists it for a release. */
function osduSchema(kind: string, entityType: string, version: string, status: string, path: string) {
  return { kind, entityType, version, status, path, webUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/${path}` };
}
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

    // Each writer is marked apart from what a mapping fills: its own label and glyph, and no marker on a mapping's variable.
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime")).toHaveText("OSDU sets it");
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime").locator("svg")).toHaveCount(1);
    await expect(sheet.getByTestId("templates-view-role-osdu.id")).toHaveText("OSDU Delivery writes it");
    await expect(sheet.getByTestId("templates-view-role-osdu.data.FacilityName")).toHaveCount(0);

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

  test("the OSDU data definitions are browsed by release, and a kind opens as the template it saves as", async ({ adminPage }) => {
    const releases = /\/api\/v1\/delivery\/templates\/osdu\/releases$/;
    const schemas = /\/api\/v1\/delivery\/templates\/osdu\/schemas(\?|$)/;
    const schema = /\/api\/v1\/delivery\/templates\/osdu\/schema(\?|$)/;
    const wellboreSchema: unknown = JSON.parse(
      readFileSync(join(import.meta.dirname, "..", "..", "samples", "recall-welllog", "templates", WELLBORE_FILE), "utf8"),
    );
    const asked: string[] = [];
    await adminPage.route(releases, (route) => route.fulfill({ json: [RELEASE, { ...RELEASE, name: "v0.29.1", commit: "0".repeat(40) }] }));
    await adminPage.route(schemas, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          release: RELEASE,
          schemas: [
            osduSchema("osdu:wks:master-data--Well:1.2.0", "master-data--Well", "1.2.0", "DEVELOPMENT", "master-data/Well.1.2.0.json"),
            osduSchema(WELLBORE_KIND, "master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"),
            osduSchema("osdu:wks:master-data--Wellbore:1.0.0", "master-data--Wellbore", "1.0.0", "PUBLISHED", "master-data/Wellbore.1.0.0.json"),
          ],
        },
      });
    });
    await adminPage.route(schema, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          kind: WELLBORE_KIND,
          version: WELLBORE_VERSION,
          release: RELEASE,
          path: "master-data/Wellbore.1.3.0.json",
          webUrl: WELLBORE_URL,
          origin: "OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/master-data/Wellbore.1.3.0.json",
          schema: wellboreSchema,
        },
      });
    });

    try {
      await openTemplates(adminPage);
      await adminPage.getByTestId("templates-tab-browse").click();

      // The newest release is browsed straight away: there is no repository or connection to pick first.
      await expect(adminPage.getByTestId("templates-browse-release")).toContainText("v0.30.0", { timeout: 15_000 });
      await expect(adminPage.getByTestId("templates-browse-summary")).toContainText("2 record types");
      await expect(adminPage.getByTestId("templates-browse-repository-link")).toHaveAttribute("href", RELEASE.webUrl);
      const rows = adminPage.getByTestId("templates-browse-results").getByTestId("table-row");
      await expect(rows).toHaveCount(2);
      await expect(adminPage.getByTestId("templates-browse-status-osdu:wks:master-data--Well:1.2.0")).toHaveText("In development");

      // One search box finds a kind by any part of it; each entity type shows its newest version unless every version is asked for.
      await adminPage.getByTestId("templates-browse-search").fill("wellbore");
      await expect(rows).toHaveCount(1);
      await expect(rows.first()).toContainText("2 versions");
      await expect(adminPage.getByTestId(`templates-browse-link-${WELLBORE_KIND}`)).toHaveAttribute("href", WELLBORE_URL);
      await adminPage.getByTestId("templates-browse-all-versions").click();
      await expect(rows).toHaveCount(2);

      // A kind opens laid out as the template it saves as; the seed saved this one, so saving it changes nothing.
      await adminPage.getByTestId(`templates-browse-view-${WELLBORE_KIND}`).click();
      const sheet = adminPage.getByTestId("templates-browse-sheet");
      await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
      await expect(sheet.getByTestId("templates-browse-source-link")).toHaveAttribute("href", WELLBORE_URL);
      await sheet.getByTestId("templates-browse-save").click();
      await expect(adminPage.getByText(/was already saved, so nothing changed/).first()).toBeVisible({ timeout: 15_000 });

      expect(asked.length).toBeGreaterThan(1);
      expect(asked.every((url) => new URL(url).searchParams.get("release") === "v0.30.0")).toBe(true);
    } finally {
      await adminPage.unroute(releases);
      await adminPage.unroute(schemas);
      await adminPage.unroute(schema);
    }
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
