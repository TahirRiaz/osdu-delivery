import { expect, test } from "./helpers";

// A source that delivers several interfaces (docs/interfaces-design.md section 9). Each interface has its own ledger,
// so its records, submissions and counts are never summed with another's: every view of the source is about one of
// them, and the source's page says which, lists them in the order a run takes them, and lets an operator switch.

test.describe.serial("interfaces", () => {
  test("a source lists its interfaces, and its records are read one interface at a time", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await adminPage.getByTestId("filter-name").fill("recall-source");
    await adminPage.getByTestId("table-row").filter({ hasText: "recall-source" }).first().click();
    await expect(adminPage.getByTestId("page-pipeline-detail")).toBeVisible();

    // The Delivery tab opens on the source: the counts of every interface, then the interfaces themselves.
    await expect(adminPage.getByTestId("delivery-stats")).toBeVisible({ timeout: 30_000 });
    const interfaces = adminPage.getByTestId("delivery-interfaces-table");
    await expect(interfaces).toBeVisible();
    await expect(interfaces.getByText("wellbores")).toBeVisible();
    await expect(interfaces.getByText("welllogs")).toBeVisible();
    // The well logs declare `after: [wellbores]`, so they run after them and the table says what each waits for.
    const logs = interfaces.getByTestId("table-row").filter({ hasText: "welllogs" });
    await expect(logs).toContainText("WellLog@1.4.0");
    await expect(logs).toContainText("wellbores");
    await expect(interfaces.getByTestId("table-row").filter({ hasText: "wellbores" }).first()).toContainText("Wellbore@1.0.0");

    // The picker names the interface every other view is about.
    const picker = adminPage.getByTestId("delivery-interface-picker");
    await expect(picker).toBeVisible();
    await expect(adminPage.getByTestId("delivery-interface-select")).toContainText("wellbores");

    // The records of a source are one interface's records: the list answers, rather than asking which interface.
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /records/i }).click();
    await expect(adminPage.getByTestId("delivery-records-search")).toBeVisible();
    await expect(adminPage.getByTestId("delivery-records-table")).toBeVisible();
    await expect(adminPage.getByText(/name the one this request is about/i)).toHaveCount(0);

    // Switching the interface carries the choice in the URL, so the view is a link an operator can send on.
    await adminPage.getByTestId("delivery-interface-select").click();
    await adminPage.getByRole("option", { name: "welllogs" }).click();
    await expect(adminPage).toHaveURL(/interface=welllogs/);
    await expect(adminPage.getByTestId("delivery-records-table")).toBeVisible();
    await expect(adminPage.getByText(/name the one this request is about/i)).toHaveCount(0);

    // The submissions of that interface answer the same way.
    await adminPage.getByTestId("pipeline-tabs").getByRole("tab", { name: /submissions/i }).click();
    await expect(adminPage.getByTestId("delivery-submissions-table")).toBeVisible();
  });
});
