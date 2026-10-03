import type { Locator, Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// The dimension builder: an extension of the explorer that writes the YAML of a dimension from what a person picks in
// the records OSDU holds, reading them live through a delivery flow's connection as the explorer does. A key is picked in
// one of the kind's records, the record it names opens beside it, a value and attributes are read there or further on,
// and every pick reaches several values asks which is meant. The YAML is checked as a flow's own dimension is, and an
// example row is made by the build's own labelling, cleaning and counting.
//
// Nothing here reaches an OSDU: the flows reach the e2e stand-in, which holds, for as long as the spec runs, five well
// logs, the two wellbores four of them name (the fifth names one OSDU holds nothing under), and the political entities
// those wellbores name. Nothing is delivered and nothing is written: the builder makes no flow and no dimension.

const PARTITION = E2E.osdu.OSDU_DATA_PARTITION;
const WELLLOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLBORE_1 = `${PARTITION}:master-data--Wellbore:e2e-builder-1`;
const WELLBORE_2 = `${PARTITION}:master-data--Wellbore:e2e-builder-2`;
/** The wellbore the fifth log names, which the stand-in does not hold. */
const WELLBORE_GONE = `${PARTITION}:master-data--Wellbore:e2e-builder-gone`;
const NORWAY = `${PARTITION}:master-data--GeoPoliticalEntity:e2e-builder-norway`;
const ROGALAND = `${PARTITION}:master-data--GeoPoliticalEntity:e2e-builder-rogaland`;
const COUNTRY = `${PARTITION}:reference-data--GeoPoliticalEntityType:Country:`;
const COUNTY = `${PARTITION}:reference-data--GeoPoliticalEntityType:County:`;
/** The name each test gives the dimension, which no flow of the e2e estate declares. */
const NAME = "E2eBuilderWellbore";
/** The issues the builder lists against the YAML, by how much they matter. */
const ERRORS = '[data-testid="builder-issue"][data-severity="error"]';
const WARNINGS = '[data-testid="builder-issue"][data-severity="warning"]';

/** A record as the stand-in holds it, readable by the e2e flows' ACL and legal tag. */
function record(id: string, kind: string, data: Record<string, unknown>) {
  return {
    id,
    kind,
    acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
    legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["NO"], status: "compliant" },
    data,
    createUser: "e2e-stand-in",
    createTime: "2026-10-03T00:00:00.000Z",
  };
}

