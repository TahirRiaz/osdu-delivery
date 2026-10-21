import { adminSession, expect, FIRST_PAGE_MS, seedSession, test } from "./helpers";

// The OSDU group (osdu/docs/reference/concepts/gui.md, The OSDU menu): one group of the side bar, its entries under
// four labelled sections in the order an operator reaches for them, each folding on its own, and the mapping builder
// reached from Mappings rather than listed. The side bar starts closed on a first visit, its menu folded to its headers,
// and the person's choices stand from then on. Nothing
// here needs the seeded estate: the navigation is the module's, whatever the catalog holds.

const SECTIONS = [
  { id: "ledger", label: "Ledger", entries: ["nav-delivery", "nav-delivery-records", "nav-delivery-activity"] },
  {
    id: "in-osdu",
    label: "In OSDU",
    entries: ["nav-delivery-explorer", "nav-delivery-assertions", "nav-delivery-inventories", "nav-delivery-dimensions"],
  },
  { id: "build", label: "Build", entries: ["nav-delivery-documents", "nav-delivery-templates", "nav-delivery-cache", "nav-delivery-order"] },
  { id: "setup", label: "Setup", entries: ["nav-delivery-partitions", "nav-delivery-search-terms"] },
];

test.describe("OSDU navigation", () => {
  test("the side bar starts closed on a first visit with its menu folded, and from then on as it was left", async ({ page, request }) => {
    // A browser that holds no choice: signed in, and nothing else seeded.
    await seedSession(page, await adminSession(request));
    await page.goto("/");
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(page.getByTestId("activity-osdu")).toBeVisible();
    await expect(page.locator("#sidebar-section-osdu")).toHaveCount(0);

    // The activity bar opens the side bar on the group it names, which shows its sections folded; every other group is
    // folded to its header.
    const osdu = page.locator("#sidebar-section-osdu");
    const foldedAsFirstOpened = async () => {
      await expect(osdu).toBeVisible();
      await expect(page.locator("#sidebar-section-osdu > button")).toHaveAttribute("aria-expanded", "true");
      for (const section of SECTIONS) {
        const header = osdu.getByRole("group", { name: `OSDU: ${section.label}` }).getByRole("button", { name: section.label, exact: true });
        await expect(header).toHaveAttribute("aria-expanded", "false");
      }

      await expect(osdu.locator("[data-testid^='nav-']")).toHaveCount(0);
      await expect(page.locator("#sidebar-section-workspace > button")).toHaveAttribute("aria-expanded", "false");
      await expect(page.locator("#sidebar-section-workspace [data-testid^='nav-']")).toHaveCount(0);
    };

    await page.getByTestId("activity-osdu").click();
    await foldedAsFirstOpened();

    await page.reload();
    await expect(page.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await foldedAsFirstOpened();
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

  test("a section folds on its own, and stays as the person left it", async ({ adminPage }) => {
    const group = adminPage.locator("#sidebar-section-osdu");
    const build = group.getByRole("group", { name: "OSDU: Build" });
    const header = build.getByRole("button", { name: "Build", exact: true });
    const entries = build.locator("[data-testid^='nav-']");
    await expect(header).toHaveAttribute("aria-expanded", "true");

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);
    await expect(adminPage.getByTestId("nav-delivery-explorer")).toBeVisible();
    await expect(adminPage.getByTestId("nav-delivery-partitions")).toBeVisible();

    await adminPage.reload();
    await expect(adminPage.getByTestId("page-dashboard")).toBeVisible({ timeout: FIRST_PAGE_MS });
    await expect(header).toHaveAttribute("aria-expanded", "false");
    await expect(entries).toHaveCount(0);

    await header.click();
    await expect(header).toHaveAttribute("aria-expanded", "true");
    expect(await entries.evaluateAll((elements) => elements.map((element) => element.getAttribute("data-testid")))).toEqual(
      SECTIONS.find((section) => section.id === "build")?.entries,
    );
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
