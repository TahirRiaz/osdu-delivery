import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";
import type { APIRequestContext } from "@playwright/test";

// The search terms (osdu/docs/search-terms.md): the columns of the source systems the mappings of active delivery flows
// read, extracted by the seed's repository sync from the sample estate's WellLog mapping. The Search terms page lists
// them with the property each fills, and refines one: renamed, noted, left out of the explorer and offered again. The
// explorer offers the terms beside the record's own properties: a value typed as Recall holds it is carried through the
// mapping, here through the wellbore search the mapping makes, and asked of OSDU's own search like any condition.
//
// Nothing here reaches an OSDU: the explorer reads the e2e stand-in, which holds the platform's wellbores and the two
// well logs this spec gives it for as long as it runs. What the spec makes of a term is reset before and after it, so
// the suite's database meets every run the same.

const PARTITION = E2E.osdu.OSDU_DATA_PARTITION;
const LOG_TYPE = "work-product-component--WellLog";

/** The two logs this spec holds in the stand-in, each naming a wellbore the stand-in holds. */
const LOGS = [
  { id: `${PARTITION}:${LOG_TYPE}:e2e-terms-log-a`, name: "e2e terms log A", wellbore: "NO-33-9-C-28-A" },
  { id: `${PARTITION}:${LOG_TYPE}:e2e-terms-log-b`, name: "e2e terms log B", wellbore: "NO-33-9-C-28-B" },
];

const TERMS_API = `${E2E.apiBaseUrl}/api/v1/delivery/search-terms`;

interface TermRow {
  id: string;
  column: string;
}

/** The id of the WellLog term of the Recall column `column`, as the seed's sync extracted it. */
async function termId(request: APIRequestContext, column: string): Promise<string> {
  const session = await adminSession(request);
  const response = await request.get(`${TERMS_API}?entityType=${LOG_TYPE}`, { headers: { Authorization: `Bearer ${session.token}` } });
  expect(response.ok(), await response.text()).toBe(true);
  const body = (await response.json()) as { terms: TermRow[] };
  const term = body.terms.find((candidate) => candidate.column === column);
  expect(term, `the seed's sync extracts the WellLog term ${column}`).toBeDefined();
  return term!.id;
}

/** Gives the term back as the mappings give it: no name, no note, offered, its likeliest route. */
async function resetTerm(request: APIRequestContext, id: string): Promise<void> {
  const session = await adminSession(request);
  const response = await request.delete(`${TERMS_API}/${id}/refinement`, { headers: { Authorization: `Bearer ${session.token}` } });
  expect([204, 404], await response.text()).toContain(response.status());
}

