import { expect, test } from "./helpers";

// The OSDU cache page: what the repositories declare they cache, what the current reference snapshot holds, and
// the changes waiting for a decision. Runs after the seed (03), so the fixture repo is synced: its metadata sync
// flow declares four cached types, and its captured snapshot carries their records.

test.describe.serial("osdu cache", () => {
  test("the sidebar opens the cache page with what the fixture declares and holds", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // The summary strip answers the three questions the page exists for.
    await expect(adminPage.getByTestId("cache-kpi-types-value")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("cache-kpi-records-value")).toBeVisible();
    await expect(adminPage.getByTestId("cache-kpi-pending-value")).toHaveText("0");

    // The cached types come from the metadata sync flow's cache section, with the paths they capture.
    const types = adminPage.getByTestId("delivery-cache-types");
    await expect(types.getByText("UnitOfMeasure").first()).toBeVisible({ timeout: 30_000 });
    await expect(types.getByText("Alias").first()).toBeVisible();

    // The records themselves, out of the current snapshot.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("picking a type filters the records, and a row opens what it caches", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    await adminPage.getByTestId("delivery-cache-type-card").filter({ hasText: "Wellbore" }).first().click();
    await expect(adminPage.getByTestId("delivery-cache-clear-type")).toBeVisible();
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("master-data--Wellbore:OSDU-DEV-1-A").first()).toBeVisible({ timeout: 30_000 });

    await items.getByText("master-data--Wellbore:OSDU-DEV-1-A").first().click();
    await expect(adminPage.getByTestId("delivery-cache-item-detail")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-item-json")).toBeVisible();
  });

  test("search finds a cached record by a value it holds rather than its id", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // "metre" is the Name of the metre unit, not part of its id: the search covers every cached value.
    await adminPage.getByTestId("delivery-cache-search").fill("metre");
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("the updates tab is empty until a cached value moves", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await adminPage.getByTestId("delivery-cache-tab-updates").click();
    await expect(adminPage.getByTestId("delivery-cache-tag-status")).toBeVisible();
    await expect(adminPage.getByText("Nothing is waiting")).toBeVisible({ timeout: 30_000 });
  });
});
