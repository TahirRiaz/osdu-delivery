import { expect, test } from "./helpers";

// The OSDU cache page: what a cache is (the cache flow file that defines it, what refreshes it, its current version and
// who captured it, and the types it declares) above what it holds (records, versions, approvals). Runs after the seed
// (03), so the fixture repo is synced, its cache flow declares four types, and the sample references were imported
// through the CLI as the cache's first version.

const CACHE = "osdu-reference-cache";

test.describe.serial("osdu cache", () => {
  test("the sidebar opens the cache with where it is defined and what it holds", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // The definition comes first: the cache, the file that defines it, and that it has no schedule in the fixture.
    const definition = adminPage.getByTestId("delivery-cache-definition");
    await expect(definition.getByTestId("delivery-cache-name")).toHaveText(CACHE, { timeout: 30_000 });
    await expect(definition.getByTestId("delivery-cache-defined-in")).toContainText(`caches/${CACHE}.yaml`);
    await expect(definition.getByTestId("delivery-cache-schedules")).toContainText("no schedule");

    // The current version, and who captured it: the seed's CLI import, with no run behind it.
    const current = definition.getByTestId("delivery-cache-current");
    await expect(current).toContainText(/\d{8}T\d{6}Z/);
    await expect(current).toContainText("cli:");
    await expect(definition.getByTestId("delivery-cache-version-count")).toHaveText("1");

    // What is cached: every declared type with the paths it keeps and what a change does.
    const types = definition.getByTestId("delivery-cache-types");
    await expect(types.getByTestId("table-row")).toHaveCount(4);
    const wellbore = types.getByTestId("table-row").filter({ hasText: "master-data--Wellbore" });
    await expect(wellbore).toContainText("NameAlias.AliasName");
    await expect(wellbore).toContainText("goes out on the next run");
    await expect(types.getByTestId("table-row").filter({ hasText: "reference-data--UnitOfMeasure" })).toContainText("waits for approval");

    // The records themselves, out of the current version.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("the definition links to the cache flow's YAML, and a refresh is a run of that flow", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("delivery-cache-name")).toHaveText(CACHE, { timeout: 30_000 });

    // Refresh now opens the trigger dialog on the cache flow with the refresh operation, and no delivery-only fields.
    // The suite never submits it: a refresh searches the OSDU target.
    await adminPage.getByTestId("delivery-cache-refresh").click();
    const dialog = adminPage.getByTestId("trigger-run-dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId("trigger-operation")).toContainText("Refresh");
    await expect(dialog.getByTestId("trigger-drop")).toHaveCount(0);
    await expect(dialog.getByTestId("trigger-submission")).toHaveCount(0);
    await adminPage.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);

    await adminPage.getByTestId("delivery-cache-view-yaml").click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();
    await expect(adminPage.getByTestId("pipeline-yaml")).toContainText("flowType: cache", { timeout: 15_000 });

    // The cache flow's own page lists its versions and links back to the cache.
    await adminPage.getByTestId("pipeline-tab-versions").click();
    await expect(adminPage.getByTestId("delivery-cache-history-versions").getByTestId("table-row").first()).toContainText(/\d{8}T\d{6}Z/, { timeout: 30_000 });
    await adminPage.getByTestId("pipeline-cache-link").click();
    await expect(adminPage.getByTestId("delivery-cache-name")).toHaveText(CACHE, { timeout: 30_000 });
  });

  test("picking a type scopes the records, and a row opens what it caches", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    const types = adminPage.getByTestId("delivery-cache-types");
    await types.getByTestId("table-row").filter({ hasText: "master-data--Wellbore" }).click();
    await expect(adminPage.getByTestId("delivery-cache-clear-type")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-tab-records")).toHaveText("Wellbore records");

    // A type in scope gives the table a column per captured name.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByRole("columnheader", { name: "Alias" })).toBeVisible({ timeout: 30_000 });
    await expect(items.getByText("master-data--Wellbore:OSDU-DEV-1-A").first()).toBeVisible({ timeout: 30_000 });

    await items.getByText("master-data--Wellbore:OSDU-DEV-1-A").first().click();
    const detail = adminPage.getByTestId("delivery-cache-item-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText("Captured values");
    await expect(adminPage.getByTestId("delivery-cache-item-copy-id")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-item-json")).toBeVisible();
    await detail.getByRole("button", { name: "Close" }).click();

    // Lifting the scope returns to the whole cache.
    await adminPage.getByTestId("delivery-cache-clear-type").click();
    await expect(adminPage.getByTestId("delivery-cache-tab-records")).toHaveText("Records");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("search finds a cached record by a value it holds rather than its id", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();

    // "metre" is the Name of the metre unit, not part of its id: the search covers every cached value.
    await adminPage.getByTestId("delivery-cache-search").fill("metre");
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("the version picker reads the cache at one named version", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();

    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });

    // The page opens on whichever version is current, and the picker offers the versions by label.
    const picker = adminPage.getByTestId("delivery-cache-version");
    await expect(picker).toContainText("Current version");
    await picker.click();
    await adminPage.getByRole("option").filter({ hasText: /^\d{8}T\d{6}Z/ }).first().click();

    // Naming that version reads the same cache, and it is not flagged as historic: it IS the current one.
    await expect(picker).not.toContainText("Current version");
    await expect(adminPage.getByTestId("delivery-cache-historic")).toHaveCount(0);
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });

    // The row says which version it is the record as of.
    await items.getByText("reference-data--UnitOfMeasure:m").first().click();
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

  test("approvals say plainly that nothing needs a decision", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await adminPage.getByTestId("delivery-cache-tab-approvals").click();
    await expect(adminPage.getByTestId("delivery-cache-tag-status")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-tag-status-pending")).toHaveAttribute("data-state", "on");
    await expect(adminPage.getByTestId("delivery-cache-approvals-empty")).toContainText("Nothing needs approval", { timeout: 30_000 });

    // The other states list through the same table, empty in the fixture.
    await adminPage.getByTestId("delivery-cache-tag-status-rejected").click();
    const table = adminPage.getByTestId("delivery-cache-tags-table");
    await expect(table).toContainText("No changes in this state", { timeout: 30_000 });
    await adminPage.getByTestId("delivery-cache-tag-status-pending").click();
    await expect(adminPage.getByTestId("delivery-cache-approvals-empty")).toBeVisible();
  });
});
