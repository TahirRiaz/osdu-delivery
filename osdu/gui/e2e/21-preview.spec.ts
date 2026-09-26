import { E2E } from "../playwright.config";
import { DELIVERY_FLOW, LOG_SOURCE } from "./global-setup";
import { expect, test } from "./helpers";

// A record before it is sent, and a record as OSDU holds it. The Preview tab of a delivery flow renders one record on a
// node exactly as a delivery would and sends nothing: the scope's first record, or the one a key names. A record's page
// reads the record from OSDU through its flow's route, follows what it refers to, and compares what OSDU holds with what
// a delivery would send now.
//
// Nothing here reaches an OSDU: the flows reach the e2e stand-in, which answers the wellbore searches a render makes and
// the record reads, and holds the one well log this spec gives it for as long as the spec runs. The records the earlier
// specs staged are the ones read here; nothing is delivered and nothing is written to the ledger.

/** A Recall well log of the sample estate: its source key as the Records page shows it, and the wellbore it names. */
const LOG_KEY = "recall:NORWAY_WELLDB/12359/1";
const LOG_WELLBORE = "NO-33-9-C-28-B";

test.describe.serial("record preview and OSDU read", () => {
  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets the record this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("the Preview tab renders the scope's first record and a named one, and says when a key names none", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill(DELIVERY_FLOW);
    await adminPage.getByTestId("repo-pipeline").filter({ hasText: DELIVERY_FLOW }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /preview/i }).click();
    await expect(adminPage.getByTestId("delivery-panel-preview")).toBeVisible();

    // The flow's scope needs its log source, which it declares required with no default: without it there is no preview.
    const run = adminPage.getByTestId("preview-run");
    await expect(adminPage.getByTestId("preview-parameter-logSource")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("preview-missing")).toContainText("logSource");
    await expect(run).toBeDisabled();
    await adminPage.getByTestId("preview-parameter-logSource").fill(LOG_SOURCE);
    await expect(run).toBeEnabled();

    // No key: the scope's first record, rendered on a node as a delivery would render it.
    await run.click();
    const result = adminPage.getByTestId("preview-result");
    await expect(result).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-asked")).toContainText("the first record of the scope");
    await expect(adminPage.getByTestId("preview-action")).toBeVisible();
    await expect(adminPage.getByTestId("preview-target")).toContainText("work-product-component--WellLog");
    await expect(adminPage.getByTestId("preview-document")).toContainText("work-product-component--WellLog", { timeout: 30_000 });

    // The route's requests in order, and the parquet chunk the well log's bulk data is, measured by its footer.
    await adminPage.getByTestId("preview-tab-steps").click();
    await expect(adminPage.getByTestId("preview-step").first()).toContainText("DDMS");
    await adminPage.getByTestId("preview-tab-payload").click();
    await expect(adminPage.getByTestId("preview-payload-files")).toContainText(".parquet");
    await expect(adminPage.getByTestId("preview-payload-files")).toContainText(/rows x \d+ columns/);

    // What the document refers to: the wellbore the render found by searching the platform, and the reference data the
    // cache gave it, none of them records of this ledger.
    await adminPage.getByTestId("preview-tab-references").click();
    await expect(adminPage.getByTestId("preview-references")).toContainText(/master-data--Wellbore:NO-[0-9A-Z-]+data\.WellboreID/);
    await expect(adminPage.getByTestId("preview-references")).toContainText("not a record of the ledger");

    // A key as the Records page shows it names that record, though its log id holds a slash.
    await adminPage.getByTestId("preview-key").fill(LOG_KEY);
    await run.click();
    await expect(adminPage.getByTestId("preview-asked")).toContainText("read as a source key", { timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-header")).toContainText("12359/1");

    // A key the table holds no row for is an answer, not a failure.
    await adminPage.getByTestId("preview-key").fill("NORWAY_WELLDB/no-such-log");
    await run.click();
    await expect(adminPage.getByTestId("preview-not-found")).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("preview-reason")).toContainText("holds no row");
  });

  test("a record's page reads it from OSDU, follows what it refers to, and compares it with what a delivery would send", async ({ adminPage, request }) => {
    // The lookup finds the record by the source key an operator holds; its row goes straight to what OSDU holds.
    await adminPage.getByTestId("nav-delivery-records").click();
    await expect(adminPage.getByTestId("page-delivery-records")).toBeVisible();
    await adminPage.getByTestId("delivery-lookup-search").fill(LOG_KEY);
    // The lookup writes its term into the address once typing settles; a click before that is taken back to the lookup.
    await expect(adminPage).toHaveURL(/q=recall%3ANORWAY_WELLDB%2F12359%2F1/);
    const row =adminPage.getByTestId("delivery-lookup-table").getByTestId("table-row").filter({ hasText: "12359/1" }).first();
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.getByTestId("open-in-osdu").click();
    await expect(adminPage.getByTestId("page-delivery-record")).toBeVisible();

    // The page opened on its In OSDU tab reads the record at once. Nothing was delivered, so OSDU holds none.
    await expect(adminPage.getByTestId("record-tab-osdu")).toHaveAttribute("data-state", "active");
    await expect(adminPage.getByTestId("osdu-not-found")).toBeVisible({ timeout: 60_000 });

    // Once OSDU holds the record, the read shows it: its version, its access, and the wellbore it refers to.
    const chip = await adminPage.getByTestId("record-target").textContent();
    const targetId = /(\S+:work-product-component--WellLog:[0-9a-f]{32})/.exec(chip ?? "")?.[1];
    expect(targetId, `the record page names the OSDU id: ${chip}`).toBeDefined();
    const partition = targetId!.split(":")[0];
    const hold = await request.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
      data: {
        id: targetId,
        kind: "osdu:wks:work-product-component--WellLog:1.4.0",
        acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
        legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["NO"], status: "compliant" },
        data: { Name: "held by the stand-in", WellboreID: `${partition}:master-data--Wellbore:${LOG_WELLBORE}:` },
        createUser: "e2e-stand-in",
        createTime: "2026-09-25T00:00:00.000Z",
      },
    });
    expect(hold.ok()).toBe(true);

    await adminPage.getByTestId("record-osdu-read").click();
    await expect(adminPage.getByTestId("osdu-record")).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("osdu-record-viewers")).toContainText(E2E.osdu.OSDU_ACL_VIEWER);
    await expect(adminPage.getByTestId("osdu-record-json")).toContainText("held by the stand-in", { timeout: 30_000 });

    // The versions OSDU keeps of it ride with the read: the stand-in keeps one, which is the latest and the one shown,
    // so it is marked rather than offered to read.
    const versions = adminPage.getByTestId("osdu-record-versions");
    await expect(versions).toContainText("latest");
    await expect(versions.getByTestId("osdu-record-version")).toHaveAttribute("data-state", "active");
    await expect(versions.getByTestId("osdu-version")).toHaveCount(0);

    // The wellbore it refers to is a link where it stands in the record, read in turn through the same flow's route,
    // and opens beneath it.
    const link = adminPage.getByTestId("osdu-record-link").first();
    await expect(link).toContainText(`master-data--Wellbore:${LOG_WELLBORE}`);
    await link.getByTestId("osdu-link-read").click();
    const linked = adminPage.getByTestId("osdu-linked");
    await expect(linked).toBeVisible();
    await expect(linked.getByTestId("osdu-record-json")).toContainText("FacilityName", { timeout: 60_000 });
    await linked.getByTestId("osdu-linked-close").click();
    await expect(linked).toHaveCount(0);

    // Compare: what OSDU holds beside what a delivery would send now, rendered afresh from the record's source row.
    await adminPage.getByTestId("record-tab-compare").click();
    await adminPage.getByTestId("record-compare-run").click();
    await expect(adminPage.getByTestId("record-compare-diff")).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.getByTestId("record-compare-counts")).toContainText(/changed|would be added|only in OSDU/);
    await expect(adminPage.getByTestId("record-compare-differences")).toContainText("data.Name");
    await expect(adminPage.getByTestId("record-compare-summary")).toBeVisible();
  });
});
