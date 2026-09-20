import { expect, test } from "./helpers";

// Traceability from the product's front door (CLAUDE.md: traceability is the product). An operator holding a source key,
// an OSDU id or a file name finds the record without knowing which flow delivered it, and the record's page tells its
// journey: when the row was received, when it was planned, every dispatch and what OSDU answered, and where it stands.
// A batch is undoable from its submission's page, aimed at what that submission delivered.
//
// The intake of the records CLI spec staged the well logs of the STAT_COMP scope, so the ledger holds pending records
// with a queued document and no dispatch yet; nothing here reaches an OSDU, and no removal is run.

test.describe.serial("record trace", () => {
  test("a record is found from what an operator holds, and its page tells its journey", async ({ adminPage }) => {
    // The Delivery page's field opens the Records page looked up for the term.
    await adminPage.getByTestId("nav-delivery").click();
    await expect(adminPage.getByTestId("page-delivery")).toBeVisible();
    // A source key is the source's name and the row's key columns (wells:NO_15_9/L-1001); the lookup is a prefix.
    await adminPage.getByTestId("delivery-find-record-term").fill("wells:NO_15_9");
    await adminPage.getByTestId("delivery-find-record-go").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage).toHaveURL(/q=wells%3ANO_15_9/);
    await expect(adminPage.getByTestId("delivery-lookup-search")).toHaveValue("wells:NO_15_9");

    // The lookup lists every record whose source key starts with the term, across flows, each naming its flow.
    const table = adminPage.getByTestId("delivery-lookup-table");
    await expect(table).toBeVisible();
    const rows = table.getByTestId("table-row");
    await expect(rows.first()).toBeVisible({ timeout: 30_000 });
    await expect(rows.first()).toContainText("NO_15_9");
    await expect(rows.first()).toContainText("wells-welllog-03-header-delivery");

    // Every row says which of its values answered, so a hit explains itself.
    await expect(rows.first().getByTestId("lookup-matched")).toBeVisible();

    // The wellbore id the mapping declares as an identity finds it, which is what an operator actually holds. The
    // log id does too, and neither is the start of the source key.
    for (const [term, url] of [["OSDU-DEV-1-A", /q=OSDU-DEV-1-A/], ["L-1001", /q=L-1001/]] as const) {
      await adminPage.getByTestId("delivery-lookup-search").fill(term);
      await expect(adminPage).toHaveURL(url);
      await expect(rows.first()).toBeVisible({ timeout: 30_000 });
      await expect(rows.first().getByTestId("lookup-matched")).toContainText(term);
    }

    await adminPage.getByTestId("delivery-lookup-search").fill("wells:NO_15_9");
    await expect(adminPage).toHaveURL(/q=wells%3ANO_15_9/);
    await expect(rows.first()).toContainText("NO_15_9", { timeout: 30_000 });

    // A state narrows the same lookup; a state nothing is in leaves it empty rather than wrong.
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "deleted" }).click();
    await expect(adminPage).toHaveURL(/status=deleted/);
    await expect(table.getByText(/No record starts with that/)).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "pending" }).click();
    await expect(rows.first()).toBeVisible({ timeout: 30_000 });

    // A row opens the record, whose journey starts with the row the intake staged it from and ends with the queued
    // document: planned, never dispatched, nothing landed.
    await rows.first().click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();
    await expect(adminPage.getByTestId("record-journey")).toBeVisible();
    await expect(adminPage.getByTestId("record-milestones")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-dispatched")).toContainText("not yet");
    await expect(adminPage.getByTestId("milestone-landed")).toContainText("not yet");
    await expect(adminPage.getByTestId("journey-planned")).toBeVisible();
    await expect(adminPage.getByTestId("journey-planned")).toContainText("claimed the OSDU id");
    await expect(adminPage.getByTestId("journey-queued")).toBeVisible();
    await expect(adminPage.getByTestId("record-origin")).toContainText("welllog_");

    // The chain before the ledger: the strip has a place for pre-ingestion and ingestion. This estate's chain ran
    // through the CLI rather than as platform runs, so no run recorded the file and the page says exactly that,
    // instead of inventing a stage or leaving a blank.
    await expect(adminPage.getByTestId("milestone-pre")).toBeVisible();
    await expect(adminPage.getByTestId("milestone-ing")).toBeVisible();
    await expect(adminPage.getByTestId("record-chain-note")).toContainText(/No run in the catalog recorded/, { timeout: 30_000 });

    // Removing the one record opens the removal surface, which says the record was never delivered; nothing is sent.
    await adminPage.getByTestId("record-delete").click();
    await expect(adminPage.getByTestId("removal-dialog")).toBeVisible();
    await expect(adminPage.getByTestId("removal-never-delivered")).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("removal-cancel").click();
    await expect(adminPage.getByTestId("removal-dialog")).toHaveCount(0);

    // The record's submission is the batch: what it delivered is its own to remove, and this one delivered nothing.
    await adminPage.getByTestId("record-submission-link").click();
    await expect(adminPage.getByTestId("page-delivery-submission")).toBeVisible();
    await expect(adminPage.getByTestId("submission-delivered-records")).toContainText("(0)", { timeout: 30_000 });
    await expect(adminPage.getByTestId("submission-remove-delivered")).toBeDisabled();

    // Its two record sets are two links: what it delivered (nothing yet), and what it last planned (every record).
    await adminPage.getByTestId("submission-delivered-records").click();
    await expect(adminPage.getByTestId("delivery-records-clear-delivered")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-records-table").getByText(/No records match/)).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-records-clear-delivered").click();
    await expect(adminPage.getByTestId("delivery-records-clear-delivered")).toHaveCount(0);
    await adminPage.goBack();
    await adminPage.goBack();
    await expect(adminPage.getByTestId("page-delivery-submission")).toBeVisible();
    await adminPage.getByTestId("submission-records").click();
    await expect(adminPage.getByTestId("delivery-records-clear-submission")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-records-table").getByTestId("table-row").first()).toBeVisible({ timeout: 30_000 });

    // Records has a place in the navigation of its own, and starts empty until something is typed.
    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-lookup-empty")).toBeVisible();
  });
});
