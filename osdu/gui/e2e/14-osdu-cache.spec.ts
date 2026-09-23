import { CACHE, SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// The OSDU cache page: the header names the cache and the file that defines it, a summary row says which version is
// read and what it holds, and the records, versions, changes and definition are tabs, with a searchable type picker in
// the tab bar. Runs after the seed (03), so the fixture repo is synced, its cache flow declares
// five types (none asking for approval), and the sample records were imported through the CLI as the first version.

/** The partition the sample cache flow fills, and so the cache the page shows. */
const PARTITION = "dev";

test.describe.serial("osdu cache", () => {
  test("the page opens on the cache: where it is defined, what it holds, and its types", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    await expect(adminPage.getByTestId("delivery-cache-name")).toHaveText(PARTITION, { timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-cache-defined-in")).toHaveText(`${SOURCE}/cache/${CACHE}.yaml`);

    // The summary: the version deliveries read, how much it holds, how it is refreshed, and that changes need no one.
    await expect(adminPage.getByTestId("delivery-cache-current-value")).toHaveText(/\d{8}T\d{6}Z/);
    await expect(adminPage.getByTestId("delivery-cache-summary")).toContainText("cli:");
    await expect(adminPage.getByTestId("delivery-cache-records-value")).toHaveText(/^\d+$/);
    await expect(adminPage.getByTestId("delivery-cache-schedules-value")).toHaveText("on demand");
    await expect(adminPage.getByTestId("delivery-cache-approval-value")).toHaveText("automatic", { timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-cache-pending-banner")).toHaveCount(0);

    // The types, in the searchable picker in the tab bar, each with its family and how many records it holds.
    const picker = adminPage.getByTestId("delivery-cache-type");
    await expect(picker).toHaveText(/All types/);
    await picker.click();
    const options = adminPage.getByRole("listbox");
    for (const name of ["UnitOfMeasure", "LogCurveBusinessValue", "VerticalMeasurementType", "TrajectoryStationPropertyType"]) {
      await expect(options.getByRole("option").filter({ hasText: name }).first()).toBeVisible();
    }
    await expect(options.getByText(/reference data · \d+ records?/).first()).toBeVisible();

    // Wellbores are searched for on the platform as a record needs one, never captured.
    await expect(options.getByRole("option").filter({ hasText: "Wellbore" })).toHaveCount(0);
    await adminPage.keyboard.press("Escape");

    // The records of every type, out of the current version.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByTestId("table-row").filter({ hasText: "UnitOfMeasure" }).filter({ hasText: "metre" }).first())
      .toBeVisible({ timeout: 30_000 });
  });

  test("the definition tab reads back the file, and the header opens its YAML and refreshes it", async ({ adminPage }) => {
    await adminPage.goto("/delivery/cache?tab=definition");
    const definition = adminPage.getByTestId("delivery-cache-definition");
    await expect(definition).toContainText(`${SOURCE}/cache/${CACHE}.yaml`, { timeout: 30_000 });
    await expect(definition).toContainText("goes out on the next run");
    const rows = definition.getByTestId("delivery-cache-definition-types").getByTestId("table-row");
    await expect(rows).toHaveCount(4);
    await expect(rows.filter({ hasText: "reference-data--UnitOfMeasure" })).toContainText("data.Code");
    await expect(rows.filter({ hasText: "master-data--Wellbore" })).toHaveCount(0);
    await expect(definition.getByTestId("delivery-cache-definition-flows").getByTestId("table-row")).toHaveCount(1);

    // The guide says how a mapping reads the cache, with an entry to start from, and never names the cache.
    const guide = definition.getByTestId("delivery-cache-mapping-guide");
    await expect(guide).toContainText(PARTITION);
    await expect(guide.getByTestId("delivery-cache-mapping-guide-entry")).toContainText(/source: cache\.\w+\.id/);

    // Refresh now opens the trigger dialog on the cache flow with the refresh operation, and no delivery-only fields.
    // The suite never submits it: a refresh searches the OSDU target.
    await adminPage.getByTestId("delivery-cache-refresh").click();
    const dialog = adminPage.getByTestId("trigger-run-dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId("trigger-operation")).toContainText("Refresh");
    await expect(dialog.getByTestId("trigger-submission")).toHaveCount(0);
    await adminPage.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);

    // Cache files lists every cache flow file as a filter on Pipelines, since a partition can be filled by several.
    await expect(adminPage.getByTestId("delivery-cache-files")).toContainText("1");
    await adminPage.getByTestId("delivery-cache-files").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();
    await expect(adminPage.getByTestId("filter-kind")).toHaveText(/cache/);
    // One repo fills the partition, so the link scopes to it too, and a single repo lists its project folders
    // collapsed. The repository is laid out per source, so that is the one project the cache flow lives under.
    const folder = adminPage.getByTestId("repo-project").filter({ hasText: SOURCE });
    await expect(folder).toHaveCount(1, { timeout: 30_000 });
    await expect(adminPage.getByTestId("repo-project")).toHaveCount(1);
    await folder.getByText(SOURCE, { exact: true }).click();
    const cacheRow = adminPage.getByTestId("repo-pipeline").filter({ hasText: CACHE });
    await expect(cacheRow.first()).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("repo-pipeline").filter({ hasText: "wells-welllog-03-header-delivery" })).toHaveCount(0);
    await cacheRow.first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();
    await adminPage.getByTestId("pipeline-tab-yaml").click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toContainText("flowType: cache", { timeout: 15_000 });

    // The cache flow's own page lists its versions and links back to the cache.
    await adminPage.getByTestId("pipeline-tab-versions").click();
    await expect(adminPage.getByTestId("delivery-cache-history-versions").getByTestId("table-row").first()).toContainText(/\d{8}T\d{6}Z/, { timeout: 30_000 });
    await adminPage.getByTestId("pipeline-cache-link").click();
    await expect(adminPage.getByTestId("delivery-cache-name")).toHaveText(PARTITION, { timeout: 30_000 });
  });

  test("picking a type scopes the records, and a row opens what it caches", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    const picker = adminPage.getByTestId("delivery-cache-type");
    await picker.click();
    await adminPage.getByRole("option").filter({ hasText: "UnitOfMeasure" }).first().click();
    await expect(picker).toHaveText(/UnitOfMeasure/);
    await expect(adminPage.getByTestId("delivery-cache-tab-records")).toHaveText("UnitOfMeasure records");

    // A type in scope gives the table a column per captured name.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByRole("columnheader", { name: "Code" })).toBeVisible({ timeout: 30_000 });
    await expect(items.getByText("reference-data--UnitOfMeasure:dega").first()).toBeVisible({ timeout: 30_000 });

    await items.getByText("reference-data--UnitOfMeasure:dega").first().click();
    const detail = adminPage.getByTestId("delivery-cache-item-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText("Captured values");
    await expect(adminPage.getByTestId("delivery-cache-item-copy-id")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-item-json")).toBeVisible();

    // How a mapping reads the record, with the value each reference reads for it: the id as a relationship, and each
    // captured name, plus an entry that finds the record by one of its values.
    const mapping = adminPage.getByTestId("delivery-cache-item-mapping");
    const references = mapping.getByTestId("delivery-cache-item-reference");
    await expect(references.filter({ hasText: "cache.UnitOfMeasure.id" })).toContainText("reference-data--UnitOfMeasure:dega:");
    await expect(references.filter({ hasText: "cache.UnitOfMeasure.Code" })).toBeVisible();
    await expect(mapping.getByTestId("delivery-cache-item-entry")).toContainText("source: cache.UnitOfMeasure.id");
    await expect(mapping.getByTestId("delivery-cache-item-entry")).toContainText("findBy: cache.UnitOfMeasure.");
    await detail.getByRole("button", { name: "Close" }).click();

    // Clearing the picker lifts the scope.
    await picker.click();
    await adminPage.getByRole("option").filter({ hasText: "Clear filter" }).click();
    await expect(picker).toHaveText(/All types/);
    await expect(adminPage.getByTestId("delivery-cache-tab-records")).toHaveText("Records");
    await expect(items.getByTestId("table-row").filter({ hasText: "LogCurveBusinessValue" }).first()).toBeVisible({ timeout: 30_000 });
  });

  test("search finds a cached record by a value it holds rather than its id", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();

    // "metre" is the Name of the metre unit, not part of its id: the search covers every cached value.
    await adminPage.getByTestId("delivery-cache-search").fill("metre");
    const rows = adminPage.getByTestId("delivery-cache-items-table").getByTestId("table-row");
    await expect(rows.filter({ hasText: "metre" }).first()).toBeVisible({ timeout: 30_000 });
    await expect(rows.filter({ hasText: "LogCurveBusinessValue" })).toHaveCount(0);
  });

  test("the version picker reads the cache at one named version", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();

    const rows = adminPage.getByTestId("delivery-cache-items-table").getByTestId("table-row");
    await expect(rows.filter({ hasText: "metre" }).first()).toBeVisible({ timeout: 30_000 });

    // The records open on whichever version is current, and the picker offers the versions by label.
    const picker = adminPage.getByTestId("delivery-cache-version");
    await expect(picker).toContainText("Current version");
    await picker.click();
    await adminPage.getByRole("option").filter({ hasText: /^\d{8}T\d{6}Z/ }).first().click();

    // Naming that version reads the same cache, and it is not flagged as historic: it IS the current one.
    await expect(picker).not.toContainText("Current version");
    await expect(adminPage.getByTestId("delivery-cache-historic")).toHaveCount(0);
    await expect(rows.filter({ hasText: "metre" }).first()).toBeVisible({ timeout: 30_000 });

    // The row says which version it is the record as of.
    await rows.filter({ hasText: "metre" }).first().click();
    await expect(adminPage.getByTestId("delivery-cache-item-version")).toContainText(/\d{8}T\d{6}Z/);
  });

  test("versions list who captured each one and what the one picked changed", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await adminPage.getByTestId("delivery-cache-tab-versions").click();

    const versions = adminPage.getByTestId("delivery-cache-history-versions").getByTestId("table-row");
    await expect(versions.first()).toContainText(/\d{8}T\d{6}Z/, { timeout: 30_000 });
    await expect(versions.first()).toContainText("current");
    await expect(versions.first()).toContainText("files under");
    await expect(versions.first()).toContainText("first version");

    // The imported version is the first, so there is nothing before it to compare with.
    await versions.first().click();
    const detail = adminPage.getByTestId("delivery-cache-history-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText("Changes in");
    await expect(detail.getByTestId("delivery-cache-history-uncomparable")).toBeVisible();

    // Closing the panel leaves the list where it was; leaving the tab closes it.
    await adminPage.getByRole("button", { name: "Close panel" }).click();
    await expect(detail).toHaveCount(0);
    await versions.first().click();
    await expect(detail).toBeVisible();
    await adminPage.getByTestId("delivery-cache-tab-records").click();
    await expect(detail).toHaveCount(0);
  });

  test("changes go out automatically unless a type asks for approval, and the page says so", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await adminPage.getByTestId("delivery-cache-tab-changes").click();

    // No type asks for approval, so the list opens on every change rather than on an approval queue.
    await expect(adminPage.getByTestId("delivery-cache-tag-status-all")).toHaveAttribute("data-state", "on", { timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-cache-approval-rule")).toHaveText("Every type updates automatically.");
    await expect(adminPage.getByTestId("delivery-cache-tags-table")).toContainText("No refresh of this cache has changed a value", { timeout: 30_000 });

    // The approval queue is empty, and says how to switch approval on for a type.
    await adminPage.getByTestId("delivery-cache-tag-status-pending").click();
    const empty = adminPage.getByTestId("delivery-cache-approvals-empty");
    await expect(empty).toContainText("Nothing needs approval", { timeout: 30_000 });
    await expect(empty).toContainText("onChange: approve");
  });
});
