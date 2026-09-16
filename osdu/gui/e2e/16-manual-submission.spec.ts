import { expect, test } from "./helpers";

// The manual submission page: every flow that takes records through the API, the reason a flow takes none, and the
// submission sheet opened for the flow chosen. The fixture estate has two flows that take them (the wellbore and well
// log delivery flows, both declaring source.submissions) and one that does not (the same wellbore flow with its
// submissions block removed); the pre and ingestion flows are not delivery flows at all, so they never appear.

/** The flow the sheet is opened for: wellbore master data is what a source sends by hand. */
const TAKES_RECORDS = "recall-wellbore";

/** The flow that declares nowhere for records to land. */
const TAKES_NONE = "wellbore-no-submissions";

/** The row for exactly this flow: the estate holds its pre and ingestion flows too, whose names start with the same text. */
function exactly(flow: string): RegExp {
  return new RegExp(`${flow}(?![\\w-])`);
}

test.describe.serial("manual submission", () => {
  test("the page lists the flows that take records", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    const rows = adminPage.getByTestId("manual-submission-flows").getByTestId("table-row");
    const wellbore = rows.filter({ hasText: exactly(TAKES_RECORDS) });
    await expect(wellbore).toHaveCount(1, { timeout: 30_000 });
    // A flow that declares nowhere for records to land is not on the list at all.
    await expect(rows.filter({ hasText: TAKES_NONE })).toHaveCount(0);
    // What it renders with is on the row, and so is what the records become: the template kind that mapping fills.
    await expect(wellbore).toContainText("Wellbore@1.0.0");
    await expect(adminPage.getByTestId(`manual-submission-template-${TAKES_RECORDS}`))
      .toContainText("osdu:wks:master-data--Wellbore:1.3.0");
  });

  test("the switch shows the flows that take no records, with the reason", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    await adminPage.getByTestId("manual-submission-show-all").click();
    const rows = adminPage.getByTestId("manual-submission-flows").getByTestId("table-row");
    const refused = rows.filter({ hasText: TAKES_NONE });
    await expect(refused).toHaveCount(1, { timeout: 30_000 });
    await expect(refused).toContainText("source.submissions");
    await expect(refused.getByRole("button", { name: /submit records/i })).toHaveCount(0);
  });

  test("the filter narrows the list and the page submits to the flow chosen", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-submit").click();
    await expect(adminPage.getByTestId("page-manual-submission")).toBeVisible();

    await adminPage.getByTestId("manual-submission-search").fill("no-such-flow");
    await expect(adminPage.getByTestId("manual-submission-flows").getByTestId("table-row")).toHaveCount(0);
    await adminPage.getByTestId("manual-submission-search").fill(TAKES_RECORDS);

    await adminPage.getByTestId(`manual-submit-${TAKES_RECORDS}`).click();
    const dialog = adminPage.getByTestId("submit-records-dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByTestId("submit-records-mapping")).toHaveText("Wellbore@1.0.0", { timeout: 15_000 });

    // A preview from here lands on the plan run, exactly as from the flow's own page.
    await dialog.getByTestId("submit-records-field-facility_name").fill("WB-E2E-PAGE-1");
    await dialog.getByTestId("submit-records-field-facility_description").fill("Sent from the manual submission page");
    await dialog.getByTestId("submit-records-now").click();
    await dialog.getByTestId("submit-records-preview").click();
    await expect(dialog.getByTestId("submit-records-error")).toHaveCount(0);
    await dialog.getByTestId("submit-records-submit").click();

    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 300_000 });
    await expect(adminPage.getByTestId("run-operation")).toHaveText("plan");
  });
});
