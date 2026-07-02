import { expect, test } from "./helpers";

// The explore surface: lineage objects and the drawer, the dependency graph, and search with its deep links.
// The seeded estate is one file flow, so object/lineage volume is small but real (the sync registers the flow's
// target objects and edges).

test.describe("explore", () => {
  test("lineage page lists objects or a clean empty state; the drawer opens on a row", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await expect(adminPage.getByTestId("page-lineage")).toBeVisible();

    const firstRow = adminPage.getByTestId("table-row").first();
    const empty = adminPage.getByTestId("empty-message");
    await expect(firstRow.or(empty)).toBeVisible({ timeout: 30_000 });

    if (await firstRow.isVisible()) {
      await firstRow.click();
      await expect(adminPage.getByTestId("object-drawer")).toBeVisible();
      await adminPage.keyboard.press("Escape");
    }
  });

  test("lineage filters and the graph-view button work", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await adminPage.getByTestId("filter-object-name").fill("no-such-object-zzz");
    await expect(adminPage.getByTestId("empty-message")).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-object-name").fill("");

    await adminPage.getByTestId("open-lineage-graph").click();
    await expect(adminPage.getByTestId("page-lineage-graph")).toBeVisible();
    await expect(adminPage.getByTestId("graph-empty")).toBeVisible();
  });

  test("the dependency graph renders for the seeded repo (nodes or the no-lineage state)", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-lineage").click();
    await adminPage.getByTestId("open-lineage-graph").click();
    await adminPage.getByTestId("graph-repo-select").click();
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();

    const node = adminPage.locator(".react-flow__node").first();
    const noLineage = adminPage.getByTestId("graph-no-lineage");
    await expect(node.or(noLineage)).toBeVisible({ timeout: 60_000 });

    if (await node.isVisible()) {
      await expect(adminPage.getByTestId("wave-list")).toBeVisible();
      // A node click navigates to the pipeline behind it.
      await node.click();
      await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 15_000 });
    }
  });

  test("search runs across all three tabs and deep-links into lineage", async ({ adminPage }) => {
    await adminPage.getByTestId("global-search").fill("Csv_Basic");
    await adminPage.getByTestId("global-search").press("Enter");
    await expect(adminPage.getByTestId("page-search")).toBeVisible();

    const tabs = adminPage.getByTestId("search-tabs");
    for (const tab of [/objects/i, /columns/i, /definitions/i]) {
      await tabs.getByRole("tab", { name: tab }).click();
      await expect(
        adminPage.getByTestId("table-row").first().or(adminPage.getByTestId("empty-message")),
      ).toBeVisible({ timeout: 30_000 });
    }

    // An empty query never fires the (400-on-empty) endpoints; the hint renders instead.
    await adminPage.getByTestId("search-input").fill("");
    await adminPage.getByTestId("search-submit").click();
    await expect(adminPage.getByTestId("search-hint")).toBeVisible();
  });
});
