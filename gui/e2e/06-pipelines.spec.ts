import { expect, test } from "./helpers";

// The pipeline catalog: list filters, the read-only YAML and definition views, the runs/schedules tabs, and
// triggering with the pipeline prefilled.

test.describe.serial("pipelines", () => {
  test("filters narrow the list and clear again", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();

    await adminPage.getByTestId("filter-name").fill("Csv_Basic");
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first())
      .toBeVisible({ timeout: 15_000 });

    await adminPage.getByTestId("filter-name").fill("no-such-pipeline-name");
    await expect(adminPage.getByTestId("empty-message")).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-name").fill("");
  });

  test("pipeline detail shows YAML, definition, runs, and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText("Csv_Basic").first())
      .toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /definition/i }).click();
    await expect(adminPage.getByTestId("pipeline-definition")).toBeVisible();

    await tabs.getByRole("tab", { name: /runs/i }).click();
    await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    await tabs.getByRole("tab", { name: /schedules/i }).click();
    await expect(adminPage.getByTestId("paged-table").first()).toBeVisible();
  });

  test("trigger from the pipeline detail is prefilled and lands on the run", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    // Repo and flow are locked by the pipeline context; just submit.
    await adminPage.getByTestId("trigger-submit").click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: /succeeded|running|queued/ }).first())
      .toBeVisible();
  });
});
