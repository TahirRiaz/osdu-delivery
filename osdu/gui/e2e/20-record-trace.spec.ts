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

    // The chain before the ledger, named run by run. Pre-ingestion is found by the file it processed; ingestion
    // processed no file of its own (it reads the table the pre flow landed), so it is found by the table it was
    // writing when the row was stamped. Both are the estate's own runs, recorded as they ran.
    await expect(adminPage.getByTestId("milestone-pre")).toContainText("wells-welllog-01-header-pre", { timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-ing")).toContainText("wells-welllog-02-header-ing");
    await expect(adminPage.getByTestId("milestone-pre")).not.toContainText("no run recorded");
    await expect(adminPage.getByTestId("milestone-ing")).not.toContainText("no run recorded");

    // Each stage is in the timeline too, saying what it did: the file it took in, and the table it loaded the row into.
    const journey = adminPage.getByTestId("record-journey");
    await expect(journey).toContainText("Landed: wells-welllog-01-header-pre took the file in");
    await expect(journey).toContainText("Ingested: wells-welllog-02-header-ing loaded the row into its table");

    // With every stage named there is nothing missing to explain, so the note is not rendered at all.
    await expect(adminPage.getByTestId("record-chain-note")).toHaveCount(0);

    // Every long value of the header is clipped to its own column and copyable: a staged payload's path is longer
    // than the column it sits in, and a value wider than its cell used to run under the value beside it.
    const location = adminPage.getByTestId("copy-record-payload-location");
    await expect(location).toBeVisible();
    const fits = await adminPage.evaluate(() => Array.from(document.querySelectorAll("span[style*='max-width']"))
      .every((span) => {
        const parent = span.parentElement;
        return parent === null || span.getBoundingClientRect().width <= parent.getBoundingClientRect().width + 1;
      }));
    expect(fits).toBe(true);

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

    // Records has a place in the navigation of its own, and opens on what the delivery system last took in rather
    // than on an empty page: the newest records of every flow, each one a click from its own journey.
    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-lookup-search")).toHaveValue("");
    await expect(adminPage.getByTestId("delivery-lookup-caption")).toBeVisible();
    const latest = adminPage.getByTestId("delivery-lookup-table").getByTestId("table-row");
    await expect(latest.first()).toBeVisible({ timeout: 30_000 });

    // Nothing was typed, so no row claims a value matched it.
    await expect(latest.first().getByTestId("lookup-matched")).toHaveCount(0);
    await latest.first().click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();
    await expect(adminPage.getByTestId("record-journey")).toBeVisible();
    await expect(adminPage.getByTestId("record-milestones")).toBeVisible({ timeout: 30_000 });
  });
});
