import { expect, test } from "./helpers";

// The manual submission page: every flow whose document offers manual submission, the reason a flow offers none, and
// the submission sheet opened for the flow chosen. The fixture estate has one flow that offers it (wellbore-records,
// wellbore master data) and two that do not (the well log flow declares none; the cache sync is a retrieval).

test.describe.serial("manual submission", () => {
  test("the page lists the flows that offer manual submission", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    const rows = adminPage.getByTestId("manual-submission-flows").getByTestId("table-row");
    await expect(rows.filter({ hasText: "wellbore-records" })).toHaveCount(1, { timeout: 30_000 });
    // A flow whose document offers no manual submission is not on the list at all.
    await expect(rows.filter({ hasText: "recall-welllog" })).toHaveCount(0);
    // What it renders with and the parameter a submission carries are on the row.
    const wellbore = rows.filter({ hasText: "wellbore-records" });
    await expect(wellbore).toContainText("Wellbore@1.0.0");
    await expect(wellbore).toContainText("site");
    // And what the records become: the template kind that mapping fills.
    await expect(adminPage.getByTestId("manual-submission-template-wellbore-records")).toContainText("osdu:wks:master-data--Wellbore:1.3.0");
  });

  test("the switch shows the flows that take no records, with the reason", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    await adminPage.getByTestId("manual-submission-show-all").click();
    const rows = adminPage.getByTestId("manual-submission-flows").getByTestId("table-row");
    const welllog = rows.filter({ hasText: "recall-welllog" });
    await expect(welllog).toHaveCount(1, { timeout: 30_000 });
    await expect(welllog).toContainText("manualSubmission");
    await expect(welllog.getByRole("button", { name: /submit records/i })).toHaveCount(0);
  });

  test("the filter narrows the list and the page submits to the flow chosen", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    await adminPage.getByTestId("manual-submission-search").fill("no-such-flow");
    await expect(adminPage.getByTestId("manual-submission-flows").getByTestId("table-row")).toHaveCount(0);
    await adminPage.getByTestId("manual-submission-search").fill("wellbore");

    await adminPage.getByTestId("manual-submit-wellbore-records").click();
    const dialog = adminPage.getByTestId("submit-records-dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId("submit-records-mapping")).toHaveText("Wellbore@1.0.0", { timeout: 15_000 });

    // A preview from here lands on the plan run, exactly as from the flow's own page.
    await dialog.getByTestId("submit-records-param-site").fill("demo");
    await dialog.getByTestId("submit-records-field-facility_name").fill("WB-E2E-PAGE-1");
    await dialog.getByTestId("submit-records-field-facility_description").fill("Sent from the manual submission page");
    await dialog.getByTestId("submit-records-now").click();
    await dialog.getByTestId("submit-records-preview").click();
    await expect(dialog.getByTestId("submit-records-error")).toHaveCount(0);
    await dialog.getByTestId("submit-records-submit").click();

    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 180_000 });
    await expect(adminPage.getByTestId("run-operation")).toHaveText("plan");
  });
});
