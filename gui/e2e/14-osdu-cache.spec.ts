import { expect, test } from "./helpers";

// The OSDU cache page: the types down the side, the records, history and approvals in the working surface, and a
// summary line saying which version is read, what it holds and what waits for a decision. Runs after the seed (03),
// so the fixture repo is synced: its metadata sync flow declares four cached types, and its captured snapshot
// carries their records.

test.describe.serial("osdu cache", () => {
  test("the sidebar opens the cache page with what the fixture declares and holds", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // The summary strip answers the questions the page exists for: which version is read, how much it holds, what waits.
    await expect(adminPage.getByTestId("cache-kpi-snapshot-value")).toContainText(/\d{8}T\d{6}Z/, { timeout: 30_000 });
    await expect(adminPage.getByTestId("cache-kpi-snapshot-value")).toContainText("current");
    await expect(adminPage.getByTestId("cache-kpi-types-value")).toBeVisible();
    await expect(adminPage.getByTestId("cache-kpi-records-value")).toBeVisible();
    await expect(adminPage.getByTestId("cache-kpi-pending-value")).toHaveText("0");

    // The cached types come from the metadata sync flow's cache section: the type picker lists them with their
    // family, how many records they hold and the names they capture.
    await adminPage.getByTestId("delivery-cache-type").click();
    const types = adminPage.getByRole("listbox");
    await expect(types.getByText("UnitOfMeasure").first()).toBeVisible({ timeout: 30_000 });
    await expect(types.getByText(/reference data/).first()).toBeVisible();
    await expect(types.getByText(/master data/).first()).toBeVisible();
    await expect(types.getByText(/Alias/).first()).toBeVisible();
    await adminPage.keyboard.press("Escape");

    // Without a type in scope there is no scope line to read.
    await expect(adminPage.getByTestId("delivery-cache-scope")).toHaveCount(0);

    // The records themselves, out of the current snapshot.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("picking a type filters the records, and a row opens what it caches", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    await adminPage.getByTestId("delivery-cache-type").click();
    await adminPage.getByRole("option").filter({ hasText: "Wellbore" }).first().click();
    await expect(adminPage.getByTestId("delivery-cache-clear-type")).toBeVisible();

    // The scope line names the type and what it captures, and the table gains a column per captured name.
    const scope = adminPage.getByTestId("delivery-cache-scope");
    await expect(scope).toContainText("Wellbore");
    await expect(scope).toContainText("master-data--Wellbore");
    await expect(scope).toContainText("data.NameAlias.AliasName");
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

    // Lifting the scope from the line returns to the whole cache.
    await adminPage.getByTestId("delivery-cache-clear-type").click();
    await expect(scope).toHaveCount(0);
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("search finds a cached record by a value it holds rather than its id", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // "metre" is the Name of the metre unit, not part of its id: the search covers every cached value.
    await adminPage.getByTestId("delivery-cache-search").fill("metre");
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("the version picker reads the cache at one named snapshot version", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });

    // The page opens on whichever version is current, and the picker offers the captured ones by label.
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

  test("version history lists the captured versions and what the one picked changed", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await adminPage.getByTestId("delivery-cache-tab-history").click();

    const versions = adminPage.getByTestId("delivery-cache-history-versions").getByTestId("table-row");
    await expect(versions.first()).toContainText(/\d{8}T\d{6}Z/, { timeout: 30_000 });
    await expect(versions.first()).toContainText("current");

    // Picking the newest version raises its changes in the bottom panel, or the reason they cannot be listed (the
    // fixture's first capture has nothing before it).
    await versions.first().click();
    const detail = adminPage.getByTestId("delivery-cache-history-detail");
    await expect(detail).toBeVisible();
    await expect(detail).toContainText("Changes in");
    await expect(detail.getByTestId("delivery-cache-history-table").or(detail.getByTestId("delivery-cache-history-uncomparable"))).toBeVisible();

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