test.describe.serial("search terms", () => {
  let uwi = "";

  test.beforeAll(async ({ playwright, request }) => {
    uwi = await termId(request, "wellbore_uwi");
    await resetTerm(request, uwi);
    const standIn = await playwright.request.newContext();
    try {
      for (const log of LOGS) {
        const held = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/records`, {
          data: {
            id: log.id,
            kind: `osdu:wks:${LOG_TYPE}:1.4.0`,
            acl: { viewers: [E2E.osdu.OSDU_ACL_VIEWER], owners: [E2E.osdu.OSDU_ACL_OWNER] },
            legal: { legaltags: [E2E.osdu.OSDU_LEGAL_TAG], otherRelevantDataCountries: ["NO"], status: "compliant" },
            data: { Name: log.name, WellboreID: `${PARTITION}:master-data--Wellbore:${log.wellbore}:` },
            createUser: "e2e-stand-in",
            createTime: "2026-10-08T00:00:00.000Z",
          },
        });
        expect(held.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright, request }) => {
    if (uwi !== "") {
      await resetTerm(request, uwi);
    }

    // The stand-in forgets the records this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/records`);
    } finally {
      await standIn.dispose();
    }
  });

  test("lists the columns the delivery flows read, and refines one: renamed, left out of the explorer, offered again", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-search-terms").click();
    await expect(adminPage.getByTestId("page-delivery-search-terms")).toBeVisible();
    await adminPage.goto(`/delivery/search-terms?type=${LOG_TYPE}`);

    // The Recall columns the WellLog mapping reads, each with the property it fills and how. The grid draws the rows in
    // view, so a term is found by the page's own find, by its column.
    const grid = adminPage.getByTestId("search-terms-grid");
    const find = adminPage.getByTestId("search-terms-find");
    await expect(grid.getByTestId("search-terms-grid-row").first()).toBeVisible({ timeout: 60_000 });
    await find.fill("log_source");
    await expect(grid.getByTestId("search-terms-grid-row").filter({ hasText: /^log_source/ })).toBeVisible();
    await find.fill("wellbore_uwi");
    const row = grid.getByTestId("search-terms-grid-row").filter({ hasText: "wellbore_uwi" });
    await expect(row).toContainText("WellboreID");
    await expect(row).toContainText("search");
    await expect(row).toContainText("Searched");

    // Renamed to what the people searching call it, with a note: the name is the term's from here on.
    await row.click();
    const sheet = adminPage.getByTestId("search-term-sheet");
    await expect(sheet.getByTestId("search-term-routes")).toContainText("Wellbore by FacilityName");
    await sheet.getByTestId("search-term-name").fill("Wellbore UWI");
    await sheet.getByTestId("search-term-note").fill("The wellbore's name as Recall keeps it.");
    await sheet.getByTestId("search-term-save").click();
    const renamed = grid.getByTestId("search-terms-grid-row").filter({ hasText: "Wellbore UWI" });
    await expect(renamed).toContainText("wellbore_uwi", { timeout: 30_000 });
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toBeHidden();

    // Left out, it is listed as such, and the explorer no longer offers it.
    await renamed.click();
    await sheet.getByTestId("search-term-offered").click();
    await sheet.getByTestId("search-term-save").click();
    await expect(renamed).toContainText("Left out", { timeout: 30_000 });
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toBeHidden();
    await adminPage.getByTestId("search-terms-explore").click();
    await expect(adminPage.getByTestId("explorer-grid").getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
    await adminPage.getByTestId("explorer-add-filter").click();
    await expect(adminPage.getByTestId("explorer-filter-attributes-term").first()).toBeVisible({ timeout: 60_000 });
    await expect(adminPage.locator(`[data-testid="explorer-filter-attributes-term"][data-term="${uwi}"]`)).toHaveCount(0);
    await adminPage.keyboard.press("Escape");

    // Offered again.
    await adminPage.goto(`/delivery/search-terms?type=${LOG_TYPE}`);
    await find.fill("wellbore_uwi");
    await renamed.click();
    await sheet.getByTestId("search-term-offered").click();
    await sheet.getByTestId("search-term-save").click();
    await expect(renamed).toContainText("Searched", { timeout: 30_000 });
    await adminPage.keyboard.press("Escape");
    await expect(sheet).toBeHidden();
  });

  test("searches the explorer by a source column, its value as Recall holds it, beside a property", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?kind=*:*:${LOG_TYPE}:*`);
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });

    // Filter lists the source columns before the properties, by the names given them; the properties are all there still.
    await adminPage.getByTestId("explorer-add-filter").click();
    const attributes = adminPage.getByTestId("explorer-filter-attributes");
    const term = attributes.locator(`[data-testid="explorer-filter-attributes-term"][data-term="${uwi}"]`);
    await expect(term).toContainText("Wellbore UWI", { timeout: 60_000 });
    await expect(attributes.locator('[data-testid="explorer-filter-attributes-field"][data-path="data.WellboreID"]')).toHaveCount(1);
    await term.click();

    // The term's values are the wellbores' names, which the mapping finds a wellbore by: one picked is the condition.
    const editor = adminPage.getByTestId("explorer-filter-editor");
    await expect(editor.getByTestId("explorer-filter-term")).toHaveText("Wellbore UWI");
    await expect(editor.getByTestId("explorer-filter-hint")).toContainText("Typed as the source column holds it");
    await editor.getByTestId("explorer-filter-held-value").filter({ hasText: "NO 33/9-C-28 B" }).click({ timeout: 60_000 });
    await expect(editor.getByTestId("explorer-filter-value")).toHaveValue("NO 33/9-C-28 B");
    await editor.getByTestId("explorer-filter-apply").click();

    // The wellbore the name finds is read first, and the log naming it is the one listed: OSDU's search answers both.
    const chips = adminPage.getByTestId("explorer-filter");
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("Wellbore UWI is NO 33/9-C-28 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("e2e terms log B");
    await adminPage.getByTestId("explorer-sent-toggle").click();
    await expect(adminPage.getByTestId("explorer-sent-query")).toContainText(`${PARTITION}:master-data--Wellbore:NO-33-9-C-28-B:`);
    await adminPage.keyboard.press("Escape");
    await expect(adminPage.getByTestId("explorer-notes")).toBeVisible();

    // The chip opens in the term's editor again, and the condition is the page's address.
    await chips.first().getByTestId("explorer-filter-edit").click();
    await expect(editor.getByTestId("explorer-filter-term")).toHaveText("Wellbore UWI", { timeout: 30_000 });
    await adminPage.keyboard.press("Escape");
    await adminPage.reload();
    await expect(chips.first()).toContainText("Wellbore UWI is NO 33/9-C-28 B", { timeout: 60_000 });
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });

    // A property's condition holds beside the term's, as before: no log B is named A.
    await adminPage.getByTestId("explorer-add-filter").click();
    await attributes.getByTestId("explorer-filter-attributes-find").fill("data.Name");
    await attributes.locator('[data-testid="explorer-filter-attributes-field"][data-path="data.Name"]').click({ timeout: 60_000 });
    await editor.getByTestId("explorer-filter-value").fill("log A");
    await editor.getByTestId("explorer-filter-apply").click();
    await expect(chips).toHaveCount(2);
    await expect(adminPage.getByTestId("explorer-empty")).toBeVisible({ timeout: 60_000 });
    await adminPage.getByTestId("explorer-filters-clear").click();
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });
  });

  test("searches in a source column picked at the field's start, its values those of the records it finds the record by", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?kind=*:*:${LOG_TYPE}:*`);
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });

    // The column is picked among the type's source columns, before its properties, by the name given it.
    await adminPage.getByTestId("explorer-search-scope").click();
    await adminPage.locator(`[data-testid="explorer-search-scope-attributes-term"][data-term="${uwi}"]`).click({ timeout: 60_000 });
    await expect(adminPage.getByTestId("explorer-search-scope-name")).toHaveText("Wellbore UWI");
    const field = adminPage.getByTestId("explorer-search-input");
    await expect(field).toHaveAttribute("placeholder", "Type a Wellbore UWI, as the source holds it");

    // As a value is typed, the values listed are the wellbores' names, which the mapping finds a wellbore by.
    await field.fill("NO 33/9-C-28");
    const options = adminPage.getByTestId("explorer-search-in");
    await expect(options.getByTestId("explorer-search-in-scoped")).toContainText("Wellbore UWI is NO 33/9-C-28");
    await options.getByTestId("explorer-search-in-held").filter({ hasText: "NO 33/9-C-28 B" }).click({ timeout: 60_000 });
    const chips = adminPage.getByTestId("explorer-filter");
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("Wellbore UWI is NO 33/9-C-28 B");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("e2e terms log B");

    // Another value searched in the column replaces the condition it had.
    await field.fill("NO 33/9-C-28 A");
    await field.press("Enter");
    await expect(chips).toHaveCount(1);
    await expect(chips.first()).toContainText("Wellbore UWI is NO 33/9-C-28 A");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("e2e terms log A");
  });

  test("searches text typed in the search box in a source column searched in lately", async ({ adminPage }) => {
    // The browser remembers the term as searched in for the type, as it does a property.
    await adminPage.evaluate(([type, remembered]) => {
      window.localStorage.setItem("sqlflow.osdu.explorer.searchedIn", JSON.stringify({ [type]: [remembered] }));
    }, [LOG_TYPE, `term:${uwi}`]);
    await adminPage.goto(`/delivery/explorer?kind=*:*:${LOG_TYPE}:*`);
    const grid = adminPage.getByTestId("explorer-grid");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(2, { timeout: 60_000 });

    const within = adminPage.getByTestId("explorer-search-input");
    await within.fill("NO 33/9-C-28 A");
    const options = adminPage.getByTestId("explorer-search-in");
    const option = options.getByTestId("explorer-search-in-term");
    await expect(option).toContainText("Wellbore UWI is NO 33/9-C-28 A", { timeout: 60_000 });
    await expect(options.getByTestId("explorer-search-in-choose")).toContainText("Pick another property or source column");
    await option.click();

    const chips = adminPage.getByTestId("explorer-filter");
    await expect(chips.first()).toContainText("Wellbore UWI is NO 33/9-C-28 A");
    await expect(within).toHaveValue("");
    await expect(grid.getByTestId("explorer-grid-row")).toHaveCount(1, { timeout: 60_000 });
    await expect(grid).toContainText("e2e terms log A");
  });
});
