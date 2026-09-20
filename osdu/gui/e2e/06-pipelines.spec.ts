import { expect, test } from "./helpers";

// The pipeline catalog for a delivery flow: the module's Delivery tab and the platform's own read-only tabs beside
// it. A delivery flow opens on its Delivery tab, where the stats strip and the flow-level actions live.

test.describe.serial("pipelines", () => {
  test("pipeline detail shows the delivery, records, YAML, definition, runs and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill("wells-welllog-03-header-delivery");
    await adminPage.getByTestId("table-row").filter({ hasText: "wells-welllog-03-header-delivery" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    // A delivery flow opens on its Delivery tab: the stats strip and the flow-level actions.
    await expect(adminPage.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-probe")).toBeVisible();

    await tabs.getByRole("tab", { name: /records/i }).click();
    await expect(adminPage.getByTestId("delivery-records-search")).toBeVisible();

    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText("wells-welllog-03-header-delivery").first())
      .toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /definition/i }).click();
    await expect(adminPage.getByTestId("pipeline-definition")).toBeVisible();

    await tabs.getByRole("tab", { name: /runs/i }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /schedules/i }).click();
    await expect(adminPage.getByTestId("paged-table").first()).toBeVisible();
  });
});
