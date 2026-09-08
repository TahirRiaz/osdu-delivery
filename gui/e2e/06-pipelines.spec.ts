import { expect, test } from "./helpers";

// The pipeline catalog: list filters, the read-only YAML and definition views, the runs/schedules tabs, and
// triggering with the pipeline prefilled.

test.describe.serial("pipelines", () => {
  test("search narrows the folder tree and clears again", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();

    await adminPage.getByTestId("filter-name").fill("recall-welllog");
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first())
      .toBeVisible({ timeout: 15_000 });

    await adminPage.getByTestId("filter-name").fill("no-such-pipeline-name");
    await expect(adminPage.getByTestId("pipelines-no-matches")).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-name").fill("");
  });

  test("the repo filter scopes to one repo's project folders", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();

    // Scope to the fixture repo; a single-repo selection drops the repo level and lists its project folders directly.
    await adminPage.getByRole("combobox", { name: "Repo" }).click();
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();

    // The fixture flow sits under the flows folder, so it groups under the "flows" project folder. The folders start
    // collapsed, so open the "flows" folder to reveal its flows.
    const rootFolder = adminPage.getByTestId("repo-project").filter({ hasText: "flows" });
    await expect(rootFolder).toBeVisible({ timeout: 15_000 });
    await rootFolder.getByText("flows").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first())
      .toBeVisible({ timeout: 15_000 });
  });

  test("pipeline detail shows the delivery, YAML, definition, runs, and schedules tabs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill("recall-welllog");
    await adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    const tabs = adminPage.getByTestId("pipeline-tabs");
    // A delivery flow opens on its Delivery tab: the stats strip and the flow-level actions.
    await expect(adminPage.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("delivery-submit-drop")).toBeVisible();
    await tabs.getByRole("tab", { name: /records/i }).click();
    await expect(adminPage.getByTestId("delivery-records-search")).toBeVisible();
    await tabs.getByRole("tab", { name: /yaml/i }).click();
    await expect(adminPage.getByTestId("pipeline-yaml")).toBeVisible();
    // Monaco renders the synced document: the flow name from the fixture YAML is on screen.
    await expect(adminPage.getByTestId("pipeline-yaml").getByText("recall-welllog").first())
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
    // The folder tree starts collapsed; a search expands it and surfaces the flow row.
    await adminPage.getByTestId("filter-name").fill("recall-welllog");
    await adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first().click();
    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    // Repo and flow are locked by the pipeline context; a plan of the demo drop needs no OSDU target.
    await expect(adminPage.getByTestId("trigger-operation")).toBeVisible();
    await adminPage.getByTestId("trigger-operation").click();
    await adminPage.getByRole("option", { name: "Plan (dry run)" }).click();
    await adminPage.getByTestId("trigger-values").fill("logSource=demo");
    await adminPage.getByTestId("trigger-submit").click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: /succeeded|running|queued/ }).first())
      .toBeVisible();
  });
});