const RECORDS = [
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-1`, WELLLOG_KIND, { Name: "e2e builder log 1", WellboreID: `${WELLBORE_1}:`, Source: "STAT" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-2`, WELLLOG_KIND, { Name: "e2e builder log 2", WellboreID: `${WELLBORE_1}:`, Source: "STAT" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-3`, WELLLOG_KIND, { Name: "e2e builder log 3", WellboreID: `${WELLBORE_1}:`, Source: "COMP" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-4`, WELLLOG_KIND, { Name: "e2e builder log 4", WellboreID: `${WELLBORE_2}:`, Source: "STAT" }),
  record(`${PARTITION}:work-product-component--WellLog:e2e-builder-log-5`, WELLLOG_KIND, { Name: "e2e builder log 5", WellboreID: `${WELLBORE_GONE}:`, Source: "STAT" }),
  record(WELLBORE_1, "osdu:wks:master-data--Wellbore:1.3.0", {
    FacilityName: "E2E Builder 1",
    GeoContexts: [
      { GeoPoliticalEntityID: `${NORWAY}:`, GeoTypeID: COUNTRY },
      { GeoPoliticalEntityID: `${ROGALAND}:`, GeoTypeID: COUNTY },
    ],
  }),
  record(WELLBORE_2, "osdu:wks:master-data--Wellbore:1.3.0", {
    FacilityName: "E2E Builder 2",
    GeoContexts: [{ GeoPoliticalEntityID: `${NORWAY}:`, GeoTypeID: COUNTRY }],
  }),
  record(NORWAY, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", { GeoPoliticalEntityName: "Norway", GeoPoliticalEntityTypeID: COUNTRY }),
  record(ROGALAND, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", { GeoPoliticalEntityName: "Rogaland", GeoPoliticalEntityTypeID: COUNTY }),
];

/** The row at `path` (the `nth` of them: a list's items share its path) of the record tree `tree` within `scope`. */
function treeRow(scope: Locator, tree: string, path: string, nth = 0): Locator {
  return scope.locator(`[data-testid="${tree}-row"][data-path="${path}"]`).nth(nth);
}

/** Picks `action` from the menu of a row of a record tree. */
async function pick(page: Page, scope: Locator, tree: string, path: string, action: string, nth = 0) {
  const row = treeRow(scope, tree, path, nth);
  await row.scrollIntoViewIfNeeded();
  await row.hover();
  await row.getByTestId(`${tree}-menu`).click();
  await page.getByTestId(`${tree}-pick-${action}`).click();
}

/** Opens a row of a record tree that holds a list or an object. */
async function open(scope: Locator, tree: string, path: string, nth = 0) {
  const row = treeRow(scope, tree, path, nth);
  await row.scrollIntoViewIfNeeded();
  await row.locator("button").first().click();
}

/** Opens the builder on the well logs, picked from the kinds of the partition. */
async function openOnWellLogs(page: Page) {
  await page.goto("/delivery/explorer");
  await expect(page.getByTestId("explorer-live")).toContainText(PARTITION, { timeout: 30_000 });
  await page.getByTestId("explorer-build-dimension").click();
  await expect(page.getByTestId("page-dimension-builder")).toBeVisible();
  const picker = page.getByTestId("builder-kind-picker");
  await picker.getByTestId("builder-kind-find").fill("WellLog");
  const logs = picker.getByTestId("builder-kind-item").filter({ hasText: WELLLOG_KIND });
  await expect(logs).toContainText("5", { timeout: 60_000 });
  await logs.click();
  await expect(page.getByTestId("builder-source-tree")).toBeVisible({ timeout: 60_000 });
}

/** The key, its wellbore's name as the value, and the dimension named: the start of every build below. */
async function pickKeyAndValue(page: Page) {
  await pick(page, page.getByTestId("builder-source"), "builder-source-tree", "data.WellboreID", "key");
  const wellbore = page.getByTestId("builder-trail").first();
  await expect(treeRow(wellbore, "builder-trail-tree", "data.FacilityName")).toBeVisible({ timeout: 60_000 });
  await pick(page, wellbore, "builder-trail-tree", "data.FacilityName", "value");
  await page.getByTestId("builder-name").fill(NAME);
  return wellbore;
}

test.describe.serial("dimension builder", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const held of RECORDS) {
        const answer = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, { data: held });
        expect(answer.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets the records this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("builds a dimension from picks: a key, its record's name, a country reached through a filter, and a collected value", async ({ adminPage }) => {
    // The explorer offers the builder beside its browsing, and the builder opens on the kinds the partition holds.
    await openOnWellLogs(adminPage);
    await expect(adminPage.getByTestId("builder-columns-empty")).toBeVisible();
    await expect(adminPage.getByTestId("builder-source")).toContainText("5 records");
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("e2e builder log 1");

    // The key: the wellbore each log names. The builder names the dimension after the type of record the key names, opens
    // that record beside the log, and takes the commonest key as its example: the wellbore three logs name.
    await pick(adminPage, adminPage.getByTestId("builder-source"), "builder-source-tree", "data.WellboreID", "key");
    await expect(adminPage.getByTestId("builder-name")).toHaveValue("Wellbore");
    await expect(adminPage.getByTestId("builder-source")).toContainText("3 records hold the example key", { timeout: 60_000 });
    const wellbore = adminPage.getByTestId("builder-trail").first();
    await expect(wellbore).toContainText("Wellbore", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-column-key-example")).toContainText("e2e-builder-1", { timeout: 60_000 });

    // The value: the wellbore's name, read from the record the key names.
    await pick(adminPage, wellbore, "builder-trail-tree", "data.FacilityName", "value");
    await adminPage.getByTestId("builder-name").fill(NAME);
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("E2E Builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 1");

    // Following the political entities the wellbore names reaches two items, so the builder asks which to follow.
    await open(wellbore, "builder-trail-tree", "data.GeoContexts");
    await open(wellbore, "builder-trail-tree", "data.GeoContexts", 1);
    await pick(adminPage, wellbore, "builder-trail-tree", "data.GeoContexts.GeoPoliticalEntityID", "follow");
    const which = adminPage.getByTestId("builder-filter-dialog");
    await expect(which).toContainText("Which items to follow?");
    await expect(which.getByTestId("builder-filter-objects").locator("li")).toHaveCount(2);
    await which.getByTestId("builder-filter-none").click();
    const entities = adminPage.getByTestId("builder-trail").nth(1);
    await expect(entities.getByTestId("builder-trail-records")).toContainText("Norway", { timeout: 60_000 });
    await expect(entities.getByTestId("builder-trail-records")).toContainText("Rogaland");

    // The country's name: two records give a name, so the builder asks which, and suggests the filter keeping the country.
    await entities.getByTestId("builder-trail-records").getByText("Norway").click();
    await pick(adminPage, entities, "builder-trail-tree", "data.GeoPoliticalEntityName", "attribute");
    await expect(which).toContainText("Which record gives");
    await expect(which.getByTestId("builder-filter-value")).toHaveValue("GeoPoliticalEntityType:Country:");
    await expect(which.getByTestId("builder-filter-result")).toContainText("Norway");
    await which.getByTestId("builder-filter-apply").click();
    await expect(which).toHaveCount(0);
    const country = adminPage.getByTestId("builder-column-attribute").nth(0);
    await expect(country.getByTestId("builder-column-attribute-name")).toHaveValue("Country");
    await expect(country.getByTestId("builder-column-attribute-example")).toHaveText("Norway", { timeout: 60_000 });

    // The source of every log of the wellbore, collected: each value with the records holding it, the commonest first.
    await pick(adminPage, adminPage.getByTestId("builder-source"), "builder-source-tree", "data.Source", "collect");
    const source = adminPage.getByTestId("builder-column-attribute").nth(1);
    await expect(source.getByTestId("builder-column-attribute-name")).toHaveValue("Source");
    await expect(source.getByTestId("builder-column-attribute-example")).toHaveText("STAT (2), COMP (1)", { timeout: 60_000 });

    // The YAML a dimension flow lists, checked as the flow's own would be: it loads, and the one thing it cannot check (no
    // template of the political entities is saved) is said, with where to save one.
    const expression = adminPage.getByTestId("builder-expression");
    await expect(adminPage.getByTestId("builder-status")).toHaveAttribute("data-valid", "true", { timeout: 60_000 });
    for (const line of [
      `- name: ${NAME}`,
      `kind: ${WELLLOG_KIND}`,
      "path: data.WellboreID",
      "label: data.FacilityName",
      "- data.GeoContexts.GeoPoliticalEntityID",
      "- 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName'",
      "Source: { collect: data.Source }",
    ]) {
      await expect(expression).toContainText(line);
    }

    const warning = adminPage.locator(WARNINGS).filter({ hasText: "GeoPoliticalEntity" }).first();
    await expect(warning.getByTestId("builder-issue-templates")).toBeVisible();
    await expect(adminPage.locator(ERRORS)).toHaveCount(0);
    await expect(adminPage.getByTestId("builder-table-name")).toHaveText(`osdu.dim_${NAME}`);

    // How a row is built, drawn as a flow's own dimension is and filled with the example key's row.
    await adminPage.getByTestId("builder-show-row").click();
    const row = adminPage.getByTestId("builder-row-dialog");
    await expect(row.getByTestId("builder-row-preview")).toBeVisible({ timeout: 30_000 });
    await expect(row).toContainText("E2E Builder 1");
    await adminPage.keyboard.press("Escape");
    await expect(row).toHaveCount(0);

    // The build lives in the address: a reload brings back every pick, and the YAML with them.
    await adminPage.reload();
    await expect(adminPage.getByTestId("builder-name")).toHaveValue(NAME, { timeout: 30_000 });
    await expect(expression).toContainText("Source: { collect: data.Source }", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-trail")).toHaveCount(2, { timeout: 60_000 });
  });

  test("steps through the example keys, valuing one that names no record as the dimension says", async ({ adminPage }) => {
    await openOnWellLogs(adminPage);
    await pickKeyAndValue(adminPage);
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-example")).toContainText("1/3");

    // The next commonest keys, one log each: the second wellbore, then the one OSDU holds nothing under.
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example-label")).toHaveText("E2E Builder 2", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-source")).toContainText("1 record holds the example key", { timeout: 60_000 });
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example")).toContainText("3/3");
    await expect(adminPage.getByTestId("builder-lane-no-record")).toContainText(`The search holds no record ${WELLBORE_GONE}.`, { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-trail")).toHaveCount(0);

    // Read nothing, the key is valued by the code its id ends with; given a value for that, the key takes it.
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("e2e-builder-gone", { timeout: 60_000 });
    await adminPage.getByTestId("builder-unlabelled").fill("Unknown wellbore");
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("Unknown wellbore", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("unlabelled: Unknown wellbore", { timeout: 60_000 });

    // Back round to the first key, cleaned: each value lower-cased, as a build would hold it.
    await adminPage.getByTestId("builder-example-next").click();
    await expect(adminPage.getByTestId("builder-example")).toContainText("1/3");
    await adminPage.getByTestId("builder-clean-lower").click();
    await expect(adminPage.getByTestId("builder-clean-lower")).toHaveAttribute("aria-pressed", "true");
    await expect(adminPage.getByTestId("builder-column-value-example")).toHaveText("e2e builder 1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("- lower", { timeout: 60_000 });
  });

  test("points a mistake at the part it is about, narrows the keys by a query, and starts over", async ({ adminPage }) => {
    await openOnWellLogs(adminPage);
    await pickKeyAndValue(adminPage);
    const status = adminPage.getByTestId("builder-status");
    await expect(status).toHaveAttribute("data-valid", "true", { timeout: 60_000 });

    // A column name the table already holds is refused at the column, and the YAML is not ready until it is put right.
    await adminPage.getByTestId("builder-column-key-name").fill("id");
    await expect(status).toHaveAttribute("data-valid", "false", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-column-key-problem")).toBeVisible();
    await expect(adminPage.locator(ERRORS).filter({ hasText: "id" }).first()).toHaveAttribute("data-target", "columns.key");
    await adminPage.getByTestId("builder-column-key-name").fill("WellboreKey");
    await expect(status).toHaveAttribute("data-valid", "true", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText("key: WellboreKey");

    // A query narrows the records the keys are read from: only the log whose source is COMP, so one key holds.
    await adminPage.getByTestId("builder-more").click();
    await adminPage.getByTestId("builder-query").fill('data.Source:"COMP"');
    await expect(adminPage.getByTestId("builder-example")).toContainText("1/1", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-source")).toContainText("1 record holds the example key", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-expression")).toContainText(`query: 'data.Source:"COMP"'`, { timeout: 60_000 });

    // Starting over clears every pick and the kind, back to the kinds of the partition.
    await adminPage.getByTestId("builder-restart").click();
    await expect(adminPage.getByTestId("builder-kind-picker")).toBeVisible();
  });

  test("starts from the type the explorer shows, and goes back to it", async ({ adminPage }) => {
    await adminPage.goto("/delivery/explorer");
    await expect(adminPage.getByTestId("explorer-live")).toContainText(PARTITION, { timeout: 30_000 });
    await adminPage.getByTestId("explorer-browse-types").click();
    const logs = adminPage.getByTestId("explorer-types").getByTestId("explorer-type").filter({ hasText: "WellLog" });
    await expect(logs).toContainText("5", { timeout: 60_000 });
    await logs.getByRole("button").last().click();
    await expect(adminPage.getByTestId("explorer-place")).toContainText("WellLog", { timeout: 60_000 });

    // A type reads every version of its kind: the builder starts on it, with the records of the type.
    await adminPage.getByTestId("explorer-build-dimension").click();
    await expect(adminPage.getByTestId("builder-kind")).toContainText("WellLog");
    await expect(adminPage.getByTestId("builder-source")).toContainText("5 records", { timeout: 60_000 });
    await pickKeyAndValue(adminPage);
    await expect(adminPage.getByTestId("builder-expression")).toContainText("kind: '*:*:work-product-component--WellLog:*'", { timeout: 60_000 });
    await expect(adminPage.getByTestId("builder-status")).toHaveAttribute("data-valid", "true", { timeout: 60_000 });

    // Back is the explorer as it was left: the type, still picked.
    await adminPage.getByTestId("builder-back").click();
    await expect(adminPage.getByTestId("page-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("explorer-place")).toContainText("WellLog", { timeout: 60_000 });
  });
});
