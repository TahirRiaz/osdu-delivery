import { DELIVERY_FLOW } from "./global-setup";
import { expect, test } from "./helpers";

// Traceability from the product's front door (CLAUDE.md: traceability is the product). An operator holding a source key,
// an OSDU id or a file name finds the record without knowing which flow delivered it, and the record's page tells its
// journey: when the row was received, when it was planned, every dispatch and what OSDU answered, and where it stands.
// A batch is undoable from its submission's page, aimed at what that submission delivered.
//
// The intake of the records CLI spec staged the Recall well logs of the STAT_COMP scope, so the ledger holds pending
// records with a queued document and no dispatch yet; nothing here reaches an OSDU, and no removal is run.

test.describe.serial("record trace", () => {
  test("a record is found from what an operator holds, and its page tells its journey", async ({ adminPage }) => {
    // The Delivery page's field opens the Records page looked up for the term.
    await adminPage.getByTestId("nav-delivery").click();
    await expect(adminPage.getByTestId("page-delivery")).toBeVisible();
    // A source key is the source system and the row's key columns (recall:NORWAY_WELLDB/12359/1: the Recall project and
    // the log id); the lookup is a prefix.
    await adminPage.getByTestId("delivery-find-record-term").fill("recall:NORWAY_WELLDB");
    await adminPage.getByTestId("delivery-find-record-go").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await expect(adminPage).toHaveURL(/q=recall%3ANORWAY_WELLDB/);
    await expect(adminPage.getByTestId("delivery-lookup-search")).toHaveValue("recall:NORWAY_WELLDB");

    // The lookup lists every record whose source key starts with the term, across flows, each naming its flow.
    const table = adminPage.getByTestId("delivery-lookup-table");
    await expect(table).toBeVisible();
    const rows = table.getByTestId("table-row");
    await expect(rows.first()).toBeVisible({ timeout: 30_000 });
    await expect(rows.first()).toContainText("NORWAY_WELLDB");
    await expect(rows.first()).toContainText(DELIVERY_FLOW);

    // Every row says which of its values answered, so a hit explains itself.
    await expect(rows.first().getByTestId("lookup-matched")).toBeVisible();

    // The wellbore name the mapping declares as an identity finds it, which is what an operator actually holds. The
    // Recall log id does too, and neither is the start of the source key. The address carries each term encoded.
    for (const [term, url] of [["NO 33/9-C-28 B", /q=NO(\+|%20)33%2F9-C-28(\+|%20)B/], ["12359/1", /q=12359%2F1/]] as const) {
      await adminPage.getByTestId("delivery-lookup-search").fill(term);
      await expect(adminPage).toHaveURL(url);
      await expect(rows.first()).toBeVisible({ timeout: 30_000 });
      await expect(rows.first().getByTestId("lookup-matched")).toContainText(term);
    }

    await adminPage.getByTestId("delivery-lookup-search").fill("recall:NORWAY_WELLDB");
    await expect(adminPage).toHaveURL(/q=recall%3ANORWAY_WELLDB/);
    await expect(rows.first()).toContainText("NORWAY_WELLDB", { timeout: 30_000 });

    // A state narrows the same lookup; a state nothing is in leaves it empty rather than wrong.
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "deleted" }).click();
    await expect(adminPage).toHaveURL(/status=deleted/);
    await expect(table.getByText(/No record starts with that/)).toBeVisible({ timeout: 30_000 });
    await adminPage.getByTestId("delivery-lookup-status").click();
    await adminPage.getByRole("option", { name: "pending" }).click();
    // The empty answer to the last state stays on screen until this one arrives, so the row is waited for by what it holds.
    await expect(adminPage).toHaveURL(/status=pending/);
    await expect(rows.first()).toContainText("NORWAY_WELLDB", { timeout: 30_000 });

    // A row opens the record, whose timeline starts with where it stands now, the queued document, and tells the one
    // chapter it has lived so far: its row arrived, the ledger picked it up and reserved its OSDU id, nothing was sent.
    await rows.first().click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();
    await expect(adminPage.getByTestId("record-journey")).toBeVisible();
    await expect(adminPage.getByTestId("record-milestones")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-landed")).toContainText("not yet");
    await expect(adminPage.getByTestId("journey-now")).toContainText("Queued to send");
    await expect(adminPage.getByTestId("journey-planned")).toBeVisible();
    await expect(adminPage.getByTestId("journey-planned")).toContainText("Picked up for delivery");
    await expect(adminPage.getByTestId("journey-planned")).toContainText("OSDU id reserved for this flow");
    await expect(adminPage.getByTestId("journey-chapter")).toHaveCount(1);
    await expect(adminPage.getByTestId("journey-chapter-trigger")).toContainText("Row arrived in arc.WellLog");
    await expect(adminPage.getByTestId("journey-chapter-outcome")).toContainText("Queued to send");

    // The row's arrival, before the ledger: the file it came in from and when it reached the ingestion table. They are
    // the record's own facts; the runs that made them are their evidence, found by the file the landing processed and
    // by the table the ingestion run was writing when it inserted the row.
    await expect(adminPage.getByTestId("milestone-file")).toContainText("welllog_", { timeout: 30_000 });
    await expect(adminPage.getByTestId("milestone-file")).not.toContainText("not recorded");
    await expect(adminPage.getByTestId("milestone-loaded")).toContainText("arc.WellLog");
    await expect(adminPage.getByTestId("milestone-changed")).toContainText("unchanged since it was loaded");

    // The timeline holds the changes of this record's row, not the pipeline's runs: the landing that brought its file
    // in, and the load that inserted the row, which names the ingestion run that wrote it.
    const journey = adminPage.getByTestId("record-journey");
    await expect(journey.getByTestId("journey-source-landing")).toContainText("File landed by recall-welllog-01-header-pre");
    await expect(journey.getByTestId("journey-source-loaded")).toContainText("Row loaded into arc.WellLog");
    await expect(journey.getByTestId("journey-source-loaded")).toContainText("written by");
    await expect(journey.getByTestId("journey-source-changed")).toHaveCount(0);

    // With every run named there is nothing missing to explain, so the note is not rendered at all.
    await expect(adminPage.getByTestId("record-chain-note")).toHaveCount(0);

    // An entry opens to the rest of what is known about it: the ingestion run that wrote the row, and the landing.
    await journey.getByTestId("journey-source-loaded").getByTestId("journey-event-toggle").click();
    const detail = journey.getByTestId("journey-source-loaded").getByTestId("journey-event-detail");
    await expect(detail).toContainText("recall-welllog-02-header-ing");
    await expect(detail).toContainText("recall-welllog-01-header-pre");

    // The Source tab names the ingestion file and row the record was staged from. Every long value it shows is clipped
    // to its own column and copyable: a value wider than its cell used to run under the value beside it.
    await adminPage.getByTestId("record-tab-source").click();
    await expect(adminPage.getByTestId("record-origin")).toContainText("welllog_");
    await expect(adminPage.getByTestId("copy-record-origin")).toBeVisible();
    const fits = await adminPage.evaluate(() => Array.from(document.querySelectorAll("span[style*='max-width']"))
      .every((span) => {
        const parent = span.parentElement;
        return parent === null || span.getBoundingClientRect().width <= parent.getBoundingClientRect().width + 1;
      }));
    expect(fits).toBe(true);

    // The Render tab sets the mapping and cache that rendered the delivered document beside those a render uses now;
    // this record was never delivered, so it says so, and offers the render.
    await adminPage.getByTestId("record-tab-render").click();
    await expect(adminPage.getByTestId("record-render-delivered-mapping")).toContainText("not delivered");
    await expect(adminPage.getByTestId("record-render-run")).toBeEnabled();

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
