import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";

// Lineage over the synced estate: every OSDU flow shows with its data on both sides. The delivery flows write the OSDU
// types their mappings fill and read the partition cache types their mappings resolve against; the cache flow reads
// OSDU types and writes those cache types. The catalog explorer lists them as datasets, a dataset's page names the
// pipelines on either side of it, and the lineage graph draws it captioned by its system.

interface DatasetNode {
  key: string;
  system: string;
  namespace: string;
  group: string;
  name: string;
  readers: number;
  writers: number;
}

const WELL_LOG = "osdu:wks:work-product-component--WellLog:1.4.0";

const WELLBORE = "osdu:wks:master-data--Wellbore:1.3.0";

test.describe.serial("lineage", () => {
  test("the synced estate lists the OSDU types and cache types its flows write and read", async ({ request }) => {
    const session = await adminSession(request);
    const response = await request.get(`${E2E.apiBaseUrl}/api/v1/lineage/datasets`, {
      headers: { Authorization: `Bearer ${session.token}` },
    });
    expect(response.ok(), `GET /lineage/datasets answered ${response.status()}`).toBeTruthy();
    const datasets = (await response.json()) as DatasetNode[];
    const named = (system: string, name: string) =>
      datasets.find((d) => d.system === system && d.name.toLowerCase() === name.toLowerCase());

    const wellLog = named("osdu-type", WELL_LOG);
    expect(wellLog, `no ${WELL_LOG} among ${datasets.map((d) => d.name).join(", ")}`).toBeDefined();
    // Two flows deliver well logs: wells-welllog-03-header-delivery in the single form, and the welllogs interface of wells-source-03-interfaces-delivery.
    expect([wellLog!.namespace, wellLog!.group, wellLog!.writers]).toEqual(["opendes", "work-product-component", 2]);

    const wellbore = named("osdu-type", WELLBORE);
    expect(wellbore).toBeDefined();
    // wells-wellbore-03-header-delivery and the wellbores interface of wells-source-03-interfaces-delivery write it; the cache flow and the download
    // read it back through their wildcard kinds.
    expect(wellbore!.writers).toBe(2);
    expect(wellbore!.readers).toBeGreaterThanOrEqual(2);

    const units = named("wells-osdu-00-reference-cache", "UnitOfMeasure");
    expect(units).toBeDefined();
    expect([units!.namespace, units!.group, units!.writers]).toEqual(["opendes", "cache", 1]);
    expect(units!.readers).toBeGreaterThanOrEqual(1);
  });

  test("an OSDU type opens with the pipeline that writes it, and jumps to the graph", async ({ adminPage }) => {
    await adminPage.goto("/catalog");
    await adminPage.getByTestId("catalog-filter").fill("WellLog:1.4.0");
    const matches = adminPage.getByTestId("catalog-object-matches");
    await matches.getByRole("button", { name: /work-product-component--WellLog:1\.4\.0/ }).first().click();

    const details = adminPage.getByTestId("catalog-object-details");
    await expect(details.getByText("Dataset", { exact: true })).toBeVisible();
    await expect(details.getByText("osdu type", { exact: true })).toBeVisible();
    await details.getByTestId("catalog-tab-pipelines").click();
    const provenance = adminPage.getByTestId("catalog-file-provenance");
    await expect(provenance.getByText("wells-welllog-03-header-delivery", { exact: true })).toBeVisible({ timeout: 30_000 });

    await details.getByTestId("search-open-graph").click();
    await adminPage.getByTestId("lineage-jump-option").first().click();
    const graph = adminPage.getByTestId("page-lineage-graph");
    await expect(graph).toBeVisible();
    await expect(graph.getByText(WELL_LOG, { exact: true }).first()).toBeVisible({ timeout: 30_000 });
    await expect(graph.getByText(/^osdu type · opendes\.work-product-component/).first()).toBeVisible();
    await expect(graph.getByText("wells-welllog-03-header-delivery", { exact: true }).first()).toBeVisible();
  });

  test("the catalog tree groups datasets by system, partition and group", async ({ adminPage }) => {
    await adminPage.goto("/catalog");
    const tree = adminPage.getByRole("tree", { name: "Catalog tree" });
    await tree.getByRole("treeitem", { name: /^Datasets/ }).click();
    await tree.getByRole("treeitem", { name: /^osdu cache/ }).click();
    await tree.getByRole("treeitem", { name: /^opendes/ }).first().click();
    await tree.getByRole("treeitem", { name: /^cache/ }).click();
    await expect(tree.getByRole("treeitem", { name: /^UnitOfMeasure/ })).toBeVisible();
  });
});
