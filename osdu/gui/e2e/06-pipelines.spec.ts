import { DELIVERY_FLOW } from "./global-setup";
import { expect, test } from "./helpers";

// The pipeline catalog for a delivery flow: the module's Delivery tab and the platform's own read-only tabs beside
// it. A delivery flow opens on its Delivery tab, where the stats strip and the flow-level actions live. What kind of
// flow it is reads everywhere the flow is shown: its row in the tree, its page's header, and its workbench tab.

test.describe.serial("pipelines", () => {
  test("pipeline detail names its kind and shows the delivery, records, YAML, runs and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    const row = adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first();
    // The row names its kind as the module describes it, not by the flowType value alone.
    await expect(row.getByTestId("repo-pipeline-kind")).toHaveText("Delivery");
    await expect(row.getByTestId("repo-pipeline-kind")).toHaveAttribute("data-kind", "delivery");
    await row.click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    // The kind leads the header: its tile before the flow's name and its name under it. The workbench tab wears the same
    // icon, so pipelines of several kinds open side by side read apart.
    await expect(adminPage.getByTestId("pipeline-kind")).toHaveAttribute("data-kind", "delivery");
    await expect(adminPage.getByTestId("pipeline-kind-label")).toHaveText("Delivery flow");
    await expect(adminPage.getByRole("tablist", { name: "Open pages" }).getByRole("tab", { selected: true })
      .locator("svg[data-kind='delivery']")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    // A delivery flow opens on its Delivery tab: the stats strip and the flow-level actions.
    await expect(adminPage.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-probe")).toBeVisible();

    await tabs.getByRole("tab", { name: /records/i }).click();
    await expect(adminPage.getByTestId("delivery-records-search")).toBeVisible();

    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText(DELIVERY_FLOW).first())
      .toBeVisible({ timeout: 30_000 });

    // The Definition tab would show the YAML tab's document again as JSON, so the kind hides it.
    await expect(tabs.getByRole("tab", { name: /definition/i })).toHaveCount(0);

    await tabs.getByRole("tab", { name: /runs/i }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /schedules/i }).click();
    await expect(adminPage.getByTestId("paged-table").first()).toBeVisible();
  });
});
