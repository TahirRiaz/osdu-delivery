import { adminSession, expect, FIRST_PAGE_MS, seedSession, test } from "./helpers";

// The OSDU group (osdu/docs/reference/concepts/gui.md, The OSDU menu): one group of the side bar, its entries under
// four labelled sections in the order an operator reaches for them, and the mapping builder reached from Mappings
// rather than listed. The side bar starts closed on a first visit, and the person's choice stands from then on. Nothing
// here needs the seeded estate: the navigation is the module's, whatever the catalog holds.

const SECTIONS = [
  { id: "ledger", label: "Ledger", entries: ["nav-delivery", "nav-delivery-records", "nav-delivery-activity"] },
  {
    id: "in-osdu",
    label: "In OSDU",
    entries: ["nav-delivery-explorer", "nav-delivery-assertions", "nav-delivery-inventories", "nav-delivery-dimensions"],
  },
  { id: "build", label: "Build", entries: ["nav-delivery-documents", "nav-delivery-templates", "nav-delivery-cache"] },
  { id: "setup", label: "Setup", entries: ["nav-delivery-partitions", "nav-delivery-search-terms"] },
];

test.describe("OSDU navigation", () => {
  test("the side bar starts closed on a first visit, and once opened it starts open", async ({ page, request }) => {
    // A browser that holds no choice: signed in, and nothing else seeded.
    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.getByTestId("activity-osdu")).toBeVisible();
    await expect(page.locator("#sidebar-section-osdu")).toHaveCount(0);

    await page.getByTestId("activity-osdu").click();
    await expect(page.locator("#sidebar-section-osdu")).toBeVisible();

    await page.reload();
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.locator("#sidebar-section-osdu")).toBeVisible();
  });

  test("the OSDU group lists its entries under its four sections, in order, and the builder under none", async ({ adminPage }) => {
    const group = adminPage.locator("#sidebar-section-osdu");
    await expect(group).toBeVisible();

    const parts = await group.locator("[data-testid^='sidebar-part-osdu-']").evaluateAll(
      (elements) => elements.map((element) => element.getAttribute("data-testid")),
    );
    expect(parts).toEqual(SECTIONS.map((section) => `sidebar-part-osdu-${section.id}`));

    for (const section of SECTIONS) {
      const part = group.getByRole("group", { name: `OSDU: ${section.label}` });
      await expect(part).toContainText(section.label);
      const entries = await part.locator("[data-testid^='nav-']").evaluateAll(
        (elements) => elements.map((element) => element.getAttribute("data-testid")),
      );
      expect(entries).toEqual(section.entries);
    }

    await expect(adminPage.getByTestId("nav-delivery-mapping-builder")).toHaveCount(0);
  });

  test("the command palette heads each section by the group and the section", async ({ adminPage }) => {
    await adminPage.keyboard.press("Control+k");
    const palette = adminPage.getByRole("dialog");
    await expect(palette).toBeVisible();
    for (const section of SECTIONS) {
      await expect(palette.getByText(`OSDU · ${section.label}`, { exact: true })).toBeVisible();
    }

    await palette.getByRole("combobox").fill("build cache");
    await expect(palette.getByRole("option", { name: "Cache" })).toBeVisible();
  });

  test("the builder is opened from Mappings, which stays lit while it is shown", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-documents").click();
    await adminPage.getByTestId("delivery-documents-new-mapping").click();
    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("nav-delivery-documents")).toHaveAttribute("aria-current", "page");
    await expect(adminPage.getByTestId("nav-delivery")).not.toHaveAttribute("aria-current", "page");
    await expect(adminPage.getByRole("tab", { name: /Mapping builder/ })).toBeVisible();
  });
});
