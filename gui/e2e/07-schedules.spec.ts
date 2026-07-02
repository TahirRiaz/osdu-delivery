import { expect, test } from "./helpers";

// Schedule CRUD from the GUI: create (interval), pause, resume, and delete, plus the pipeline cross-link.

test.describe.serial("schedules", () => {
  test("create an interval schedule for the synced pipeline", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    await expect(adminPage.getByTestId("page-schedules")).toBeVisible();

    await adminPage.getByTestId("open-create-schedule").click();
    await adminPage.getByTestId("schedule-repo").fill("e2e-repo");
    await adminPage.getByRole("option", { name: "e2e-repo" }).click();
    await adminPage.getByTestId("schedule-flow").fill("Csv");
    await adminPage.getByRole("option", { name: "Csv_Basic" }).click();

    // Interval trigger: 6 hours, so the scheduler will not actually fire during the suite.
    await adminPage.getByTestId("schedule-trigger-interval").click();
    await adminPage.getByTestId("schedule-interval").fill("21600");
    await adminPage.getByTestId("create-schedule-submit").click();

    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" });
    await expect(row.first()).toBeVisible({ timeout: 15_000 });
    await expect(row.first().getByTestId("schedule-badge")).toHaveText("enabled");
  });

  test("pause and resume flip the schedule state", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("schedule-pause").click();
    await expect(row.getByTestId("schedule-badge")).toHaveText("paused", { timeout: 15_000 });

    await row.getByTestId("schedule-resume").click();
    await expect(row.getByTestId("schedule-badge")).toHaveText("enabled", { timeout: 15_000 });
  });

  test("the flow name links to the pipeline detail", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first();
    await row.getByTestId("schedule-flow-link").click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible({ timeout: 15_000 });
  });

  test("delete removes the schedule after confirmation", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-schedules").click();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    await row.getByTestId("schedule-delete").click();
    await expect(adminPage.getByTestId("confirm-dialog")).toBeVisible();
    await adminPage.getByTestId("confirm-dialog-confirm").click();
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "Csv_Basic" }))
      .toHaveCount(0, { timeout: 15_000 });
  });
});
