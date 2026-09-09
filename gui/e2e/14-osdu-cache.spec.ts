import { expect, test } from "./helpers";

// The OSDU cache page: the read-only view over what the repositories declare they cache and what the current
// reference snapshot actually holds. Runs after the seed (03), so the fixture repo is synced: its metadata sync
// flow declares four cached types, and its captured snapshot carries their records.

test.describe.serial("osdu cache", () => {
  test("the sidebar opens the cache page with what the fixture declares and holds", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // The definitions come from the metadata sync flow's cache section, the counts from the snapshot.
    const definitions = adminPage.getByTestId("delivery-cache-definitions-table");
    await expect(definitions).toBeVisible({ timeout: 30_000 });
    await expect(definitions.getByText("UnitOfMeasure").first()).toBeVisible();
    await expect(definitions.getByText("osdu-cache-sync").first()).toBeVisible();

    // The alias path is cached under its declared name, which is what a mapping matches by.
    await expect(definitions.getByText("Alias").first()).toBeVisible();

    // The records themselves, out of the current snapshot.
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items).toBeVisible();
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });
  });

  test("search finds a cached record by a value it holds, and a row opens what it caches", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-cache").click();
    await expect(adminPage.getByTestId("page-delivery-cache")).toBeVisible();

    // "metre" is the Name of the metre unit, not part of its id: the search covers every cached value.
    await adminPage.getByTestId("delivery-cache-search").fill("metre");
    const items = adminPage.getByTestId("delivery-cache-items-table");
    await expect(items.getByText("reference-data--UnitOfMeasure:m").first()).toBeVisible({ timeout: 30_000 });

    await items.getByText("reference-data--UnitOfMeasure:m").first().click();
    await expect(adminPage.getByTestId("delivery-cache-item-detail")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-cache-item-json")).toBeVisible();
  });
});
