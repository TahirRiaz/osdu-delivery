import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// The explorer: what the partition of OSDU holds, read live through a delivery flow's connection, and nothing
// the delivery system keeps. It browses by type, searches by name and by id, reads a record under the place it sits in,
// follows the records that mention it, compares two of its versions side by side, and offers the ids near one OSDU holds
// nothing under.
//
// Nothing here reaches an OSDU: the flows reach the e2e stand-in, which holds the platform's wellbores and the well log
// this spec gives it, in two versions, for as long as the spec runs. Nothing is delivered and nothing is written.

/** The well log this spec holds in the stand-in: written twice, so OSDU keeps two versions of it. */
const LOG = `${E2E.osdu.OSDU_DATA_PARTITION}:work-product-component--WellLog:e2e-explorer-log`;

/** The wellbore the log names, one of the wellbores the stand-in holds. */
const WELLBORE = `${E2E.osdu.OSDU_DATA_PARTITION}:master-data--Wellbore:NO-33-9-C-28-B`;

test.describe.serial("explorer", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const [name, source] of [["e2e explorer log", "STAT"], ["e2e explorer log", "STAT_COMP"]]) {
        const held = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
          data: {
            id: LOG,
            kind: "osdu:wks:work-product-component--WellLog:1.4.0",
            acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
            legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["NO"], status: "compliant" },
            data: { Name: name, LogSource: source, WellboreID: `${WELLBORE}:` },
            createUser: "e2e-stand-in",
            createTime: "2026-09-25T00:00:00.000Z",
          },
        });
        expect(held.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets the record this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("opens on a welcome, then browses the partition by type and searches it by name", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-explorer").click();
    await expect(adminPage.getByTestId("page-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-live")).toContainText(E2E.osdu.OSDU_DATA_PARTITION, { timeout: 30_000 });

    // Nothing is read from OSDU until the reader asks: the welcome, and no list of types or records behind it.
    await expect(adminPage.getByTestId("explorer-welcome")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-types")).toHaveCount(0);
    await adminPage.getByTestId("explorer-browse-types").click();
    await expect(adminPage.getByTestId("explorer-pick-type")).toBeVisible();

    // The kinds the partition holds, counted by one aggregation: its wellbores and the log this spec holds.
    const types = adminPage.getByTestId("explorer-types");
    await expect(types.getByTestId("explorer-type-all")).toContainText("8", { timeout: 60_000 });
    const wellbores = types.getByTestId("explorer-type").filter({ hasText: "Wellbore" });
    await expect(wellbores).toContainText("7");

    // A type narrows the grid to its records, and the place says where they are.
    await wellbores.getByRole("button").last().click();
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(7, { timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-place")).toContainText("Wellbore");
    await expect(adminPage.getByTestId("explorer-count")).toContainText("7 records");

    // A name finds the records holding it, within the type picked.
    await adminPage.getByTestId("explorer-search-input").fill("NO 33/9-C-28");
    await expect(adminPage.getByTestId("explorer-search-hint")).toContainText("search");
    await adminPage.getByTestId("explorer-search-input").press("Enter");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await expect(grid).toContainText("NO 33/9-C-28 A");
    await expect(grid).toContainText("NO 33/9-C-28 B");

    // A property's values group the records, and a value narrows them to it.
    await adminPage.getByTestId("explorer-group-by").click();
    await adminPage.getByTestId("explorer-group-by-field").filter({ hasText: "FacilityName" }).click({ timeout: 60_000 });
    await adminPage.getByTestId("explorer-group-by-value").filter({ hasText: "NO 33/9-C-28 B" }).click({ timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-filter")).toContainText("NO 33/9-C-28 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
  });

  test("reads a record under its place, follows what mentions it, and compares two versions of that", async ({ adminPage }) => {
    // A whole id typed opens its record straight away.
    await adminPage.goto("/delivery/explorer");
    await adminPage.getByTestId("explorer-search-input").fill(`${WELLBORE}:`);
    await expect(adminPage.getByTestId("explorer-search-hint")).toContainText("open");
    await adminPage.getByTestId("explorer-search-input").press("Enter");
    const record = adminPage.getByTestId("explorer-record");
    await expect(record.getByTestId("osdu-record-name")).toHaveText("NO 33/9-C-28 B", { timeout: 60_000 });
    await expect(record.getByTestId("explorer-record-place")).toContainText("master-data");
    await expect(record.getByTestId("explorer-record-place")).toContainText("Wellbore");

    // The records that name it: the log, opened on the trail after it.
    await record.getByTestId("osdu-outline-mentions").click();
    await expect(record.getByTestId("explorer-mentions")).toContainText("1 record", { timeout: 60_000 });
    await record.getByTestId("explorer-mention-item").filter({ hasText: "e2e explorer log" }).click();
    const linked = record.getByTestId("osdu-linked");
    await expect(linked.getByTestId("osdu-record-json")).toContainText("e2e explorer log", { timeout: 60_000 });

    // OSDU keeps two versions of the log: Compare puts them side by side, with the one value that moved.
    await linked.getByTestId("osdu-version-compare-toggle").click();
    const compare = adminPage.getByTestId("osdu-version-compare-dialog");
    await expect(compare.getByTestId("osdu-version-compare-counts")).toContainText("1 changed", { timeout: 60_000 });
    await expect(compare.getByTestId("osdu-version-differences")).toContainText("data.LogSource");
    await expect(compare.getByTestId("osdu-version-differences")).toContainText("STAT_COMP");
    await adminPage.keyboard.press("Escape");

    // Back where the record was opened from: the welcome, which now lists it among the records opened lately.
    await linked.getByTestId("osdu-linked-close").click();
    await record.getByTestId("explorer-record-back").click();
    await expect(adminPage.getByTestId("explorer-welcome-record").filter({ hasText: "NO 33/9-C-28 B" })).toBeVisible();
  });

  test("shows how to search for a value of a record, and searches with it", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?id=${encodeURIComponent(`${WELLBORE}:`)}`);
    const record = adminPage.getByTestId("explorer-record");
    await expect(record.getByTestId("osdu-record-name")).toHaveText("NO 33/9-C-28 B", { timeout: 60_000 });

    // The wellbore's name: its whole value by its keyword, its words, and whether a record holds one, each said in words.
    await record.locator('[data-testid="explorer-element-query"][data-path="data.FacilityName"]').click();
    const panel = adminPage.getByTestId("explorer-element-queries");
    const exact = panel.locator('[data-testid="explorer-element-query-item"][data-purpose="equal"]');
    await expect(exact.getByTestId("explorer-element-lucene")).toHaveText('data.FacilityName.keyword:"NO 33/9-C-28 B"', { timeout: 30_000 });
    await expect(panel.locator('[data-testid="explorer-element-query-item"][data-purpose="exists"]').getByTestId("explorer-element-lucene")).toHaveText("_exists_:data.FacilityName");
    await expect(panel.getByTestId("explorer-element-reading")).toContainText("data.FacilityName.keyword");

    // The stand-in's wellbores are of a version no template is saved of: the newest saved of the type reads them, and says so.
    await expect(panel.getByTestId("explorer-element-notes")).toContainText("is read by osdu:wks:master-data--Wellbore:1.3.0");

    // Searching with it finds the wellbore, by a Lucene query in the explorer's own search.
    await exact.getByTestId("explorer-element-search").click();
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("NO 33/9-C-28 B");
  });

  test("offers the ids near one OSDU holds nothing under", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?id=${encodeURIComponent(`${E2E.osdu.OSDU_DATA_PARTITION}:master-data--Wellbore:NO-33-9-C-28`)}`);
    await expect(adminPage.getByTestId("osdu-not-found")).toBeVisible({ timeout: 60_000 });
    const near = adminPage.getByTestId("explorer-near");
    await expect(near.getByTestId("explorer-near-item")).toHaveCount(2, { timeout: 60_000 });
    await near.getByTestId("explorer-near-item").filter({ hasText: "NO 33/9-C-28 B" }).click();
    await expect(adminPage.getByTestId("osdu-record-name")).toHaveText("NO 33/9-C-28 B", { timeout: 60_000 });
  });
});
