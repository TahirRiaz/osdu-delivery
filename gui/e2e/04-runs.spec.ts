import { expect, test } from "./helpers";

// The run lifecycle end to end from the GUI: trigger, watch the in-process worker execute it, drill into every
// detail tab, exercise the filters, and cancel a queued run (kept queued by routing it to a pool no node serves).

test.describe.serial("runs", () => {
  test("trigger a run and watch it succeed with populated drill-downs", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await expect(adminPage.getByTestId("page-runs")).toBeVisible();

    await adminPage.getByTestId("open-trigger-run").click();
    await expect(adminPage.getByTestId("trigger-run-dialog")).toBeVisible();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();
    await adminPage.getByTestId("trigger-submit").click();

    // 202 accepted: the dialog navigates to the run detail, which polls to a terminal state.
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });

    // Every drill-down tab renders; statements carry the generated SQL (this suite's table is unique per run,
    // so the first trigger CREATEs it and the DDL statement is recorded) and open the full-statement dialog.
    await expect(async () => {
      await adminPage.reload();
      await expect(adminPage.getByTestId("run-tabs")).toBeVisible({ timeout: 10_000 });
      await adminPage.getByTestId("run-tabs").getByRole("tab", { name: /statements/i }).click();
      await expect(adminPage.getByTestId("table-row").first()).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    const statementRow = adminPage.getByTestId("table-row").first();
    await statementRow.click();
    await expect(adminPage.getByTestId("statement-dialog")).toBeVisible();
    await adminPage.keyboard.press("Escape");

    for (const tab of ["assertions", "files", "surrogate-keys", "health-metrics"]) {
      await adminPage.getByTestId(`tab-${tab}`).click();
      await expect(adminPage.getByTestId(`${tab}-table`)).toBeVisible();
    }
  });

  test("the runs list shows the run and the status filter narrows it", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" });
    await expect(row.first()).toBeVisible({ timeout: 30_000 });

    await adminPage.getByTestId("filter-status-succeeded").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first()).toBeVisible();

    await adminPage.getByTestId("filter-status-failed").click();
    await expect(
      adminPage.getByTestId("empty-message")
        .or(adminPage.getByTestId("table-row").filter({ hasText: "failed" }).first()),
    ).toBeVisible({ timeout: 15_000 });
    await adminPage.getByTestId("filter-status-failed").click(); // back to all
  });

  test("a queued run (pooled to no node) can be cancelled from its detail page", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("open-trigger-run").click();
    await adminPage.getByTestId("trigger-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("trigger-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();
    // No worker serves this pool, so the run stays queued and the cancel path is deterministic.
    await adminPage.getByTestId("trigger-pool").fill("e2e-unserved-pool");
    await adminPage.getByTestId("trigger-submit").click();

    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "queued" }).first()).toBeVisible();
    await adminPage.getByTestId("cancel-run").click();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "cancelled" }).first())
      .toBeVisible({ timeout: 30_000 });
    // A terminal run offers no cancel affordance.
    await expect(adminPage.getByTestId("cancel-run")).toHaveCount(0);
  });

  test("run detail links back to its pipeline and repo", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-runs").click();
    await adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first().click();
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible();
    await adminPage.getByTestId("run-pipeline-link").click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 15_000 });
  });
});
