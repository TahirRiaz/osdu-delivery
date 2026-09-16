import type { Page } from "@playwright/test";
import { expect, test } from "./helpers";

// Records sent by hand: the Submit records dialog reads the flow's source contract, refuses a flow that declares nowhere
// for records to land, validates before it sends, and a preview of one wellbore lands on a plan run that renders it.
// Wellbore master data is what a source sends by hand, so the flow it runs against is the wellbore one. A plan needs no
// OSDU target, so the spec runs on any machine.

/** The flow that takes records: it declares source.submissions, naming the pre flows its rows land for. */
const TAKES_RECORDS = "recall-wellbore";

/** The same flow with its source.submissions block removed, which is what a refusal looks like. */
const TAKES_NONE = "wellbore-no-submissions";

const WELLBORE = {
  facility_name: "WB-E2E-1",
  facility_description: "Sent from the GUI",
  facility_id: "srn:master-data/Wellbore:WB-E2E-1",
};

/** The row for exactly this flow: the estate holds its pre and ingestion flows too, whose names start with the same text. */
function exactly(flow: string): RegExp {
  return new RegExp(`${flow}(?![\\w-])`);
}

async function openFlow(page: Page, flow: string): Promise<void> {
  await page.getByTestId("nav-pipelines").click();
  await page.getByTestId("filter-name").fill(flow);
  await page.getByTestId("table-row").filter({ hasText: exactly(flow) }).first().click();
  await expect(page.getByTestId("page-pipeline-detail")).toBeVisible();
  await expect(page.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
}

async function openDialog(page: Page, flow: string) {
  await openFlow(page, flow);
  await page.getByTestId("delivery-submit-records").click();
  const dialog = page.getByTestId("submit-records-dialog");
  await expect(dialog).toBeVisible();
  return dialog;
}

test.describe.serial("submit records", () => {
  test("a flow that declares nowhere for records to land refuses them", async ({ adminPage }) => {
    const dialog = await openDialog(adminPage, TAKES_NONE);

    await expect(dialog.getByTestId("submit-records-refusal")).toContainText("source.submissions", { timeout: 15_000 });
    await expect(dialog.getByTestId("submit-records-submit")).toBeDisabled();
    await expect(dialog.getByTestId("submit-records-tab-form")).toHaveCount(0);
  });

  test("the form is built from the flow's source contract", async ({ adminPage }) => {
    const dialog = await openDialog(adminPage, TAKES_RECORDS);

    await expect(dialog.getByTestId("submit-records-mapping")).toHaveText("Wellbore@1.0.0", { timeout: 15_000 });
    // The columns the mapping reads, and the column the flow orders its versions by.
    for (const column of Object.keys(WELLBORE)) {
      await expect(dialog.getByTestId(`submit-records-field-${column}`)).toBeVisible();
    }

    await expect(dialog.getByTestId("submit-records-field-update_date")).toBeVisible();
    // The template version the mapping fills, and what a column fills in it.
    await expect(dialog.getByTestId("submit-records-template")).toContainText("osdu:wks:master-data--Wellbore:1.3.0");
    await expect(dialog.getByTestId("submit-records-uses-facility_name")).toContainText("osdu.data.FacilityName");
    // The aliases child dataset the mapping repeats is named with what it fills, and the JSON tab is where child rows go.
    await expect(dialog.getByTestId("submit-records-dataset-aliases")).toContainText("aliases");
    await expect(dialog.getByTestId("submit-records-submit")).toBeDisabled();
  });

  test("an incomplete record is refused before anything is sent", async ({ adminPage }) => {
    const dialog = await openDialog(adminPage, TAKES_RECORDS);
    await expect(dialog.getByTestId("submit-records-mapping")).toBeVisible({ timeout: 15_000 });

    // A description without the key column: the record has no id to be delivered under.
    await dialog.getByTestId("submit-records-field-facility_description").fill("no name");
    await expect(dialog.getByTestId("submit-records-error")).toContainText("facility_name");
    await expect(dialog.getByTestId("submit-records-submit")).toBeDisabled();

    // A submission id that is not a UUID is refused too.
    await dialog.getByTestId("submit-records-field-facility_name").fill(WELLBORE.facility_name);
    await dialog.getByTestId("submit-records-id").fill("not-a-uuid");
    await expect(dialog.getByTestId("submit-records-error")).toContainText("UUID");
    await dialog.getByTestId("submit-records-id").fill("");
    await expect(dialog.getByTestId("submit-records-error")).toHaveCount(0);
  });

  test("a preview of one wellbore lands on a plan run that renders it", async ({ adminPage }) => {
    const dialog = await openDialog(adminPage, TAKES_RECORDS);
    await expect(dialog.getByTestId("submit-records-mapping")).toBeVisible({ timeout: 15_000 });

    for (const [column, value] of Object.entries(WELLBORE)) {
      await dialog.getByTestId(`submit-records-field-${column}`).fill(value);
    }

    await dialog.getByTestId("submit-records-now").click();
    await expect(dialog.getByTestId("submit-records-field-update_date")).not.toHaveValue("");
    await dialog.getByTestId("submit-records-preview").click();
    await expect(dialog.getByTestId("submit-records-error")).toHaveCount(0);
    await expect(dialog.getByTestId("submit-records-submit")).toHaveText("Preview");
    await dialog.getByTestId("submit-records-submit").click();

    // The submission lands its rows as files, the chain loads them, and the OSDU member plans what they became.
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 300_000 });
    await expect(adminPage.getByTestId("run-operation")).toHaveText("plan");
  });

  test("the JSON tab takes a record with its child rows", async ({ adminPage }) => {
    const dialog = await openDialog(adminPage, TAKES_RECORDS);
    await expect(dialog.getByTestId("submit-records-mapping")).toBeVisible({ timeout: 15_000 });
    await dialog.getByTestId("submit-records-tab-json").click();

    // Monaco holds the starting JSON the contract produced: the columns the mapping reads and a blank row for the aliases
    // child dataset.
    const editor = dialog.getByTestId("submit-records-json");
    await expect(editor).toBeVisible();
    await expect(editor).toContainText("facility_name");
    await expect(editor).toContainText("aliases");

    await dialog.getByTestId("submit-records-preview").click();
    await dialog.getByTestId("submit-records-submit").click();

    // The template's blank values are sent as they are: the run renders them and the record is reported untracked,
    // which is a run that succeeded and delivered nothing.
    await expect(adminPage.getByTestId("page-run-detail")).toBeVisible({ timeout: 15_000 });
    await expect(adminPage.getByTestId("status-badge").filter({ hasText: "succeeded" }).first())
      .toBeVisible({ timeout: 300_000 });
  });
});
