import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { expect, test } from "./helpers";

// Templates and the mapping builder (docs/delivery/mapping-templates.md). Runs after the seed (03), which saves the two
// templates the sample mappings pin and syncs the fixture repository, so both templates are pinned by a synced mapping
// and the repository's cache is in the catalog. Browsing OSDU reads the Open Group's public data definitions repository
// through the control plane, which the suite cannot reach, so the browse test stands in for those three answers and lets
// everything after them (the preview, the save) run against the real control plane.

const WELLBORE_KIND = "osdu:wks:master-data--Wellbore:1.3.0";
const WELLBORE_VERSION = "58d6bdbd9d066a06";
const WELLBORE_FILE = "osdu_wks_master-data--Wellbore_1.3.0.json";

const DATA_DEFINITIONS = "https://community.opengroup.org/osdu/data/data-definitions";
const RELEASE = {
  name: "v0.30.0",
  commit: "99f8fc88d8ad838b5738ac5ad92ac643538b5766",
  publishedUtc: "2026-07-17T06:55:57Z",
  webUrl: `${DATA_DEFINITIONS}/-/tree/v0.30.0/Generated`,
  local: true,
};
const WELLBORE_URL = `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json`;

/** A record schema as the control plane lists it for a release. */
function osduSchema(kind: string, entityType: string, version: string, status: string, path: string) {
  return { kind, entityType, version, status, path, webUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/${path}` };
}
const WELLLOG_KIND = "osdu:wks:work-product-component--WellLog:1.4.0";
const WELLLOG_VERSION = "26a3c3441882db4f";

/** The row of a data table holding the element with the given test id. */
function rowWith(page: Page, table: string, testId: string): Locator {
  return page.getByTestId(table).getByTestId("table-row").filter({ has: page.getByTestId(testId) });
}

async function openTemplates(page: Page): Promise<void> {
  await page.getByTestId("nav-delivery-templates").click();
  await expect(page.getByTestId("page-delivery-templates")).toBeVisible();
}

test.describe.serial("templates and the mapping builder", () => {
  test("the saved templates say which mappings pin them, and a pinned version is not deleted", async ({ adminPage }) => {
    await openTemplates(adminPage);

    const rows = adminPage.getByTestId("templates-saved-table").getByTestId("table-row");
    const wellbore = rows.filter({ hasText: WELLBORE_KIND });
    await expect(wellbore).toHaveCount(1, { timeout: 30_000 });
    await expect(wellbore).toContainText(WELLBORE_VERSION);
    await expect(wellbore).toContainText("1 mapping");
    await expect(rows.filter({ hasText: WELLLOG_KIND })).toContainText(WELLLOG_VERSION);

    await wellbore.click();
    const sheet = adminPage.getByTestId("templates-saved-sheet");
    await expect(sheet.getByTestId("templates-view-kind")).toHaveText(WELLBORE_KIND, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION);
    await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
    await expect(sheet.getByTestId("templates-view-saved")).toContainText("Pinned by 1 synced mapping");

    // The variables a mapping fills are listed; what OSDU Delivery and OSDU write themselves shows only on request.
    // The tree opens fully folded; opening the data section shows what it holds.
    await expect(sheet.getByTestId("templates-view-variable-osdu.data")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toHaveCount(0);
    await sheet.getByTestId("templates-view-variable-osdu.data").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toHaveCount(0);
    await sheet.getByTestId("templates-view-show-minted").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.id")).toBeVisible();

    // Each writer is marked apart from what a mapping fills: a glyph named for the writer, and no marker on a mapping's variable.
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime")).toHaveAttribute("aria-label", "OSDU sets it");
    await expect(sheet.getByTestId("templates-view-role-osdu.createTime").locator("svg")).toHaveCount(1);
    await expect(sheet.getByTestId("templates-view-role-osdu.id")).toHaveAttribute("aria-label", "OSDU Delivery writes it");
    await expect(sheet.getByTestId("templates-view-role-osdu.data.FacilityName")).toHaveCount(0);

    // Selecting a variable in the tree lays out its properties beside it.
    await sheet.getByTestId("templates-view-variable-osdu.id").click();
    await expect(sheet.getByTestId("templates-view-properties-path")).toHaveText("osdu.id");
    await expect(sheet.getByTestId("templates-view-properties-role")).toHaveText("OSDU Delivery writes it");
    await sheet.getByTestId("templates-view-variable-osdu.data.FacilityName").click();
    await expect(sheet.getByTestId("templates-view-properties-path")).toHaveText("osdu.data.FacilityName");
    await expect(sheet.getByTestId("templates-view-properties-role")).toHaveCount(0);

    // Show required narrows the tree to what the schema demands, keeping the holders on the way to them.
    await sheet.getByTestId("templates-view-show-required").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.acl")).toBeVisible();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toHaveCount(0);
    await sheet.getByTestId("templates-view-show-required").click();
    await expect(sheet.getByTestId("templates-view-variable-osdu.data.FacilityName")).toBeVisible();

    await sheet.getByTestId("templates-view-tab-schema").click();
    await expect(sheet.getByTestId("templates-view-schema-json")).toBeVisible({ timeout: 15_000 });

    // The synced Wellbore mapping pins this version, so Delete sits beside the saved state disabled, and says why.
    await expect(sheet.getByTestId("templates-view-actions").getByTestId("templates-delete")).toBeDisabled();
    await sheet.getByTestId("templates-delete-blocked").hover();
    await expect(adminPage.getByText(/Pinned by 1 synced mapping\. Move it to another template version/).first()).toBeVisible();
    await expect(rows.filter({ hasText: WELLBORE_KIND })).toHaveCount(1);
  });

  test("a schema file is laid out as a template, and saving a version already saved changes nothing", async ({ adminPage }) => {
    await openTemplates(adminPage);
    await adminPage.getByTestId("templates-tab-import").click();

    const schema = readFileSync(join(import.meta.dirname, "..", "..", "samples", "templates", WELLBORE_FILE), "utf8");
    // The kind is asked for until the schema names its own, and then it is read from the schema.
    await expect(adminPage.getByTestId("templates-import-kind")).toBeVisible();
    await adminPage.getByTestId("templates-import-json").fill(schema);
    await expect(adminPage.getByTestId("templates-import-kind-declared")).toHaveText(WELLBORE_KIND);
    await expect(adminPage.getByTestId("templates-import-kind")).toHaveCount(0);
    await adminPage.getByTestId("templates-import-preview").click();

    // A version is the schema's content, so the same file lays out as the version the catalog already holds.
    const sheet = adminPage.getByTestId("templates-import-sheet");
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
    await sheet.getByTestId("templates-import-save").click();
    await expect(adminPage.getByText(/was already saved, so nothing changed/).first()).toBeVisible({ timeout: 15_000 });
  });

  test("a schema is saved straight from the import form, and opens as the saved version", async ({ adminPage }) => {
    await openTemplates(adminPage);
    await adminPage.getByTestId("templates-tab-import").click();

    const schema = readFileSync(join(import.meta.dirname, "..", "..", "samples", "templates", WELLBORE_FILE), "utf8");
    await adminPage.getByTestId("templates-import-json").fill(schema);
    await adminPage.getByTestId("templates-import-save-now").click();

    await expect(adminPage.getByText(/was already saved, so nothing changed/).first()).toBeVisible({ timeout: 15_000 });
    const sheet = adminPage.getByTestId("templates-import-sheet");
    await expect(sheet.getByTestId("templates-view-kind")).toHaveText(WELLBORE_KIND, { timeout: 15_000 });
    await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION);
    await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
  });

  test("the OSDU data definitions are browsed by release, and a kind opens as the template it saves as", async ({ adminPage }) => {
    const releases = /\/api\/v1\/delivery\/templates\/osdu\/releases$/;
    const schemas = /\/api\/v1\/delivery\/templates\/osdu\/schemas(\?|$)/;
    const schema = /\/api\/v1\/delivery\/templates\/osdu\/schema(\?|$)/;
    const wellboreSchema: unknown = JSON.parse(
      readFileSync(join(import.meta.dirname, "..", "..", "samples", "templates", WELLBORE_FILE), "utf8"),
    );
    const asked: string[] = [];
    const sync = /\/api\/v1\/delivery\/templates\/osdu\/sync$/;
    await adminPage.route(sync, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T11:00:00Z", releases: [RELEASE], downloaded: ["v0.30.0"] } }));
    await adminPage.route(releases, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T10:00:00Z", releases: [RELEASE, { ...RELEASE, name: "v0.29.1", commit: "0".repeat(40), local: false }] } }));
    await adminPage.route(schemas, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          release: RELEASE,
          schemas: [
            osduSchema("osdu:wks:master-data--Well:1.2.0", "master-data--Well", "1.2.0", "DEVELOPMENT", "master-data/Well.1.2.0.json"),
            osduSchema(WELLBORE_KIND, "master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"),
            osduSchema("osdu:wks:master-data--Wellbore:1.0.0", "master-data--Wellbore", "1.0.0", "PUBLISHED", "master-data/Wellbore.1.0.0.json"),
          ],
        },
      });
    });
    await adminPage.route(schema, (route) => {
      asked.push(route.request().url());
      return route.fulfill({
        json: {
          kind: WELLBORE_KIND,
          version: WELLBORE_VERSION,
          release: RELEASE,
          path: "master-data/Wellbore.1.3.0.json",
          webUrl: WELLBORE_URL,
          origin: "OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/master-data/Wellbore.1.3.0.json",
          schema: wellboreSchema,
        },
      });
    });

    try {
      await openTemplates(adminPage);
      await adminPage.getByTestId("templates-tab-browse").click();

      // The newest release is browsed straight away: there is no repository or connection to pick first.
      await expect(adminPage.getByTestId("templates-browse-release")).toContainText("v0.30.0", { timeout: 15_000 });
      await expect(adminPage.getByTestId("templates-browse-summary")).toContainText("2 record types");
      await expect(adminPage.getByTestId("templates-browse-repository-link")).toHaveAttribute("href", RELEASE.webUrl);

      // The local copy says when it last read the release list, and syncing reads it again and says what it downloaded.
      await expect(adminPage.getByTestId("templates-browse-synced")).toContainText("Synced");
      await adminPage.getByTestId("templates-browse-sync").click();
      await expect(adminPage.getByText(/Synced with the OSDU data definitions: 1 releases; downloaded v0\.30\.0/).first()).toBeVisible({ timeout: 15_000 });
      const rows = adminPage.getByTestId("templates-browse-results").getByTestId("table-row");
      await expect(rows).toHaveCount(2);
      await expect(adminPage.getByTestId("templates-browse-status-osdu:wks:master-data--Well:1.2.0")).toHaveText("In development");

      // One search box finds a kind by any part of it; each entity type shows its newest version unless every version is asked for.
      await adminPage.getByTestId("templates-browse-search").fill("wellbore");
      await expect(rows).toHaveCount(1);
      await expect(rows.first()).toContainText("2 versions");
      await expect(adminPage.getByTestId(`templates-browse-link-${WELLBORE_KIND}`)).toHaveAttribute("href", WELLBORE_URL);
      await adminPage.getByTestId("templates-browse-all-versions").click();
      await expect(rows).toHaveCount(2);

      // A kind opens laid out as the template it saves as; the seed saved this one, so saving it changes nothing.
      await adminPage.getByTestId(`templates-browse-view-${WELLBORE_KIND}`).click();
      const sheet = adminPage.getByTestId("templates-browse-sheet");
      await expect(sheet.getByTestId("templates-view-version")).toHaveText(WELLBORE_VERSION, { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-view-state")).toHaveText("saved");
      await expect(sheet.getByTestId("templates-browse-source-link")).toHaveAttribute("href", WELLBORE_URL);
      await sheet.getByTestId("templates-browse-save").click();
      await expect(adminPage.getByText(/was already saved, so nothing changed/).first()).toBeVisible({ timeout: 15_000 });

      expect(asked.length).toBeGreaterThan(1);
      expect(asked.every((url) => new URL(url).searchParams.get("release") === "v0.30.0")).toBe(true);
    } finally {
      await adminPage.unroute(sync);
      await adminPage.unroute(releases);
      await adminPage.unroute(schemas);
      await adminPage.unroute(schema);
    }
  });

  test("two versions of a kind compare with a verdict, every variable change and the files side by side", async ({ adminPage }) => {
    const releases = /\/api\/v1\/delivery\/templates\/osdu\/releases$/;
    const schemas = /\/api\/v1\/delivery\/templates\/osdu\/schemas(\?|$)/;
    const compare = /\/api\/v1\/delivery\/templates\/osdu\/compare(\?|$)/;
    const OLDER_KIND = "osdu:wks:master-data--Wellbore:1.0.0";
    const side = (kind: string, path: string, templateVersion: string, fileText: string) => ({
      kind, release: RELEASE, path, webUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/${path}`, status: "PUBLISHED", templateVersion, fileText,
    });
    const comparison = {
      from: side(OLDER_KIND, "master-data/Wellbore.1.0.0.json", "5694d203047e3aae", `{\n  "x-osdu-schema-source": "${OLDER_KIND}",\n  "Name": "string"\n}\n`),
      to: side(WELLBORE_KIND, "master-data/Wellbore.1.3.0.json", WELLBORE_VERSION, `{\n  "x-osdu-schema-source": "${WELLBORE_KIND}",\n  "FacilityName": "string"\n}\n`),
      sameFile: false,
      onlyIdentifiersDiffer: false,
      sameTemplate: false,
      breaking: 1,
      additive: 1,
      wording: 1,
      unchanged: 40,
      changes: [
        { path: "osdu.data.Name", change: "Removed", impact: "Breaking", role: "Mapping", fields: [{ field: "type", before: "string", after: null, impact: "Breaking" }] },
        { path: "osdu.data.FacilityName", change: "Added", impact: "Additive", role: "Mapping", fields: [{ field: "type", before: null, after: "string", impact: "Additive" }] },
        { path: "osdu.data.Depth", change: "Changed", impact: "Wording", role: "Mapping", fields: [{ field: "description", before: "Depth.", after: "Measured depth.", impact: "Wording" }] },
      ],
      sameReferencedFiles: 3,
      referencedFiles: [
        {
          name: "AbstractFacility",
          fromPath: "abstract/AbstractFacility.1.0.0.json",
          toPath: "abstract/AbstractFacility.1.1.0.json",
          fromWebUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.0.0.json`,
          toWebUrl: `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.1.0.json`,
          fromText: "{\n  \"FacilityName\": \"string\"\n}\n",
          toText: "{\n  \"FacilityName\": \"string\",\n  \"FacilityOperators\": \"array\"\n}\n",
        },
      ],
    };
    const compared: URL[] = [];
    await adminPage.route(releases, (route) => route.fulfill({ json: { syncedUtc: "2026-09-13T10:00:00Z", releases: [RELEASE, { ...RELEASE, name: "v0.29.1", commit: "0".repeat(40), local: false }] } }));
    await adminPage.route(schemas, (route) => route.fulfill({
      json: {
        release: RELEASE,
        schemas: [
          osduSchema(WELLBORE_KIND, "master-data--Wellbore", "1.3.0", "PUBLISHED", "master-data/Wellbore.1.3.0.json"),
          osduSchema(OLDER_KIND, "master-data--Wellbore", "1.0.0", "PUBLISHED", "master-data/Wellbore.1.0.0.json"),
        ],
      },
    }));
    await adminPage.route(compare, (route) => {
      compared.push(new URL(route.request().url()));
      return route.fulfill({ json: comparison });
    });

    try {
      await openTemplates(adminPage);
      await adminPage.getByTestId("templates-tab-browse").click();
      await expect(adminPage.getByTestId("templates-browse-summary")).toContainText("1 record type", { timeout: 15_000 });

      // Compare opens on the version before this one in the same release.
      await adminPage.getByTestId(`templates-browse-compare-${WELLBORE_KIND}`).click();
      const sheet = adminPage.getByTestId("templates-compare-sheet");
      await expect(sheet.getByTestId("templates-compare-from-version")).toContainText("1.0.0", { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-compare-to-version")).toContainText("1.3.0");

      // Each picker says whether it is the release or the schema version, and a line says what the two sides are, so
      // two versions of one release are never read as the same schema.
      await expect(sheet.getByText("From release", { exact: true })).toBeVisible();
      await expect(sheet.getByText("From schema version", { exact: true })).toBeVisible();
      // The release the compare opened on is whichever the data definitions publish newest, so only its shape is asserted.
      await expect(sheet.getByTestId("templates-compare-comparing"))
        .toHaveText(/^Comparing schema versions 1\.0\.0 and 1\.3\.0, both from release v[\d.]+\.$/);

      // The verdict first, then every variable that differs, with what it means for a mapping of the older version.
      await expect(sheet.getByTestId("templates-compare-count-breaking")).toHaveText("1 breaking", { timeout: 15_000 });
      await expect(sheet.getByTestId("templates-compare-count-additive")).toHaveText("1 additive");
      await expect(sheet.getByTestId("templates-compare-count-wording")).toHaveText("1 wording");
      // The list opens with the impact headings open and every variable folded; a variable opens onto its fields.
      const changeRows = sheet.getByTestId("templates-compare-change-row");
      await expect(changeRows).toHaveCount(3);
      await expect(sheet.getByTestId("templates-compare-group-breaking").getByTestId("templates-compare-change-osdu.data.Name")).toHaveText("Removed");
      await expect(sheet.getByTestId("templates-compare-field-osdu.data.Name-type")).toHaveCount(0);
      await sheet.getByTestId("templates-compare-change-toggle-osdu.data.Name").click();
      await expect(sheet.getByTestId("templates-compare-field-osdu.data.Name-type")).toBeVisible();
      await sheet.getByTestId("templates-compare-group-toggle-breaking").click();
      await expect(changeRows).toHaveCount(2);
      await sheet.getByTestId("templates-compare-expand-all").click();
      await expect(sheet.getByTestId("templates-compare-expand-all")).toHaveText("Collapse all");
      await expect(sheet.getByTestId("templates-compare-group-additive").getByTestId("templates-compare-change-osdu.data.FacilityName")).toHaveText("Added");

      // What changed is marked in place: the words a description lost, and the words it gained.
      const depthDescription = sheet.getByTestId("templates-compare-field-osdu.data.Depth-description");
      await expect(depthDescription.locator("del")).toHaveText("Depth");
      await expect(depthDescription.locator("ins")).toHaveText("Measured depth");

      // The impact filter narrows the list to one impact, counting what each holds.
      await expect(changeRows).toHaveCount(3);
      await expect(sheet.getByTestId("templates-compare-impact-filter-wording")).toContainText("1");
      await sheet.getByTestId("templates-compare-impact-filter-breaking").click();
      await expect(changeRows).toHaveCount(1);
      await expect(sheet.getByTestId("templates-compare-group-wording")).toHaveCount(0);
      await sheet.getByTestId("templates-compare-impact-filter-all").click();
      await expect(changeRows).toHaveCount(3);

      // The files as the releases publish them, side by side.
      await sheet.getByTestId("templates-compare-tab-files").click();
      await expect(sheet.getByTestId("templates-compare-files")).toBeVisible();
      await expect(sheet.getByTestId("templates-compare-files-original")).toContainText(OLDER_KIND);
      await expect(sheet.getByTestId("templates-compare-to-link")).toHaveAttribute("href", `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json`);

      // The shared schemas the versions refer to that differ are there to compare too.
      await expect(sheet.getByTestId("templates-compare-referenced-summary")).toHaveText("1 shared schema it refers to differs; 3 are the same.");
      await sheet.getByTestId("templates-compare-file-picker").click();
      await adminPage.getByRole("option").filter({ hasText: "AbstractFacility" }).click();
      await expect(sheet.getByTestId("templates-compare-files-original")).toContainText("abstract/AbstractFacility.1.0.0.json");
      await expect(sheet.getByTestId("templates-compare-to-link")).toHaveAttribute("href", `${DATA_DEFINITIONS}/-/blob/v0.30.0/Generated/abstract/AbstractFacility.1.1.0.json`);

      const first = compared[0];
      expect([first.searchParams.get("fromKind"), first.searchParams.get("toKind")]).toEqual([OLDER_KIND, WELLBORE_KIND]);
      expect([first.searchParams.get("fromRelease"), first.searchParams.get("toRelease")]).toEqual(["v0.30.0", "v0.30.0"]);

      // Swapping compares the other way round.
      await sheet.getByTestId("templates-compare-swap").click();
      await expect.poll(() => compared.some((url) => url.searchParams.get("fromKind") === WELLBORE_KIND && url.searchParams.get("toKind") === OLDER_KIND)).toBe(true);
    } finally {
      await adminPage.unroute(releases);
      await adminPage.unroute(schemas);
      await adminPage.unroute(compare);
    }
  });

  test("the builder starts a mapping from a saved template, with what the cache holds prefilled", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-mapping-builder").click();
    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-empty")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-repo").click();
    await adminPage.getByRole("option").filter({ hasText: "e2e-repo" }).first().click();
    // The repository's delivery flow delivers to partition dev, so the builder reads that partition's cache without being told.
    await expect(adminPage.getByTestId("mapping-builder-cache")).toContainText("dev", { timeout: 15_000 });
    await expect(adminPage.getByTestId("mapping-builder-cache-note")).toContainText(/holds \d+ types? at version /, { timeout: 15_000 });

    await adminPage.getByTestId("mapping-builder-template").click();
    await adminPage.getByRole("option").filter({ hasText: WELLLOG_KIND }).first().click();
    // The name starts as the template's entity; this draft takes its own, so it is never taken for the synced mapping.
    await expect(adminPage.getByTestId("mapping-builder-name")).toHaveValue("WellLog");
    await adminPage.getByTestId("mapping-builder-name").fill("WellLogDraft");
    await adminPage.getByTestId("mapping-builder-system").fill("wells");
    await adminPage.getByTestId("mapping-builder-start").click();

    // A well log points at its wellbore, which the cache holds, so that entry starts filled from the cache.
    const wellbore = rowWith(adminPage, "mapping-builder-variables", "mapping-builder-variable-osdu.data.WellboreID");
    await expect(wellbore).toBeVisible({ timeout: 15_000 });
    await expect(wellbore.getByTestId("mapping-builder-prefilled")).toBeVisible();

    // The check writes the YAML and refuses the draft as it stands: it has no key, and the cache entry finds by no column.
    await expect(adminPage.getByTestId("mapping-builder-invalid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-issue").first()).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-yaml")).toContainText(WELLLOG_VERSION);
    await expect(adminPage.getByTestId("mapping-builder-propose-blocked")).toBeVisible();

    await adminPage.getByTestId("mapping-builder-key-input").fill("log_id");
    await adminPage.getByTestId("mapping-builder-key-add").click();
    await expect(adminPage.getByTestId("mapping-builder-key-chip-log_id")).toBeVisible();
  });

  test("a synced mapping opens in the builder and passes the check against the cache its flow reads", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-delivery-documents").click();
    await expect(adminPage.getByTestId("page-delivery-documents")).toBeVisible();
    const row = adminPage.getByTestId("delivery-mappings-table").getByTestId("table-row").filter({ hasText: "WellLog@1.4.0" }).first();
    await expect(row).toContainText(WELLLOG_VERSION, { timeout: 30_000 });
    await row.click();

    const detail = adminPage.getByTestId("delivery-mapping-detail");
    await expect(detail.getByTestId("delivery-mapping-template")).toContainText(WELLLOG_VERSION, { timeout: 15_000 });

    // The properties open first: the template as the record's tree, with what this mapping fills of it. Every row says
    // how the mapping reaches its variable, and the tree opens on what is filled and what a check names.
    const properties = detail.getByTestId("delivery-mapping-coverage");
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveAttribute("data-coverage", "Always", { timeout: 30_000 });
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Name")).toHaveAttribute("data-coverage", "Always");
    // A curve's business value is the one entry the mapping leaves optional, so a log without it still delivers.
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Curves[].LogCurveBusinessValueID"))
      .toHaveAttribute("data-coverage", "Sometimes");
    // An entry filling a free key of an object that takes them is a row under that object.
    await expect(properties.getByTestId("templates-view-coverage-osdu.tags.DeliveredBy")).toHaveAttribute("data-coverage", "Always");

    // What fills a variable is read beside the tree, whole: the origin, every line the lookup tries, and the modifiers
    // in order. A cache entry's modifiers change the value the lookup compares, which is the one thing a reader gets wrong.
    await properties.getByTestId("templates-view-variable-osdu.data.WellboreID").click();
    const entry = properties.getByTestId("templates-view-properties-entry");
    await expect(entry.getByTestId("delivery-mapping-property-detail-source")).toContainText("cache.Wellbore.id");
    await expect(entry.getByTestId("delivery-mapping-property-detail-lookup"))
      .toContainText("cache.Wellbore.FacilityName = dataset.wellbore_uwi");
    await properties.getByTestId("templates-view-variable-osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID").click();
    const modifiers = entry.getByTestId("delivery-mapping-property-detail-modifiers");
    await expect(modifiers).toContainText("split on ' ', part 2");
    await expect(modifiers).toContainText("replace M to m, FT to ft");
    await expect(modifiers).toContainText("the value the lookup compares");

    // The search reads what fills a variable too, so a source column answers with every variable it reaches: the
    // vertical measurement, and the unit that measurement is found by.
    await properties.getByTestId("templates-view-variables-filter").fill("elev_meas_ref");
    await expect(properties.getByTestId("templates-view-variable-osdu.data.VerticalMeasurement.VerticalMeasurement")).toBeVisible();
    await expect(properties.getByTestId("templates-view-variables-count")).toContainText("2 of");
    await expect(properties.getByTestId("templates-view-variable-osdu.data.Name")).toHaveCount(0);
    await properties.getByTestId("templates-view-variables-filter-clear").click();

    // The overview leaves out what the mapping does not fill and the schema does not require; the switch adds it back.
    await expect(properties.getByTestId("templates-view-variable-osdu.data.ResourceHomeRegionID")).toHaveCount(0);
    await properties.getByTestId("templates-view-show-everything").click();
    await expect(properties.getByTestId("templates-view-variable-osdu.data")).toBeVisible();
    await properties.getByTestId("templates-view-show-everything").click();

    // Show missing answers whether the mapping satisfies the schema: the sample fills everything this record requires.
    await properties.getByTestId("templates-view-show-missing").click();
    await expect(properties.getByTestId("templates-view-variables-empty")).toContainText("Nothing the schema requires");
    await properties.getByTestId("templates-view-show-missing").click();

    // Narrowing to what nothing fills keeps the holders on the way, and drops what the mapping fills, an entry that
    // may leave its value out included: the mapping fills that variable either way.
    await properties.getByTestId("templates-view-show-gaps").click();
    await expect(properties.getByTestId("templates-view-variable-osdu.data.ResourceHomeRegionID")).toBeVisible();
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveCount(0);
    await expect(properties.getByTestId("templates-view-coverage-osdu.data.Curves[].LogCurveBusinessValueID")).toHaveCount(0);
    await properties.getByTestId("templates-view-show-gaps").click();
    await expect(properties.getByTestId("templates-view-coverage-osdu.acl")).toHaveAttribute("data-coverage", "Always");

    // The record shape: the renderer's layout with placeholders, and the partition filled into the id once it is given.
    await detail.getByTestId("delivery-mapping-tab-shape").click();
    const shape = detail.getByTestId("delivery-mapping-shape-json");
    // The editor draws only the lines in view, so the check reads the id on the first lines rather than a deeper field.
    await expect(shape).toContainText("<delivery key from wells", { timeout: 15_000 });
    await detail.getByTestId("delivery-mapping-shape-parameter-dataPartition").fill("dev");
    await expect(shape).toContainText("dev:work-product-component--WellLog:", { timeout: 15_000 });

    await detail.getByTestId("delivery-mapping-open-builder").click();

    await expect(adminPage.getByTestId("page-delivery-mapping-builder")).toBeVisible();
    await expect(adminPage.getByTestId("mapping-builder-opened-from")).toContainText("mappings/WellLog@1.4.0.yaml", { timeout: 30_000 });
    // The sample mapping renders its fixtures against the imported cache exactly, so it loads, passes and can be proposed.
    await expect(adminPage.getByTestId("mapping-builder-valid")).toBeVisible({ timeout: 30_000 });
    await expect(adminPage.getByTestId("mapping-builder-propose")).toBeEnabled();
  });
});
